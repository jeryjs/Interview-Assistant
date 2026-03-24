using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace Naveen_Sir.Services;

public sealed class WhisperTranscriptionService : IDisposable
{
    private readonly object _syncLock = new();
    private WhisperFactory? _factory;
    private string _loadedModelPath = string.Empty;

    public event Action<string>? StatusChanged;

    public async Task<string> TranscribePcm16ChunkAsync(
        byte[] pcm16Mono16K,
        string requestedModelPath,
        string language,
        CancellationToken cancellationToken)
    {
        if (pcm16Mono16K.Length < 16000)
        {
            return string.Empty;
        }

        var modelPath = await EnsureModelReadyAsync(requestedModelPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        WhisperFactory factory;
        lock (_syncLock)
        {
            if (_factory is null || !string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                _factory?.Dispose();
                _factory = WhisperFactory.FromPath(modelPath);
                _loadedModelPath = modelPath;
                StatusChanged?.Invoke($"Loaded whisper model: {Path.GetFileName(modelPath)}");
            }

            factory = _factory;
        }

        using var processor = factory
            .CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
            .WithNoContext()
            .Build();

        using var wavStream = BuildWaveStream(pcm16Mono16K);
        var textBuilder = new StringBuilder();

        await foreach (var segment in processor.ProcessAsync(wavStream, cancellationToken))
        {
            var text = segment.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (textBuilder.Length > 0)
                {
                    textBuilder.Append(' ');
                }

                textBuilder.Append(text);
            }
        }

        return textBuilder.ToString().Trim();
    }

    private async Task<string> EnsureModelReadyAsync(string requestedModelPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedModelPath) && File.Exists(requestedModelPath))
        {
            return requestedModelPath;
        }

        var modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InterviewHelper",
            "models");

        Directory.CreateDirectory(modelDirectory);
        var autoModelPath = Path.Combine(modelDirectory, "ggml-base.en.bin");
        if (File.Exists(autoModelPath))
        {
            return autoModelPath;
        }

        StatusChanged?.Invoke("Downloading local whisper model (base.en)...");
        await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.BaseEn, cancellationToken);
        await using var output = File.Create(autoModelPath);
        await modelStream.CopyToAsync(output, cancellationToken);
        StatusChanged?.Invoke("Whisper model download completed");
        return autoModelPath;
    }

    private static MemoryStream BuildWaveStream(byte[] pcm16Mono16K)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + pcm16Mono16K.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(16000 * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(pcm16Mono16K.Length);
        writer.Write(pcm16Mono16K);
        writer.Flush();

        stream.Position = 0;
        return stream;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _factory?.Dispose();
            _factory = null;
        }
    }
}