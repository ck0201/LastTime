namespace LastTime.Api.Domain;

public class HistoryEntry
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public Item Item { get; set; } = null!;

    public DateOnly Date { get; set; }
    public string Action { get; set; } = string.Empty;
    public decimal? Cost { get; set; }
    public string? Provider { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
