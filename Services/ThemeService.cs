using Microsoft.Win32;

namespace Naveen_Sir.Services;

public static class ThemeService
{
    private const string PersonalizeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, false);
            var value = key?.GetValue(AppsUseLightThemeValue);
            if (value is int intValue)
            {
                return intValue == 0;
            }
        }
        catch
        {
            return true;
        }

        return true;
    }
}