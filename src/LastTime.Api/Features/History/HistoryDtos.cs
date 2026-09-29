using System.ComponentModel.DataAnnotations;
using LastTime.Api.Domain;

namespace LastTime.Api.Features.History;

public record HistoryEntryRequest(
    [Required] DateOnly? Date,
    [Required, StringLength(100)] string Action,
    [Range(0, 9999999999.99)] decimal? Cost,
    [StringLength(200)] string? Provider,
    [StringLength(2000)] string? Notes);

public record HistoryEntryResponse(
    Guid Id, Guid ItemId, DateOnly Date, string Action, decimal? Cost, string? Provider, string? Notes,
    DateTimeOffset CreatedAt)
{
    public static HistoryEntryResponse From(HistoryEntry e) =>
        new(e.Id, e.ItemId, e.Date, e.Action, e.Cost, e.Provider, e.Notes, e.CreatedAt);
}
