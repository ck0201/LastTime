using System.Net;
using System.Net.Http.Json;
using LastTime.Api.Features.Items;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class ItemsApiTests(ApiFactory factory) : IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ItemResponse> CreateAsync(string name, string? description = null)
    {
        var response = await _client.PostAsJsonAsync("/api/items", new ItemRequest(name, description));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ItemResponse>())!;
    }

    [Fact]
    public async Task Create_ReturnsCreated_WithTrimmedNameAndLocation()
    {
        var response = await _client.PostAsJsonAsync("/api/items", new ItemRequest("  LG AC  ", "  Bedroom  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = (await response.Content.ReadFromJsonAsync<ItemResponse>())!;
        Assert.Equal("LG AC", item.Name);
        Assert.Equal("Bedroom", item.Description);
        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.EndsWith($"/api/items/{item.Id}", response.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_BlankName_ReturnsBadRequest(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/items", new ItemRequest(name, null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_NameTooLong_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/api/items", new ItemRequest(new string('x', 201), null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_BlankDescription_IsStoredAsNull()
    {
        var item = await CreateAsync("Laptop", "   ");
        Assert.Null(item.Description);
    }

    [Fact]
    public async Task GetById_ReturnsItem_Or404()
    {
        var created = await CreateAsync("Bike");

        var found = await _client.GetFromJsonAsync<ItemResponse>($"/api/items/{created.Id}");
        Assert.Equal("Bike", found!.Name);

        var missing = await _client.GetAsync($"/api/items/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Search_IsCaseInsensitive_MatchesNameAndDescription_AndSortsByName()
    {
        await CreateAsync("LG AC", "Bedroom");
        await CreateAsync("Laptop", "Work computer");
        await CreateAsync("Bike", "ac unit nearby");

        var byName = await _client.GetFromJsonAsync<List<ItemResponse>>("/api/items?search=ac");
        Assert.Equal(["Bike", "LG AC"], byName!.Select(i => i.Name));

        var all = await _client.GetFromJsonAsync<List<ItemResponse>>("/api/items");
        Assert.Equal(["Bike", "Laptop", "LG AC"], all!.Select(i => i.Name));
    }

    [Fact]
    public async Task Search_TreatsLikeWildcardsLiterally()
    {
        await CreateAsync("100% Cotton Shirt");
        await CreateAsync("Laptop");

        var percent = await _client.GetFromJsonAsync<List<ItemResponse>>("/api/items?search=%25");
        Assert.Equal(["100% Cotton Shirt"], percent!.Select(i => i.Name));

        var underscore = await _client.GetFromJsonAsync<List<ItemResponse>>("/api/items?search=_");
        Assert.Empty(underscore!);
    }

    [Fact]
    public async Task Update_ChangesFields_Or404()
    {
        var created = await CreateAsync("Old name", "old");

        var response = await _client.PutAsJsonAsync($"/api/items/{created.Id}", new ItemRequest("New name", null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<ItemResponse>())!;
        Assert.Equal("New name", updated.Name);
        Assert.Null(updated.Description);

        var missing = await _client.PutAsJsonAsync($"/api/items/{Guid.NewGuid()}", new ItemRequest("x", null));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesItem_Then404()
    {
        var created = await CreateAsync("Temp");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/items/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/items/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/items/{created.Id}")).StatusCode);
    }
}


