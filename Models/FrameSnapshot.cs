namespace Naveen_Sir.Models;

public sealed class FrameSnapshot
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public byte[] JpegBytes { get; set; } = [];
    public double ChangeScore { get; set; }
    public string Summary { get; set; } = string.Empty;

    public string ToSummaryLine()
    {
        return $"[{Timestamp.LocalDateTime:HH:mm:ss}] Δ{ChangeScore:F3} {Summary}";
    }
}