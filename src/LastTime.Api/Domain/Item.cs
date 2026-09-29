namespace LastTime.Api.Domain;

public class Item
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<HistoryEntry> History { get; set; } = [];
}
