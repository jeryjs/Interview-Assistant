namespace Naveen_Sir.Models;

public sealed class TranscriptEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "mic";
    public string Text { get; set; } = string.Empty;

    public string ToDisplayLine()
    {
        return $"[{Timestamp.LocalDateTime:HH:mm:ss}] ({Source}) {Text}";
    }
}