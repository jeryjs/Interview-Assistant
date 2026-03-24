namespace Naveen_Sir.Models;

public sealed class ChipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceModel { get; set; } = string.Empty;
    public string SourceProviderName { get; set; } = string.Empty;
}