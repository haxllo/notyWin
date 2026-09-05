using Microsoft.Win32;

namespace NotyWin.App;

/// <summary>
/// Toggle "Launch at login" via the per-user Run key.
/// Maps to the macOS <c>SMAppService</c> behaviour with the same scope
/// (current user, not system-wide).
/// </summary>
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "NotyWin";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is not null;
    }

    public static void SetEnabled(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            // Quote the path so spaces in the install location are handled.
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }
}
