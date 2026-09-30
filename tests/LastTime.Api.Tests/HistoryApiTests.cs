using System.Net;
using System.Net.Http.Json;
using LastTime.Api.Features.History;
using LastTime.Api.Features.Items;

namespace LastTime.Api.Tests;

[Collection(ApiCollection.Name)]
public class HistoryApiTests(ApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        (_client, _) = await factory.CreateAuthenticatedClientAsync("owner@example.com");
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<ItemResponse> CreateItemAsync(string name = "LG AC")
    {
        var response = await _client.PostAsJsonAsync("/api/items", new ItemRequest(name, null));
        return (await response.Content.ReadFromJsonAsync<ItemResponse>())!;
    }

    private async Task<HistoryEntryResponse> AddAsync(
        Guid itemId, string date, string action, decimal? cost = null, string? provider = null, string? notes = null)
    {
        var request = new HistoryEntryRequest(DateOnly.Parse(date), action, cost, provider, notes);
        var response = await _client.PostAsJsonAsync($"/api/items/{itemId}/history", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HistoryEntryResponse>())!;
    }

    [Fact]
    public async Task Create_StoresAllFields_AndTrimsText()
    {
        var item = await CreateItemAsync();

        var entry = await AddAsync(item.Id, "2026-08-12", "  Serviced ", 850.50m, " CoolCare Services ", " Gas checked ");

        Assert.Equal(item.Id, entry.ItemId);
        Assert.Equal(new DateOnly(2026, 8, 12), entry.Date);
        Assert.Equal("Serviced", entry.Action);
        Assert.Equal(850.50m, entry.Cost);
        Assert.Equal("CoolCare Services", entry.Provider);
        Assert.Equal("Gas checked", entry.Notes);
    }

    [Fact]
    public async Task Create_OptionalFieldsCanBeOmitted_AndBlankBecomesNull()
    {
        var item = await CreateItemAsync();

        var entry = await AddAsync(item.Id, "2024-03-14", "Purchased", provider: "  ", notes: "");

        Assert.Null(entry.Cost);
        Assert.Null(entry.Provider);
        Assert.Null(entry.Notes);
    }

    [Fact]
    public async Task Create_UnknownItem_Returns404()
    {
        var request = new HistoryEntryRequest(new DateOnly(2026, 1, 1), "Serviced", null, null, null);
        var response = await _client.PostAsJsonAsync($"/api/items/{Guid.NewGuid()}/history", request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Create_InvalidInput_Returns400()
    {
        var item = await CreateItemAsync();
        var url = $"/api/items/{item.Id}/history";
        var today = new DateOnly(2026, 1, 1);

        var bad = new HistoryEntryRequest[]
        {
            new(null, "Serviced", null, null, null),                       // date required
            new(today, "   ", null, null, null),                           // action required
            new(today, new string('x', 101), null, null, null),            // action too long
            new(today, "Serviced", -1m, null, null),                       // negative cost
            new(today, "Serviced", null, new string('x', 201), null),      // provider too long
            new(today, "Serviced", null, null, new string('x', 2001)),     // notes too long
        };

        foreach (var request in bad)
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync(url, request)).StatusCode);

        // A body with no date at all must not silently become 0001-01-01.
        var noDate = await _client.PostAsJsonAsync(url, new { action = "Serviced" });
        Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);
    }

    [Fact]
    public async Task ItemDetail_ReturnsTimelineNewestFirst_WithTiesByRecordedOrder()
    {
        var item = await CreateItemAsync();
        await AddAsync(item.Id, "2024-03-14", "Purchased", 38999m);
        await AddAsync(item.Id, "2026-08-12", "Serviced", 850m, "CoolCare Services", "Gas checked");
        await AddAsync(item.Id, "2026-02-03", "Repaired", 1200m, "CoolCare Services");
        await AddAsync(item.Id, "2026-02-03", "Inspected");   // same date, recorded later => first

        var detail = await _client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}");

        Assert.Equal("LG AC", detail!.Name);
        Assert.Equal(["Serviced", "Inspected", "Repaired", "Purchased"], detail.History.Select(h => h.Action));
    }

    [Fact]
    public async Task ItemDetail_WithNoHistory_ReturnsEmptyList()
    {
        var item = await CreateItemAsync();
        var detail = await _client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}");
        Assert.Empty(detail!.History);
    }

    [Fact]
    public async Task Last_WithoutAction_ReturnsNewestEntry()
    {
        var item = await CreateItemAsync();
        await AddAsync(item.Id, "2026-08-12", "Serviced");
        await AddAsync(item.Id, "2026-09-01", "Repaired");
        await AddAsync(item.Id, "2025-01-01", "Purchased");

        var last = await _client.GetFromJsonAsync<HistoryEntryResponse>($"/api/items/{item.Id}/history/last");

        Assert.Equal("Repaired", last!.Action);
    }

    [Fact]
    public async Task Last_WithAction_MatchesCaseInsensitively_AndBySubstring()
    {
        var item = await CreateItemAsync();
        await AddAsync(item.Id, "2025-05-01", "Serviced");
        await AddAsync(item.Id, "2026-08-12", "AC Service - gas refill", 850m);
        await AddAsync(item.Id, "2026-09-01", "Repaired");

        var last = await _client.GetFromJsonAsync<HistoryEntryResponse>($"/api/items/{item.Id}/history/last?action=SERVICE");

        Assert.Equal("AC Service - gas refill", last!.Action);
        Assert.Equal(850m, last.Cost);
    }

    [Fact]
    public async Task Last_SameDate_ReturnsMostRecentlyRecorded()
    {
        var item = await CreateItemAsync();
        await AddAsync(item.Id, "2026-08-12", "Serviced", notes: "first");
        await AddAsync(item.Id, "2026-08-12", "Serviced", notes: "second");

        var last = await _client.GetFromJsonAsync<HistoryEntryResponse>($"/api/items/{item.Id}/history/last?action=serviced");

        Assert.Equal("second", last!.Notes);
    }

    [Fact]
    public async Task Last_ActionWildcardsAreLiteral()
    {
        var item = await CreateItemAsync();
        await AddAsync(item.Id, "2026-08-12", "Serviced");

        var response = await _client.GetAsync($"/api/items/{item.Id}/history/last?action=%25");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Last_NoMatch_Or_NoHistory_Or_UnknownItem_Returns404()
    {
        var item = await CreateItemAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/items/{item.Id}/history/last")).StatusCode);

        await AddAsync(item.Id, "2026-08-12", "Serviced");
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/items/{item.Id}/history/last?action=painted")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.GetAsync($"/api/items/{Guid.NewGuid()}/history/last")).StatusCode);
    }

    [Fact]
    public async Task Update_ChangesFields_Or404()
    {
        var item = await CreateItemAsync();
        var entry = await AddAsync(item.Id, "2026-08-12", "Serviced", 850m, "Old Provider", "old");

        var request = new HistoryEntryRequest(new DateOnly(2026, 8, 13), "Repaired", null, null, "new");
        var response = await _client.PutAsJsonAsync($"/api/items/{item.Id}/history/{entry.Id}", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<HistoryEntryResponse>())!;
        Assert.Equal(new DateOnly(2026, 8, 13), updated.Date);
        Assert.Equal("Repaired", updated.Action);
        Assert.Null(updated.Cost);
        Assert.Null(updated.Provider);
        Assert.Equal("new", updated.Notes);

        var missing = await _client.PutAsJsonAsync($"/api/items/{item.Id}/history/{Guid.NewGuid()}", request);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Update_And_Delete_RejectEntryFromAnotherItem()
    {
        var itemA = await CreateItemAsync("A");
        var itemB = await CreateItemAsync("B");
        var entry = await AddAsync(itemA.Id, "2026-08-12", "Serviced");

        var request = new HistoryEntryRequest(new DateOnly(2026, 8, 13), "Hacked", null, null, null);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PutAsJsonAsync($"/api/items/{itemB.Id}/history/{entry.Id}", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.DeleteAsync($"/api/items/{itemB.Id}/history/{entry.Id}")).StatusCode);

        var detail = await _client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{itemA.Id}");
        Assert.Equal("Serviced", Assert.Single(detail!.History).Action);
    }

    [Fact]
    public async Task Delete_RemovesEntry_Then404()
    {
        var item = await CreateItemAsync();
        var entry = await AddAsync(item.Id, "2026-08-12", "Serviced");

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/items/{item.Id}/history/{entry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.DeleteAsync($"/api/items/{item.Id}/history/{entry.Id}")).StatusCode);

        var detail = await _client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}");
        Assert.Empty(detail!.History);
    }

    [Fact]
    public async Task List_IncludesEachItemsLastEntry_OrNullWhenNoHistory()
    {
        var ac = await CreateItemAsync("LG AC");
        var laptop = await CreateItemAsync("Laptop");
        await CreateItemAsync("Bike");   // no history
        await AddAsync(ac.Id, "2024-03-14", "Purchased", 38999m);
        await AddAsync(ac.Id, "2026-08-12", "Serviced", 850m, "CoolCare Services");
        await AddAsync(ac.Id, "2026-02-03", "Repaired", 1200m);
        await AddAsync(laptop.Id, "2025-01-10", "Purchased", 60000m);
        await AddAsync(laptop.Id, "2025-06-01", "Screen repair");
        await AddAsync(laptop.Id, "2025-06-01", "Battery replaced");   // same date, recorded later

        var items = (await _client.GetFromJsonAsync<List<ItemSummaryResponse>>("/api/items"))!;

        Assert.Equal(["Bike", "Laptop", "LG AC"], items.Select(i => i.Name));
        Assert.Null(items[0].LastEntry);
        Assert.Equal("Battery replaced", items[1].LastEntry!.Action);
        Assert.Equal("Serviced", items[2].LastEntry!.Action);
        Assert.Equal(850m, items[2].LastEntry!.Cost);
        Assert.Equal("CoolCare Services", items[2].LastEntry!.Provider);
    }

    [Fact]
    public async Task List_Search_StillFiltersAndKeepsLastEntry()
    {
        var ac = await CreateItemAsync("LG AC");
        await CreateItemAsync("Laptop");
        await AddAsync(ac.Id, "2026-08-12", "Serviced");

        var items = await _client.GetFromJsonAsync<List<ItemSummaryResponse>>("/api/items?search=ac");

        var only = Assert.Single(items!);
        Assert.Equal("LG AC", only.Name);
        Assert.Equal("Serviced", only.LastEntry!.Action);
    }

    [Fact]
    public async Task DeletingItem_DeletesItsHistory()
    {
        var item = await CreateItemAsync();
        var entry = await AddAsync(item.Id, "2026-08-12", "Serviced");

        await _client.DeleteAsync($"/api/items/{item.Id}");

        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.DeleteAsync($"/api/items/{item.Id}/history/{entry.Id}")).StatusCode);
    }
}
