using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Naveen_Sir.Models;

namespace Naveen_Sir.Services;

[SupportedOSPlatform("windows")]
public sealed class ScreenCaptureService : IDisposable
{
    private readonly object _syncLock = new();
    private readonly List<FrameSnapshot> _frameHistory = [];

    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private bool _enabled;

    private double[]? _lastHistogram;

    public event Action<string>? StatusChanged;

    public void SetEnabled(bool enabled)
    {
        lock (_syncLock)
        {
            if (_enabled == enabled)
            {
                return;
            }

            _enabled = enabled;

            if (_enabled)
            {
                _captureCts = new CancellationTokenSource();
                _captureTask = Task.Run(() => CaptureLoopAsync(_captureCts.Token));
                StatusChanged?.Invoke("Screen capture started");
            }
            else
            {
                _captureCts?.Cancel();
                _captureCts = null;
                _captureTask = null;
                StatusChanged?.Invoke("Screen capture stopped");
            }
        }
    }

    public IReadOnlyList<FrameSnapshot> GetTopKeyframes(TimeSpan timeframe, int maxFrames)
    {
        lock (_syncLock)
        {
            var selected = KeyframeSelector.SelectTopFrames(_frameHistory, timeframe, maxFrames);
            return selected;
        }
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(550));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var frame = CaptureFrame();
                lock (_syncLock)
                {
                    _frameHistory.Add(frame);
                    var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(45);
                    _frameHistory.RemoveAll(item => item.Timestamp < cutoff);
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Screen capture failed: {ex.Message}");
            }
        }
    }

    private FrameSnapshot CaptureFrame()
    {
        var width = (int)Math.Max(320, SystemParameters.PrimaryScreenWidth);
        var height = (int)Math.Max(180, SystemParameters.PrimaryScreenHeight);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);

        using var resized = new Bitmap(640, 360, PixelFormat.Format24bppRgb);
        using (var resizedGraphics = Graphics.FromImage(resized))
        {
            resizedGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            resizedGraphics.DrawImage(bitmap, 0, 0, resized.Width, resized.Height);
        }

        var histogram = ComputeHistogram(resized);
        var delta = _lastHistogram is null ? 1d : ComputeDelta(_lastHistogram, histogram);
        _lastHistogram = histogram;

        var jpegBytes = EncodeJpeg(resized, quality: 58L);

        return new FrameSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            JpegBytes = jpegBytes,
            ChangeScore = delta,
            Summary = "screen-keyframe",
        };
    }

    private static byte[] EncodeJpeg(Bitmap image, long quality)
    {
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encoder = Encoder.Quality;
        using var encoderParameters = new EncoderParameters(1);
        encoderParameters.Param[0] = new EncoderParameter(encoder, quality);
        image.Save(stream, codec, encoderParameters);
        return stream.ToArray();
    }

    private static double[] ComputeHistogram(Bitmap image)
    {
        var histogram = new double[48];
        var totalPixels = image.Width * image.Height;

        for (var y = 0; y < image.Height; y += 2)
        {
            for (var x = 0; x < image.Width; x += 2)
            {
                var pixel = image.GetPixel(x, y);
                var redBin = pixel.R / 16;
                var greenBin = pixel.G / 16;
                var blueBin = pixel.B / 16;

                histogram[redBin] += 1;
                histogram[16 + greenBin] += 1;
                histogram[32 + blueBin] += 1;
            }
        }

        var sampleCount = Math.Max(1, totalPixels / 4);
        for (var i = 0; i < histogram.Length; i++)
        {
            histogram[i] /= sampleCount;
        }

        return histogram;
    }

    private static double ComputeDelta(IReadOnlyList<double> previous, IReadOnlyList<double> current)
    {
        var diff = 0d;
        for (var i = 0; i < previous.Count; i++)
        {
            diff += Math.Abs(previous[i] - current[i]);
        }

        return diff / previous.Count;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _captureCts?.Cancel();
            _captureTask = null;
            _captureCts = null;
            _enabled = false;
        }
    }
}