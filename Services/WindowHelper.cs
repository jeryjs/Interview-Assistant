using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int TOOLWINDOW_FLAGS = 0x00000080;

    public static void HideFromScreenShare(Window window)
    {
        ShowInTaskbar(window, false);
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    public static void ShowInScreenShare(Window window)
    {
        ShowInTaskbar(window, true);
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowDisplayAffinity(hwnd, WDA_NONE);
    }

    private static void ShowInTaskbar(Window window, bool show)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var styles = GetWindowLong(hwnd, GWL_EXSTYLE);

        if (show)
        {
            styles &= ~TOOLWINDOW_FLAGS;
        }
        else
        {
            styles |= TOOLWINDOW_FLAGS;
        }

        SetWindowLong(hwnd, GWL_EXSTYLE, styles);
    }    
}
