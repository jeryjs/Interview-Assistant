using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Naveen_Sir.Models;

namespace Naveen_Sir.Services;

public sealed class AssistantEngine : IDisposable
{
    private readonly object _stateLock = new();
    private readonly AppState _state;
    private readonly AudioCaptureService _audioCapture = new();
    private readonly ScreenCaptureService _screenCapture = new();
    private readonly WhisperTranscriptionService _whisperTranscriber = new();
    private readonly OpenAiCompatibleClient _providerClient = new();

    private readonly Channel<AudioCaptureService.AudioChunk> _audioChunks = Channel.CreateUnbounded<AudioCaptureService.AudioChunk>();
    private readonly List<TranscriptEntry> _transcriptHistory = [];
    private readonly HashSet<string> _chipTextSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _chipOrder = [];
    private readonly SemaphoreSlim _recommendationLock = new(1, 1);

    private CancellationTokenSource? _runCts;
    private Task? _transcriptionLoopTask;
    private Task? _recommendationLoopTask;

    private DateTimeOffset _lastSpeechAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastRecommendationAt = DateTimeOffset.MinValue;

    private bool _started;
    private bool _micEnabled;
    private bool _systemAudioEnabled;
    private bool _screenEnabled;

    public event Action<string>? TranscriptGenerated;
    public event Action<IReadOnlyList<ChipItem>>? ChipsGenerated;
    public event Action<string>? StatusChanged;

    public AssistantEngine(AppState state)
    {
        _state = state;
        _micEnabled = state.MicEnabled;
        _systemAudioEnabled = state.SystemAudioEnabled;
        _screenEnabled = state.ScreenShareEnabled;

        _audioCapture.ChunkReady += OnAudioChunkReady;
        _audioCapture.SpeechDetected += OnSpeechDetected;
        _audioCapture.StatusChanged += message => StatusChanged?.Invoke(message);
        _screenCapture.StatusChanged += message => StatusChanged?.Invoke(message);
        _whisperTranscriber.StatusChanged += message => StatusChanged?.Invoke(message);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _runCts = new CancellationTokenSource();
        _audioCapture.SetEnabled(_micEnabled, _systemAudioEnabled);
        _screenCapture.SetEnabled(_screenEnabled);

        _transcriptionLoopTask = Task.Run(() => RunTranscriptionLoopAsync(_runCts.Token));
        _recommendationLoopTask = Task.Run(() => RunRecommendationLoopAsync(_runCts.Token));
        StatusChanged?.Invoke("Assistant engine started");
    }

    public void SetCaptureState(bool micEnabled, bool systemAudioEnabled, bool screenEnabled)
    {
        _micEnabled = micEnabled;
        _systemAudioEnabled = systemAudioEnabled;
        _screenEnabled = screenEnabled;

        _audioCapture.SetEnabled(_micEnabled, _systemAudioEnabled);
        _screenCapture.SetEnabled(_screenEnabled);
    }

    private void OnAudioChunkReady(AudioCaptureService.AudioChunk chunk)
    {
        if (!_audioChunks.Writer.TryWrite(chunk))
        {
            StatusChanged?.Invoke("Audio queue backpressure detected");
        }
    }

    private void OnSpeechDetected(DateTimeOffset timestamp)
    {
        _lastSpeechAt = timestamp;
    }

