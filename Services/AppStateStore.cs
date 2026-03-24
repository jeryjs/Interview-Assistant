using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Naveen_Sir.Models;

namespace Naveen_Sir.Services;

public static class AppStateStore
{
    private const string EncryptedPrefix = "enc:";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static string StateDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "InterviewHelper");

    private static string StateFilePath => Path.Combine(StateDirectoryPath, "state.json");

    public static AppState Load()
    {
        if (!File.Exists(StateFilePath))
        {
            var initialState = new AppState();
            initialState.Normalize();
            return initialState;
        }

        var serialized = File.ReadAllText(StateFilePath);
        var state = JsonSerializer.Deserialize<AppState>(serialized) ?? throw new InvalidOperationException("Failed to deserialize state file.");
        foreach (var loadout in state.ProviderLoadouts)
        {
            loadout.ApiKey = DecryptIfNeeded(loadout.ApiKey);
        }

        state.Normalize();
        return state;
    }

    public static Task SaveAsync(AppState state)
    {
        state.Normalize();
        Directory.CreateDirectory(StateDirectoryPath);

        var toPersist = new AppState
        {
            IsPaused = state.IsPaused,
            MicEnabled = state.MicEnabled,
            SystemAudioEnabled = state.SystemAudioEnabled,
            ScreenShareEnabled = state.ScreenShareEnabled,
            ScreenSourceMode = state.ScreenSourceMode,
            ScreenSourceWindowHandle = state.ScreenSourceWindowHandle,
            ScreenSourceWindowTitle = state.ScreenSourceWindowTitle,
            PinOnTop = state.PinOnTop,
            TranscriptExpanded = state.TranscriptExpanded,
            WhisperModelPath = state.WhisperModelPath,
            WhisperLanguage = state.WhisperLanguage,
            MaxChipCount = state.MaxChipCount,
            ActiveLoadoutId = state.ActiveLoadoutId,
            ChipFeed = state.ChipFeed,
            ThreadCache = state.ThreadCache,
            ProviderLoadouts = state.ProviderLoadouts
                .Select(loadout => new ProviderLoadout
                {
                    Id = loadout.Id,
                    Name = loadout.Name,
                    Endpoint = loadout.Endpoint,
                    ApiKey = Encrypt(loadout.ApiKey),
                    ModelId = loadout.ModelId,
                    Temperature = loadout.Temperature,
                    MaxTokens = loadout.MaxTokens,
                })
                .ToList(),
        };

        var serialized = JsonSerializer.Serialize(toPersist, SerializerOptions);
        return File.WriteAllTextAsync(StateFilePath, serialized);
    }

    private static string Encrypt(string plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(protectedBytes);
    }

    private static string DecryptIfNeeded(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return input;
        }

        var encoded = input[EncryptedPrefix.Length..];
        var protectedBytes = Convert.FromBase64String(encoded);
        var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}