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
    private bool _runtimeActive;
    private bool _isPaused;
    private bool _micEnabled;
    private bool _systemAudioEnabled;
    private bool _screenEnabled;

    public event Action<string>? TranscriptGenerated;
    public event Action<IReadOnlyList<ChipItem>>? ChipsGenerated;
    public event Action<string>? StatusChanged;
    public event Action<string>? ScreenSourceUnavailable;

    public AssistantEngine(AppState state)
    {
        _state = state;
        _isPaused = state.IsPaused;
        _micEnabled = state.MicEnabled;
        _systemAudioEnabled = state.SystemAudioEnabled;
        _screenEnabled = state.ScreenShareEnabled;

        _screenCapture.SetSource(ResolveScreenSourceMode(state.ScreenSourceMode), state.ScreenSourceWindowHandle, state.ScreenSourceWindowTitle);

        _audioCapture.ChunkReady += OnAudioChunkReady;
        _audioCapture.SpeechDetected += OnSpeechDetected;
        _audioCapture.StatusChanged += message => StatusChanged?.Invoke(message);
        _screenCapture.StatusChanged += message => StatusChanged?.Invoke(message);
        _screenCapture.SourceUnavailable += OnScreenSourceUnavailable;
        _whisperTranscriber.StatusChanged += message => StatusChanged?.Invoke(message);
    }

    private void OnScreenSourceUnavailable(string message)
    {
        _screenEnabled = false;
        _state.ScreenShareEnabled = false;
        ScreenSourceUnavailable?.Invoke(message);
        StatusChanged?.Invoke(message);
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        if (_isPaused)
        {
            StatusChanged?.Invoke("Assistant paused");
            return;
        }

        StartRuntime();
        StatusChanged?.Invoke("Assistant engine started");
    }

    public void SetCaptureState(bool micEnabled, bool systemAudioEnabled, bool screenEnabled)
    {
        _micEnabled = micEnabled;
        _systemAudioEnabled = systemAudioEnabled;
        _screenEnabled = screenEnabled;

        if (_runtimeActive && !_isPaused)
        {
            _audioCapture.SetEnabled(_micEnabled, _systemAudioEnabled);
            _screenCapture.SetEnabled(_screenEnabled);
            return;
        }

        _audioCapture.SetEnabled(false, false);
        _screenCapture.SetEnabled(false);
    }

    public void SetScreenSource(ScreenSourceMode mode, long windowHandle, string windowTitle)
    {
        _state.ScreenSourceMode = mode == ScreenSourceMode.SpecificWindow ? "SpecificWindow" : "EntireScreen";
        _state.ScreenSourceWindowHandle = mode == ScreenSourceMode.SpecificWindow ? windowHandle : 0;
        _state.ScreenSourceWindowTitle = mode == ScreenSourceMode.SpecificWindow ? windowTitle : string.Empty;
        _screenCapture.SetSource(mode, windowHandle, windowTitle);
    }

    private static ScreenSourceMode ResolveScreenSourceMode(string value)
    {
        return string.Equals(value, "SpecificWindow", StringComparison.Ordinal)
            ? ScreenSourceMode.SpecificWindow
            : ScreenSourceMode.EntireScreen;
    }

    public bool IsPaused()
    {
        return _isPaused;
    }

    public void SetPaused(bool paused)
    {
        if (_isPaused == paused)
        {
            return;
        }

        _isPaused = paused;
        _state.IsPaused = paused;

        if (_isPaused)
        {
            StopRuntime();
            DrainPendingAudioChunks();
            StatusChanged?.Invoke("Assistant paused");
            return;
        }

        if (_started)
        {
            StartRuntime();
            StatusChanged?.Invoke("Assistant resumed");
        }
    }

    private void StartRuntime()
    {
        if (_runtimeActive)
        {
            return;
        }

        _runCts = new CancellationTokenSource();
        _audioCapture.SetEnabled(_micEnabled, _systemAudioEnabled);
        _screenCapture.SetEnabled(_screenEnabled);
        _transcriptionLoopTask = Task.Run(() => RunTranscriptionLoopAsync(_runCts.Token));
        _recommendationLoopTask = Task.Run(() => RunRecommendationLoopAsync(_runCts.Token));
        _runtimeActive = true;
    }

    private void StopRuntime()
    {
        if (!_runtimeActive)
        {
            return;
        }

        var cts = _runCts;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
        }

        _audioCapture.SetEnabled(false, false);
        _screenCapture.SetEnabled(false);

        try
        {
            _transcriptionLoopTask?.Wait(TimeSpan.FromSeconds(2));
            _recommendationLoopTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignored
        }

        _transcriptionLoopTask = null;
        _recommendationLoopTask = null;
        cts?.Dispose();
        _runCts = null;
        _runtimeActive = false;
    }

    private void DrainPendingAudioChunks()
    {
        while (_audioChunks.Reader.TryRead(out _))
        {
        }
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
            var contextWindow = GetContextWindow();
            if (!HasTranscriptInWindow(contextWindow))
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var dueByMaxInterval = now - _lastRecommendationAt >= TimeSpan.FromSeconds(30);
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
        var contextWindow = GetContextWindow();

        lock (_stateLock)
        {
            var cutoff = DateTimeOffset.UtcNow - contextWindow;
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
        var chipModelId = loadout.ResolveChipModelId();
        var frames = _screenEnabled
            ? _screenCapture.GetTopKeyframes(contextWindow, 10)
            : [];

        StatusChanged?.Invoke($"Sending context to provider: {frames.Count} keyframes, {currentChips.Count} chips");

        var streamBuffer = new StringBuilder();
        await foreach (var chunk in _providerClient.StreamRecommendationTextAsync(loadout, chipModelId, transcriptContext, frames, currentChips, includeImages: _screenEnabled, cancellationToken))
        {
            streamBuffer.Append(chunk);
            foreach (var chip in DrainChipsFromBuffer(streamBuffer))
            {
                EmitChip(chip, loadout, chipModelId);
            }
        }

        foreach (var chip in ParseChips(streamBuffer.ToString()))
        {
            EmitChip(chip, loadout, chipModelId);
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

    private void EmitChip(string chipText, ProviderLoadout loadout, string chipModelId)
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

        var palette = ChipColorService.ForText(chipText);

        ChipsGenerated?.Invoke([
            new ChipItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = chipText,
                CreatedAtUtc = DateTime.UtcNow,
                SourceModel = chipModelId,
                SourceProviderName = loadout.Name,
                BorderColor = palette.BorderHex,
                GradientStartColor = palette.GradientStartHex,
                GradientEndColor = palette.GradientEndHex,
            },
        ]);
    }

    public async IAsyncEnumerable<string> StreamTopicOverviewAsync(
        string topic,
        ProviderLoadout loadout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string transcriptContext;
        var contextWindow = GetContextWindow();
        lock (_stateLock)
        {
            var cutoff = DateTimeOffset.UtcNow - contextWindow;
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

        var topicModelId = loadout.ResolveTopicModelId();
        await foreach (var chunk in _providerClient.StreamTopicMarkdownAsync(loadout, topicModelId, topic, transcriptContext, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return chunk;
        }
    }

    private TimeSpan GetContextWindow()
    {
        var seconds = Math.Clamp(_state.ContextWindowSeconds, 10, 180);
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        StopRuntime();
        _audioChunks.Writer.TryComplete();

        _audioCapture.Dispose();
        _screenCapture.Dispose();
        _whisperTranscriber.Dispose();
        _recommendationLock.Dispose();
    }
}