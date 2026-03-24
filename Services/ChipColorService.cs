using System.Security.Cryptography;
using System.Text;

namespace Naveen_Sir.Services;

public static class ChipColorService
{
    public readonly record struct ChipPalette(string BorderHex, string GradientStartHex, string GradientEndHex);

    public static ChipPalette ForText(string text)
    {
        var normalized = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            normalized = "empty";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var avgWordLength = words.Length == 0 ? 4d : words.Average(static word => word.Length);
        var vowelCount = normalized.Count(static c => "aeiou".Contains(c));
        var letterCount = normalized.Count(char.IsLetter);
        var punctuationCount = normalized.Count(static c => char.IsPunctuation(c));
        var uniqueRatio = normalized.Distinct().Count() / (double)Math.Max(1, normalized.Length);
        var vowelRatio = letterCount == 0 ? 0.35 : vowelCount / (double)letterCount;
        var rhythm = words.Length == 0
            ? 0
            : words.Select(static w => w.Length % 7).Sum();

        var hashHue = (hash[0] << 8) + hash[1];
        var featureHue = (int)Math.Round(
            avgWordLength * 19
            + uniqueRatio * 163
            + vowelRatio * 241
            + punctuationCount * 13
            + rhythm * 7);

        var hue = Mod(hashHue + featureHue, 360);
        var saturation = 52 + Mod(hash[2] + (int)Math.Round(uniqueRatio * 60), 30);
        var borderLightness = 48 + Mod(hash[3] + (int)Math.Round(vowelRatio * 40), 14);

        var startLightness = Clamp(borderLightness + 18, 28, 84);
        var endLightness = Clamp(borderLightness + 9, 24, 76);
        var startSaturation = Clamp(saturation - 12, 26, 70);
        var endSaturation = Clamp(saturation - 18, 20, 64);

        var borderHex = HslToHex(hue, saturation, borderLightness, alpha: 255);
        var gradientStartHex = HslToHex(hue, startSaturation, startLightness, alpha: 56);
        var gradientEndHex = HslToHex(Mod(hue + 14, 360), endSaturation, endLightness, alpha: 36);

        return new ChipPalette(borderHex, gradientStartHex, gradientEndHex);
    }

    private static string HslToHex(int h, int s, int l, int alpha)
    {
        var hue = h / 360d;
        var saturation = s / 100d;
        var lightness = l / 100d;

        var q = lightness < 0.5
            ? lightness * (1 + saturation)
            : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;

        var r = HueToRgb(p, q, hue + 1d / 3d);
        var g = HueToRgb(p, q, hue);
        var b = HueToRgb(p, q, hue - 1d / 3d);

        var r8 = (byte)Math.Round(r * 255);
        var g8 = (byte)Math.Round(g * 255);
        var b8 = (byte)Math.Round(b * 255);
        var a8 = (byte)alpha;

        return $"#{a8:X2}{r8:X2}{g8:X2}{b8:X2}";
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1d / 6d)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1d / 2d)
        {
            return q;
        }

        if (t < 2d / 3d)
        {
            return p + (q - p) * (2d / 3d - t) * 6;
        }

        return p;
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Min(max, Math.Max(min, value));
    }

    private static int Mod(int value, int modulo)
    {
        var result = value % modulo;
        return result < 0 ? result + modulo : result;
    }
}