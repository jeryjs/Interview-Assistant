using System.Runtime.InteropServices;
using System.Text;

namespace Naveen_Sir.Services;

public static class WindowCatalogService
{
    public static IReadOnlyList<WindowCandidate> GetVisibleWindows()
    {
        var windows = new List<WindowCandidate>();

        _ = EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd) || !TryGetWindowTitle(hWnd, out var title) || string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (!GetWindowRect(hWnd, out var rect))
            {
                return true;
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 120 || height < 80)
            {
                return true;
            }

            windows.Add(new WindowCandidate
            {
                Handle = hWnd,
                Title = title,
                Width = width,
                Height = height,
            });

            return true;
        }, nint.Zero);

        return windows
            .OrderBy(window => window.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsWindowValid(long handle)
    {
        if (handle == 0)
        {
            return false;
        }

        var hWnd = (nint)handle;
        if (!IsWindow(hWnd) || !IsWindowVisible(hWnd) || IsIconic(hWnd))
        {
            return false;
        }

        if (!GetWindowRect(hWnd, out var rect))
        {
            return false;
        }

        return rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    public static bool TryGetWindowBounds(long handle, out WindowBounds bounds)
    {
        bounds = default;
        if (!IsWindowValid(handle))
        {
            return false;
        }

        var hWnd = (nint)handle;
        if (!GetWindowRect(hWnd, out var rect))
        {
            return false;
        }

        bounds = new WindowBounds(
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top));

        return true;
    }

    private static bool TryGetWindowTitle(nint hWnd, out string title)
    {
        title = string.Empty;
        var titleLength = GetWindowTextLength(hWnd);
        if (titleLength <= 0)
        {
            return false;
        }

        var builder = new StringBuilder(titleLength + 1);
        _ = GetWindowText(hWnd, builder, builder.Capacity);
        title = builder.ToString().Trim();
        return !string.IsNullOrWhiteSpace(title);
    }

    public sealed class WindowCandidate
    {
        public nint Handle { get; init; }
        public string Title { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }

        public string DisplayLabel => $"{Title} ({Width}×{Height})";
        public long HandleValue => (long)Handle;
    }

    public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}