namespace Naveen_Sir.Models;

public sealed class RecommendationEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "Event";
    public string Message { get; set; } = string.Empty;
    public List<ChipItem> Chips { get; set; } = [];

    public string HeaderText => $"[{CreatedAt:HH:mm:ss}] {Kind}";
}