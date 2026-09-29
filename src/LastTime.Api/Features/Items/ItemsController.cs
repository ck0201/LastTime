using LastTime.Api.Data;
using LastTime.Api.Domain;
using LastTime.Api.Features.History;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LastTime.Api.Features.Items;

[ApiController]
[Route("api/items")]
public class ItemsController(LastTimeDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ItemResponse>>> Search([FromQuery] string? search, CancellationToken ct)
    {
        var query = db.Items.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = LikePattern.Contains(search);
            query = query.Where(i =>
                EF.Functions.ILike(i.Name, pattern, LikePattern.EscapeChar) ||
                (i.Description != null && EF.Functions.ILike(i.Description, pattern, LikePattern.EscapeChar)));
        }

        return await query
            .OrderBy(i => i.Name.ToLower())
            .Select(i => new ItemResponse(i.Id, i.Name, i.Description, i.CreatedAt))
            .ToListAsync(ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ItemDetailResponse>> GetById(Guid id, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
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
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return NotFound();

        item.Name = request.Name.Trim();
        item.Description = NullIfBlank(request.Description);
        await db.SaveChangesAsync(ct);

        return ItemResponse.From(item);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var deleted = await db.Items.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound() : NoContent();
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}


