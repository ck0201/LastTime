using System.Net;
using System.Net.Http.Json;
using LastTime.Api.Features.History;
using LastTime.Api.Features.Items;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class HistoryAuthTests(ApiFactory factory) : IAsyncLifetime
{
    private static readonly HistoryEntryRequest Request = new(new DateOnly(2026, 8, 12), "Serviced", null, null, null);

    private HttpClient _anonymous = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;
    private ItemResponse _bobsItem = null!;
    private HistoryEntryResponse _bobsEntry = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _anonymous = factory.CreateClient();
        (_alice, _) = await factory.CreateAuthenticatedClientAsync("alice@example.com");
        (_bob, _) = await factory.CreateAuthenticatedClientAsync("bob@example.com");

        _bobsItem = await CreateItemAsync(_bob, "Bob's AC");
        _bobsEntry = await AddAsync(_bob, _bobsItem.Id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<ItemResponse> CreateItemAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/items", new ItemRequest(name, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ItemResponse>())!;
    }

    private static async Task<HistoryEntryResponse> AddAsync(HttpClient client, Guid itemId)
    {
        var response = await client.PostAsJsonAsync($"/api/items/{itemId}/history", Request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HistoryEntryResponse>())!;
    }

    private string EntryUrl => $"/api/items/{_bobsItem.Id}/history/{_bobsEntry.Id}";

    [Fact]
    public async Task Unauthenticated_AllHistoryEndpoints_Return401()
    {
        var history = $"/api/items/{_bobsItem.Id}/history";

        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync($"{history}/last")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PostAsJsonAsync(history, Request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PutAsJsonAsync(EntryUrl, Request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.DeleteAsync(EntryUrl)).StatusCode);
    }

    [Fact]
    public async Task OtherUsersHistory_CannotBeRead_ViaLastOrItemDetail()
    {
        var last = await _alice.GetAsync($"/api/items/{_bobsItem.Id}/history/last");
        var detail = await _alice.GetAsync($"/api/items/{_bobsItem.Id}");

        Assert.Equal(HttpStatusCode.NotFound, last.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task OtherUsersItem_CannotReceiveNewHistory()
    {
        var response = await _alice.PostAsJsonAsync($"/api/items/{_bobsItem.Id}/history", Request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var detail = await _bob.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{_bobsItem.Id}");
        Assert.Single(detail!.History);
    }

    [Fact]
    public async Task OtherUsersEntry_CannotBeUpdated()
    {
        var changed = Request with { Action = "Hijacked" };

        var response = await _alice.PutAsJsonAsync(EntryUrl, changed);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var detail = await _bob.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{_bobsItem.Id}");
        Assert.Equal("Serviced", Assert.Single(detail!.History).Action);
    }

    [Fact]
    public async Task OtherUsersEntry_CannotBeDeleted()
    {
        var response = await _alice.DeleteAsync(EntryUrl);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var detail = await _bob.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{_bobsItem.Id}");
        Assert.Single(detail!.History);
    }

    [Fact]
    public async Task EntryCannotBeReachedThroughAnotherOfTheCallersOwnItems()
    {
        // Alice owns an item, but Bob's entry id must not resolve under it (or under Bob's item for her).
        var alicesItem = await CreateItemAsync(_alice, "Alice's AC");

        var viaOwnItem = await _alice.DeleteAsync($"/api/items/{alicesItem.Id}/history/{_bobsEntry.Id}");

        Assert.Equal(HttpStatusCode.NotFound, viaOwnItem.StatusCode);
        var detail = await _bob.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{_bobsItem.Id}");
        Assert.Single(detail!.History);
    }

    [Fact]
    public async Task Owner_CanStillReadCreateUpdateAndDelete()
    {
        var last = await _bob.GetFromJsonAsync<HistoryEntryResponse>($"/api/items/{_bobsItem.Id}/history/last");
        Assert.Equal(_bobsEntry.Id, last!.Id);

        var second = await AddAsync(_bob, _bobsItem.Id);

        var update = await _bob.PutAsJsonAsync($"/api/items/{_bobsItem.Id}/history/{second.Id}", Request with { Action = "Repaired" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Repaired", (await update.Content.ReadFromJsonAsync<HistoryEntryResponse>())!.Action);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _bob.DeleteAsync($"/api/items/{_bobsItem.Id}/history/{second.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _bob.DeleteAsync(EntryUrl)).StatusCode);
    }
}
