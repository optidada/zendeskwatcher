using Microsoft.Win32;

namespace ZendeskWatcher.UI;

/// <summary>
/// Puts the tray icon on the taskbar instead of behind the ^ arrow, once. Not an official Windows API: Windows 11 keeps
/// a key per program under NotifyIconSettings (created when the icon is first shown) whose IsPromoted value is what
/// "Other system tray icons" in Windows Settings toggles. It's set only while missing, so a choice made there (0 when
/// hidden) is kept. On Windows 10 the key doesn't exist and nothing happens.
/// </summary>
internal static class TrayPin
{
    private const string Root = @"Control Panel\NotifyIconSettings";

    /// <summary>Result of one try: pinned now, nothing to do (already chosen, or not Windows 11), or the entry isn't there yet.</summary>
    public enum Outcome { Pinned, NothingToDo, NotYet }

    public static Outcome TryPin()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null || Environment.ProcessPath is not { } exe)
                return Outcome.NothingToDo;

            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name, writable: true);
                if (entry?.GetValue("ExecutablePath") is not string path || !string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (entry.GetValue("IsPromoted") is not null)
                    return Outcome.NothingToDo; // the user (or an earlier start) chose already
                entry.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                Log.Info("Tray icon: pinned to the taskbar (first start)");
                return Outcome.Pinned;
            }
            return Outcome.NotYet;
        }
        catch (Exception ex)
        {
            Log.Error("Tray icon: could not pin it", ex);
            return Outcome.NothingToDo;
        }
    }
}
