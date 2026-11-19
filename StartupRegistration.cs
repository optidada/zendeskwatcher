using Microsoft.Win32;

namespace ZendeskWatcher;

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZendeskWatcher";

    /// <summary>
    /// Marks a start by Windows. Windows starts the Run list one app at a time, so it can reach this one minutes after
    /// logon, when the user has already started it by hand: that start then ends quietly.
    /// </summary>
    public const string AutostartArg = "--autostart";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {AutostartArg}");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
