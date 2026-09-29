using System.ComponentModel.DataAnnotations;
using LastTime.Api.Domain;
using LastTime.Api.Features.History;

namespace LastTime.Api.Features.Items;

public record ItemRequest(
    [Required, StringLength(200)] string Name,
    [StringLength(2000)] string? Description);

public record ItemResponse(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt)
{
    public static ItemResponse From(Item i) => new(i.Id, i.Name, i.Description, i.CreatedAt);
}


public record ItemDetailResponse(
    Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, List<HistoryEntryResponse> History);
