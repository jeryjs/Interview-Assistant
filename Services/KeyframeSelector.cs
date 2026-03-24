using Naveen_Sir.Models;

namespace Naveen_Sir.Services;

public static class KeyframeSelector
{
    public static IReadOnlyList<FrameSnapshot> SelectTopFrames(
        IReadOnlyList<FrameSnapshot> frames,
        TimeSpan timeframe,
        int maxFrames)
    {
        var cutoff = DateTimeOffset.UtcNow - timeframe;
        var candidateFrames = frames
            .Where(frame => frame.Timestamp >= cutoff)
            .OrderByDescending(frame => frame.ChangeScore)
            .ToList();

        if (candidateFrames.Count == 0)
        {
            return [];
        }

        var selected = new List<FrameSnapshot>(maxFrames);
        foreach (var frame in candidateFrames)
        {
            if (selected.Any(existing => Math.Abs((existing.Timestamp - frame.Timestamp).TotalMilliseconds) < 450))
            {
                continue;
            }

            selected.Add(frame);
            if (selected.Count == maxFrames)
            {
                break;
            }
        }

        return selected
            .OrderBy(frame => frame.Timestamp)
            .ToList();
    }
}