using NAudio.Wave;

namespace Naveen_Sir.Services;

public sealed class AudioCaptureService : IDisposable
{
    private const int TargetSampleRate = 16000;
    private const int TargetChannels = 1;
    private const int TargetBitsPerSample = 16;
    private const int ChunkSeconds = 3;

    private readonly ChunkAccumulator _micAccumulator = new(TargetSampleRate * 2 * ChunkSeconds);
    private readonly ChunkAccumulator _systemAccumulator = new(TargetSampleRate * 2 * ChunkSeconds);
    private readonly object _syncLock = new();

    private WaveInEvent? _micCapture;
    private WasapiLoopbackCapture? _systemCapture;

    private bool _micEnabled;
    private bool _systemEnabled;
    private bool _disposed;

    public event Action<AudioChunk>? ChunkReady;
    public event Action<DateTimeOffset>? SpeechDetected;
    public event Action<string>? StatusChanged;

    public void SetEnabled(bool micEnabled, bool systemEnabled)
    {
        lock (_syncLock)
        {
            _micEnabled = micEnabled;
            _systemEnabled = systemEnabled;

            if (_micEnabled && _micCapture is null)
            {
                _micCapture = new WaveInEvent
                {
                    DeviceNumber = 0,
                    BufferMilliseconds = 240,
                    WaveFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels),
                };
                _micCapture.DataAvailable += OnMicDataAvailable;
                _micCapture.RecordingStopped += OnCaptureStopped;
                _micCapture.StartRecording();
                StatusChanged?.Invoke("Microphone capture started");
            }

            if (!_micEnabled && _micCapture is not null)
            {
                StopAndDisposeMicCapture();
            }

            if (_systemEnabled && _systemCapture is null)
            {
                _systemCapture = new WasapiLoopbackCapture();
                _systemCapture.DataAvailable += OnSystemDataAvailable;
                _systemCapture.RecordingStopped += OnCaptureStopped;
                _systemCapture.StartRecording();
                StatusChanged?.Invoke("System audio capture started");
            }

            if (!_systemEnabled && _systemCapture is not null)
            {
                StopAndDisposeSystemCapture();
            }
        }
    }

    private void OnCaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            StatusChanged?.Invoke($"Audio capture stopped: {e.Exception.Message}");
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_micEnabled || e.BytesRecorded == 0)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var pcm = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);

        if (ComputeRms(pcm) > 0.018)
        {
            SpeechDetected?.Invoke(timestamp);
        }

        foreach (var chunk in _micAccumulator.AppendAndSplit(pcm))
        {
            ChunkReady?.Invoke(new AudioChunk("mic", timestamp, chunk));
        }
    }

    private void OnSystemDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_systemEnabled || _systemCapture is null || e.BytesRecorded == 0)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var converted = ConvertToMonoPcm16K(e.Buffer, e.BytesRecorded, _systemCapture.WaveFormat);
        if (converted.Length == 0)
        {
            return;
        }

        if (ComputeRms(converted) > 0.014)
        {
            SpeechDetected?.Invoke(timestamp);
        }

        foreach (var chunk in _systemAccumulator.AppendAndSplit(converted))
        {
            ChunkReady?.Invoke(new AudioChunk("system", timestamp, chunk));
        }
    }

    private static byte[] ConvertToMonoPcm16K(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        var channels = Math.Max(1, sourceFormat.Channels);
        var sampleRate = sourceFormat.SampleRate;

        if (sourceFormat.Encoding == WaveFormatEncoding.Pcm && sourceFormat.BitsPerSample == 16 && channels == 1 && sampleRate == TargetSampleRate)
        {
            var output = new byte[bytesRecorded];
            Buffer.BlockCopy(buffer, 0, output, 0, bytesRecorded);
            return output;
        }

        var monoSamples = sourceFormat.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when sourceFormat.BitsPerSample == 32 => ExtractMonoFloatFromFloat32(buffer, bytesRecorded, channels),
            WaveFormatEncoding.Pcm when sourceFormat.BitsPerSample == 16 => ExtractMonoFloatFromPcm16(buffer, bytesRecorded, channels),
            _ => [],
        };

        if (monoSamples.Length == 0)
        {
            return [];
        }

        return ResampleAndEncodeToPcm16(monoSamples, sampleRate, TargetSampleRate);
    }

    private static float[] ExtractMonoFloatFromFloat32(byte[] source, int bytesRecorded, int channels)
    {
        var totalFrames = bytesRecorded / (4 * channels);
        var mono = new float[totalFrames];

        for (var frame = 0; frame < totalFrames; frame++)
        {
            var sampleSum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frame * channels + channel) * 4;
                sampleSum += BitConverter.ToSingle(source, offset);
            }

            mono[frame] = sampleSum / channels;
        }

        return mono;
    }

    private static float[] ExtractMonoFloatFromPcm16(byte[] source, int bytesRecorded, int channels)
    {
        var totalFrames = bytesRecorded / (2 * channels);
        var mono = new float[totalFrames];

        for (var frame = 0; frame < totalFrames; frame++)
        {
            var sampleSum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frame * channels + channel) * 2;
                var sample = BitConverter.ToInt16(source, offset) / 32768f;
                sampleSum += sample;
            }

            mono[frame] = sampleSum / channels;
        }

        return mono;
    }

    private static byte[] ResampleAndEncodeToPcm16(float[] monoSamples, int sourceRate, int targetRate)
    {
        if (monoSamples.Length == 0 || sourceRate <= 0 || targetRate <= 0)
        {
            return [];
        }

        var ratio = (double)sourceRate / targetRate;
        var targetCount = (int)Math.Max(1, Math.Floor(monoSamples.Length / ratio));
        var output = new byte[targetCount * 2];
        var index = 0d;

        for (var i = 0; i < targetCount; i++)
        {
            var sourceIndex = Math.Min((int)index, monoSamples.Length - 1);
            var clamped = Math.Clamp(monoSamples[sourceIndex], -1f, 1f);
            var sample = (short)Math.Round(clamped * short.MaxValue);
            var bytes = BitConverter.GetBytes(sample);
            output[i * 2] = bytes[0];
            output[i * 2 + 1] = bytes[1];
            index += ratio;
        }

        return output;
    }

    private static double ComputeRms(byte[] pcm16)
    {
        if (pcm16.Length < 2)
        {
            return 0;
        }

        double sumSquares = 0;
        var sampleCount = pcm16.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BitConverter.ToInt16(pcm16, i * 2) / 32768d;
            sumSquares += sample * sample;
        }

        return Math.Sqrt(sumSquares / sampleCount);
    }

    private void StopAndDisposeMicCapture()
    {
        if (_micCapture is null)
        {
            return;
        }

        _micCapture.DataAvailable -= OnMicDataAvailable;
        _micCapture.RecordingStopped -= OnCaptureStopped;
        _micCapture.StopRecording();
        _micCapture.Dispose();
        _micCapture = null;
        StatusChanged?.Invoke("Microphone capture stopped");
    }

    private void StopAndDisposeSystemCapture()
    {
        if (_systemCapture is null)
        {
            return;
        }

        _systemCapture.DataAvailable -= OnSystemDataAvailable;
        _systemCapture.RecordingStopped -= OnCaptureStopped;
        _systemCapture.StopRecording();
        _systemCapture.Dispose();
        _systemCapture = null;
        StatusChanged?.Invoke("System audio capture stopped");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_syncLock)
        {
            StopAndDisposeMicCapture();
            StopAndDisposeSystemCapture();
        }
    }

    public sealed record AudioChunk(string Source, DateTimeOffset Timestamp, byte[] Pcm16Mono16K);

    private sealed class ChunkAccumulator
    {
        private readonly int _chunkBytes;
        private readonly List<byte> _buffer = [];
        private readonly object _lock = new();

        public ChunkAccumulator(int chunkBytes)
        {
            _chunkBytes = chunkBytes;
        }

        public IReadOnlyList<byte[]> AppendAndSplit(byte[] incoming)
        {
            if (incoming.Length == 0)
            {
                return [];
            }

            lock (_lock)
            {
                _buffer.AddRange(incoming);
                var output = new List<byte[]>();

                while (_buffer.Count >= _chunkBytes)
                {
                    var chunk = _buffer.GetRange(0, _chunkBytes).ToArray();
                    _buffer.RemoveRange(0, _chunkBytes);
                    output.Add(chunk);
                }

                return output;
            }
        }
    }
}