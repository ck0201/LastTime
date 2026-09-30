using System.Net;
using System.Net.Http.Json;
using LastTime.Api.Data;
using LastTime.Api.Features.Items;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class ItemsAuthTests(ApiFactory factory) : IAsyncLifetime
{
    private HttpClient _anonymous = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;
    private Guid _aliceId;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _anonymous = factory.CreateClient();
        (_alice, _aliceId) = await factory.CreateAuthenticatedClientAsync("alice@example.com");
        (_bob, _) = await factory.CreateAuthenticatedClientAsync("bob@example.com");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<ItemResponse> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/items", new ItemRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ItemResponse>())!;
    }

    private async Task<Guid> OwnerOfAsync(Guid itemId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LastTimeDbContext>();
        return await db.Items.Where(i => i.Id == itemId).Select(i => i.UserId).SingleAsync();
    }

    [Fact]
    public async Task Unauthenticated_AllItemEndpoints_Return401()
    {
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync("/api/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync($"/api/items/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _anonymous.PostAsJsonAsync("/api/items", new ItemRequest("x", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _anonymous.PutAsJsonAsync($"/api/items/{id}", new ItemRequest("x", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.DeleteAsync($"/api/items/{id}")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Requests_DoNotChangeData()
    {
        var item = await CreateAsync(_alice, "Keep me");

        await _anonymous.PostAsJsonAsync("/api/items", new ItemRequest("Sneaky", null));
        await _anonymous.PutAsJsonAsync($"/api/items/{item.Id}", new ItemRequest("Changed", null));
        await _anonymous.DeleteAsync($"/api/items/{item.Id}");

        var items = await _alice.GetFromJsonAsync<List<ItemSummaryResponse>>("/api/items");
        Assert.Equal(["Keep me"], items!.Select(i => i.Name));
    }

    [Fact]
    public async Task List_ReturnsOnlyTheCallersItems()
    {
        await CreateAsync(_alice, "Alice AC");
        await CreateAsync(_bob, "Bob Bike");

        var alice = await _alice.GetFromJsonAsync<List<ItemSummaryResponse>>("/api/items");
        var bob = await _bob.GetFromJsonAsync<List<ItemSummaryResponse>>("/api/items");

        Assert.Equal(["Alice AC"], alice!.Select(i => i.Name));
        Assert.Equal(["Bob Bike"], bob!.Select(i => i.Name));
    }

    [Fact]
    public async Task Search_DoesNotReturnOtherUsersMatches()
    {
        await CreateAsync(_bob, "Bob AC");

        var alice = await _alice.GetFromJsonAsync<List<ItemSummaryResponse>>("/api/items?search=ac");

        Assert.Empty(alice!);
    }

    [Fact]
    public async Task Create_AssignsAuthenticatedUserAsOwner()
    {
        var item = await CreateAsync(_alice, "Laptop");

        Assert.Equal(_aliceId, await OwnerOfAsync(item.Id));
    }

    [Fact]
    public async Task Create_IgnoresClientProvidedUserId()
    {
        var bobId = Guid.NewGuid();
        var response = await _alice.PostAsJsonAsync("/api/items", new { name = "Laptop", userId = bobId });

        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<ItemResponse>())!;
        Assert.Equal(_aliceId, await OwnerOfAsync(item.Id));
    }

    [Fact]
    public async Task Update_OwnItem_Works_AndIgnoresClientProvidedUserId()
    {
        var item = await CreateAsync(_alice, "Old");

        var response = await _alice.PutAsJsonAsync($"/api/items/{item.Id}", new { name = "New", userId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("New", (await response.Content.ReadFromJsonAsync<ItemResponse>())!.Name);
        Assert.Equal(_aliceId, await OwnerOfAsync(item.Id));
    }

    [Fact]
    public async Task Update_OtherUsersItem_Returns404_AndLeavesItUnchanged()
    {
        var bobsItem = await CreateAsync(_bob, "Bob's");

        var response = await _alice.PutAsJsonAsync($"/api/items/{bobsItem.Id}", new ItemRequest("Hijacked", null));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var stillBobs = await _bob.GetFromJsonAsync<ItemResponse>($"/api/items/{bobsItem.Id}");
        Assert.Equal("Bob's", stillBobs!.Name);
    }

    [Fact]
    public async Task Delete_OwnItem_Works()
    {
        var item = await CreateAsync(_alice, "Temp");

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync($"/api/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _alice.GetAsync($"/api/items/{item.Id}")).StatusCode);
    }

    [Fact]
    public async Task Delete_OtherUsersItem_Returns404_AndItemSurvives()
    {
        var bobsItem = await CreateAsync(_bob, "Bob's");

        Assert.Equal(HttpStatusCode.NotFound, (await _alice.DeleteAsync($"/api/items/{bobsItem.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _bob.GetAsync($"/api/items/{bobsItem.Id}")).StatusCode);
    }

    [Fact]
    public async Task GetById_OtherUsersItem_LooksLikeItDoesNotExist()
    {
        var bobsItem = await CreateAsync(_bob, "Bob's");

        var other = await _alice.GetAsync($"/api/items/{bobsItem.Id}");
        var missing = await _alice.GetAsync($"/api/items/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(missing.StatusCode, other.StatusCode);
    }
}