    private async Task RunTranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var chunk in _audioChunks.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var text = await _whisperTranscriber.TranscribePcm16ChunkAsync(
                    chunk.Pcm16Mono16K,
                    _state.WhisperModelPath,
                    _state.WhisperLanguage,
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var entry = new TranscriptEntry
                {
                    Timestamp = chunk.Timestamp,
                    Source = chunk.Source,
                    Text = text,
                };

                lock (_stateLock)
                {
                    _transcriptHistory.Add(entry);
                    var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(40);
                    _transcriptHistory.RemoveAll(item => item.Timestamp < cutoff);
                }

                TranscriptGenerated?.Invoke(entry.ToDisplayLine());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Transcription error: {ex.Message}");
            }
        }
    }

    private async Task RunRecommendationLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!HasTranscriptInWindow(TimeSpan.FromSeconds(30)))
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var dueByMaxInterval = now - _lastRecommendationAt >= TimeSpan.FromSeconds(10);
            var dueByPause = now - _lastSpeechAt >= TimeSpan.FromSeconds(1.2)
                && now - _lastRecommendationAt >= TimeSpan.FromSeconds(2.4);

            if (!dueByMaxInterval && !dueByPause)
            {
                continue;
            }

            if (!await _recommendationLock.WaitAsync(0, cancellationToken))
            {
                continue;
            }

            try
            {
                _lastRecommendationAt = DateTimeOffset.UtcNow;
                await GenerateRecommendationsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Recommendation loop failed: {ex.Message}");
            }
            finally
            {
                _recommendationLock.Release();
            }
        }
    }

    private bool HasTranscriptInWindow(TimeSpan window)
    {
        lock (_stateLock)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            return _transcriptHistory.Any(item => item.Timestamp >= cutoff);
        }
    }

    private async Task GenerateRecommendationsAsync(CancellationToken cancellationToken)
    {
        string transcriptContext;
        List<string> currentChips;

        lock (_stateLock)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30);
            transcriptContext = string.Join(
                "\n",
                _transcriptHistory
                    .Where(item => item.Timestamp >= cutoff)
                    .Select(item => item.ToDisplayLine()));

            currentChips = _chipOrder.TakeLast(60).ToList();
        }

        if (string.IsNullOrWhiteSpace(transcriptContext))
        {
            return;
        }

        var loadout = _state.ResolveActiveLoadout();
        var frames = _screenEnabled
            ? _screenCapture.GetTopKeyframes(TimeSpan.FromSeconds(30), 10)
            : [];

        StatusChanged?.Invoke($"Sending context to provider: {frames.Count} keyframes, {currentChips.Count} chips");

        var streamBuffer = new StringBuilder();
        await foreach (var chunk in _providerClient.StreamRecommendationTextAsync(loadout, transcriptContext, frames, currentChips, cancellationToken))
        {
            streamBuffer.Append(chunk);
            foreach (var chip in DrainChipsFromBuffer(streamBuffer))
            {
                EmitChip(chip, loadout);
            }
        }

        foreach (var chip in ParseChips(streamBuffer.ToString()))
        {
            EmitChip(chip, loadout);
        }
    }

    private IEnumerable<string> DrainChipsFromBuffer(StringBuilder streamBuffer)
    {
        var text = streamBuffer.ToString();
        var lines = text.Split('\n');
        if (lines.Length <= 1)
        {
            return [];
        }

        var output = lines[..^1]
            .Select(NormalizeChip)
            .Where(static chip => !string.IsNullOrWhiteSpace(chip))
            .ToList();

        streamBuffer.Clear();
        streamBuffer.Append(lines[^1]);
        return output;
    }

    private static IReadOnlyList<string> ParseChips(string rawText)
    {
        return rawText
            .Split(['\n', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeChip)
            .Where(static chip => !string.IsNullOrWhiteSpace(chip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string NormalizeChip(string chip)
    {
        var normalized = chip.Trim();
        normalized = normalized.TrimStart('-', '*', '•', '1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '.', ')', ' ');
        normalized = normalized.Replace("\r", string.Empty, StringComparison.Ordinal);
        if (normalized.Length > 90)
        {
            normalized = normalized[..90].Trim();
        }

        return normalized;
    }

    private void EmitChip(string chipText, ProviderLoadout loadout)
    {
        if (string.IsNullOrWhiteSpace(chipText) || chipText.Length < 3)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!_chipTextSet.Add(chipText))
            {
                return;
            }

            _chipOrder.Add(chipText);
            if (_chipOrder.Count > 500)
            {
                _chipOrder.RemoveRange(0, _chipOrder.Count - 500);
            }
        }

        ChipsGenerated?.Invoke([
            new ChipItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = chipText,
                CreatedAtUtc = DateTime.UtcNow,
                SourceModel = loadout.ModelId,
                SourceProviderName = loadout.Name,
            },
        ]);
    }

    public async IAsyncEnumerable<string> StreamTopicOverviewAsync(
        string topic,
        ProviderLoadout loadout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string transcriptContext;
        lock (_stateLock)
        {
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(45);
            transcriptContext = string.Join(
                "\n",
                _transcriptHistory
                    .Where(item => item.Timestamp >= cutoff)
                    .Select(item => item.ToDisplayLine()));
        }

        if (string.IsNullOrWhiteSpace(transcriptContext))
        {
            transcriptContext = "No recent transcript context available.";
        }

        try
        {
            await foreach (var chunk in _providerClient.StreamTopicMarkdownAsync(loadout, topic, transcriptContext, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return chunk;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var safeMessage = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (safeMessage.Length > 220)
            {
                safeMessage = safeMessage[..220] + "…";
            }

            yield return $"# {topic}\n\n> Provider call failed.\n\n`{safeMessage}`\n";
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _audioChunks.Writer.TryComplete();

        try
        {
            _transcriptionLoopTask?.Wait(TimeSpan.FromSeconds(2));
            _recommendationLoopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _audioCapture.Dispose();
        _screenCapture.Dispose();
        _whisperTranscriber.Dispose();
        _recommendationLock.Dispose();
        _runCts?.Dispose();
    }
}