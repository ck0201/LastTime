using LastTime.Api.Domain;

namespace LastTime.Api.Features.History;

public static class HistoryQueryExtensions
{
    /// <summary>Newest first; entries on the same date are ordered by when they were recorded.</summary>
    public static IOrderedQueryable<HistoryEntry> NewestFirst(this IQueryable<HistoryEntry> query) =>
        query.OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt);
}
