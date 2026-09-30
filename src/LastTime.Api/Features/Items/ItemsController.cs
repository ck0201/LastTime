using LastTime.Api.Data;
using LastTime.Api.Domain;
using LastTime.Api.Features.Auth;
using LastTime.Api.Features.History;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LastTime.Api.Features.Items;

[ApiController]
[Authorize]
[Route("api/items")]
public class ItemsController(LastTimeDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ItemSummaryResponse>>> Search([FromQuery] string? search, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var query = db.Items.AsNoTracking().Where(i => i.UserId == userId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search);
            query = query.Where(i =>
                EF.Functions.ILike(i.Name, pattern, LikePattern.EscapeChar) ||
                (i.Description != null && EF.Functions.ILike(i.Description, pattern, LikePattern.EscapeChar)));
        }

        // One query: each item with its newest history entry (same ordering as the timeline).
        var rows = await query
            .OrderBy(i => i.Name.ToLower())
            .Select(i => new
            {
                Item = i,
                Last = i.History
                    .OrderByDescending(h => h.Date)
                    .ThenByDescending(h => h.CreatedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new ItemSummaryResponse(
                r.Item.Id, r.Item.Name, r.Item.Description, r.Item.CreatedAt,
                r.Last is null ? null : HistoryEntryResponse.From(r.Last)))
            .ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ItemDetailResponse>> GetById(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);
        if (item is null) return NotFound();

        var history = await db.HistoryEntries.AsNoTracking()
            .Where(e => e.ItemId == id)
            .NewestFirst()
            .ToListAsync(ct);

        return new ItemDetailResponse(item.Id, item.Name, item.Description, item.CreatedAt,
            history.ConvertAll(HistoryEntryResponse.From));
    }

    [HttpPost]
    public async Task<ActionResult<ItemResponse>> Create(ItemRequest request, CancellationToken ct)
    {
        var item = new Item
        {
            UserId = User.GetUserId(),
            Name = request.Name.Trim(),
            Description = NullIfBlank(request.Description),
        };
        db.Items.Add(item);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { id = item.Id }, ItemResponse.From(item));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ItemResponse>> Update(Guid id, ItemRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id && i.UserId == userId, ct);
        if (item is null) return NotFound();

        item.Name = request.Name.Trim();
        item.Description = NullIfBlank(request.Description);
        await db.SaveChangesAsync(ct);

        return ItemResponse.From(item);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var deleted = await db.Items.Where(i => i.Id == id && i.UserId == userId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound() : NoContent();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}


