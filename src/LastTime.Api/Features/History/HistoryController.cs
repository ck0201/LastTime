using LastTime.Api.Data;
using LastTime.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LastTime.Api.Features.History;

[ApiController]
[Route("api/items/{itemId:guid}/history")]
public class HistoryController(LastTimeDbContext db) : ControllerBase
{
    /// <summary>The latest entry, optionally only for actions containing <paramref name="action"/> ("when did I last service…").</summary>
    [HttpGet("last")]
    public async Task<ActionResult<HistoryEntryResponse>> Last(Guid itemId, [FromQuery] string? action, CancellationToken ct)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, ct)) return NotFound();

        var query = db.HistoryEntries.AsNoTracking().Where(e => e.ItemId == itemId);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var pattern = LikePattern.Contains(action);
            query = query.Where(e => EF.Functions.ILike(e.Action, pattern, LikePattern.EscapeChar));
        }

        var entry = await query.NewestFirst().FirstOrDefaultAsync(ct);
        return entry is null ? NotFound() : HistoryEntryResponse.From(entry);
    }

    [HttpPost]
    public async Task<ActionResult<HistoryEntryResponse>> Create(Guid itemId, HistoryEntryRequest request, CancellationToken ct)
    {
        if (!await db.Items.AnyAsync(i => i.Id == itemId, ct)) return NotFound();

        var entry = new HistoryEntry { ItemId = itemId };
        Apply(entry, request);
        db.HistoryEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Last), new { itemId }, HistoryEntryResponse.From(entry));
    }

    [HttpPut("{entryId:guid}")]
    public async Task<ActionResult<HistoryEntryResponse>> Update(
        Guid itemId, Guid entryId, HistoryEntryRequest request, CancellationToken ct)
    {
        var entry = await db.HistoryEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.ItemId == itemId, ct);
        if (entry is null) return NotFound();

        Apply(entry, request);
        await db.SaveChangesAsync(ct);

        return HistoryEntryResponse.From(entry);
    }

    [HttpDelete("{entryId:guid}")]
    public async Task<IActionResult> Delete(Guid itemId, Guid entryId, CancellationToken ct)
    {
        var deleted = await db.HistoryEntries.Where(e => e.Id == entryId && e.ItemId == itemId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound() : NoContent();
    }

    private static void Apply(HistoryEntry entry, HistoryEntryRequest r)
    {
        entry.Date = r.Date!.Value; // [Required] guarantees a value
        entry.Action = r.Action.Trim();
        entry.Cost = r.Cost;
        entry.Provider = NullIfBlank(r.Provider);
        entry.Notes = NullIfBlank(r.Notes);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
