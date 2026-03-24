namespace Naveen_Sir.Models;

public sealed class AppState
{
    public bool MicEnabled { get; set; } = true;
    public bool SystemAudioEnabled { get; set; } = true;
    public bool ScreenShareEnabled { get; set; } = true;
    public bool PinOnTop { get; set; }
    public bool TranscriptExpanded { get; set; } = false;
    public string WhisperModelPath { get; set; } = string.Empty;
    public string WhisperLanguage { get; set; } = "en";
    public int MaxChipCount { get; set; } = 220;
    public List<ProviderLoadout> ProviderLoadouts { get; set; } = [ProviderLoadout.CreateDefault()];
    public string ActiveLoadoutId { get; set; } = string.Empty;
    public List<ChipItem> ChipFeed { get; set; } = [];
    public Dictionary<string, string> ThreadCache { get; set; } = [];

    public void Normalize()
    {
        if (ProviderLoadouts.Count == 0)
        {
            ProviderLoadouts.Add(ProviderLoadout.CreateDefault());
        }

        if (string.IsNullOrWhiteSpace(ActiveLoadoutId) || ProviderLoadouts.All(loadout => loadout.Id != ActiveLoadoutId))
        {
            ActiveLoadoutId = ProviderLoadouts[0].Id;
        }

        if (MaxChipCount < 50)
        {
            MaxChipCount = 220;
        }

        var now = DateTime.UtcNow;
        var validWindow = TimeSpan.FromHours(6);

        ChipFeed = ChipFeed
            .Where(chip =>
                !string.IsNullOrWhiteSpace(chip.Text)
                && !string.IsNullOrWhiteSpace(chip.SourceProviderName)
                && !string.IsNullOrWhiteSpace(chip.SourceModel)
                && now - chip.CreatedAtUtc <= validWindow)
            .OrderBy(chip => chip.CreatedAtUtc)
            .TakeLast(MaxChipCount)
            .ToList();

        var validChipIds = ChipFeed.Select(chip => chip.Id).ToHashSet(StringComparer.Ordinal);
        ThreadCache = ThreadCache
            .Where(entry => validChipIds.Contains(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(static entry => entry.Key, static entry => entry.Value);
    }

    public ProviderLoadout ResolveActiveLoadout()
    {
        return ProviderLoadouts.First(loadout => loadout.Id == ActiveLoadoutId);
    }
}