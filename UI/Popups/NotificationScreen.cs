using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// The monitor notifications appear on: the one you're working on. When something shows while nothing else is on
/// screen, it's the monitor with the mouse (<see cref="FollowMouse"/>); everything that follows (cards, pill, ticker,
/// glow) joins it there until all are gone, so a stack never splits or jumps. A monitor that's unplugged meanwhile:
/// the primary one.
/// </summary>
internal static class NotificationScreen
{
    private static string? _held;

    /// <summary>The monitor in use now (the primary one before anything was shown, or when the chosen one is gone).</summary>
    public static Screen Current
    {
        get
        {
            var screens = Screen.AllScreens;
            var name = Resolve(screens.Select(s => s.DeviceName), _held, (Screen.PrimaryScreen ?? screens[0]).DeviceName);
            return screens.FirstOrDefault(s => s.DeviceName == name) ?? Screen.PrimaryScreen ?? screens[0];
        }
    }

    /// <summary>Nothing is on screen and something is about to show: the monitor with the mouse from now on.</summary>
    public static void FollowMouse()
    {
        try
        {
            _held = Screen.FromPoint(Cursor.Position).DeviceName;
        }
        catch (Exception ex) // no mouse position (e.g. on the lock screen): stay where it was
        {
            Log.Error("Could not tell which monitor the mouse is on", ex);
        }
    }

    /// <summary>The scaling of the monitor in use (1 = 100 %, 1.5 = 150 %): sizes and gaps are multiplied by it.</summary>
    public static float Scale => ScaleOf(Current);

    /// <summary>
    /// Font sizes are in points, which Windows turns into pixels at the main monitor's scaling (the one at sign-in).
    /// On a monitor with another scaling they're multiplied by this, so text grows with everything else.
    /// </summary>
    public static float FontRatio => Scale / SystemScale;

    /// <summary>The scaling Windows uses for points (the main monitor's at sign-in).</summary>
    public static float SystemScale
    {
        get
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96f;
        }
    }

    /// <summary>A monitor's own scaling; the system scaling when Windows can't tell.</summary>
    public static float ScaleOf(Screen screen)
    {
        try
        {
            var b = screen.Bounds;
            var monitor = MonitorFromPoint(new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96f;
        }
        catch (Exception ex) // older Windows without shcore: the old behavior
        {
            Log.Error("Could not read the monitor's scaling", ex);
        }
        return SystemScale;
    }

    private const int MONITOR_DEFAULTTONEAREST = 2, MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// After a notification's menu opened (Options ▾, Who could take it ▾, the ticker's): moved wholly onto the
    /// notifications' monitor. Windows puts a menu that doesn't fit beside a notification near the edge on the monitor
    /// it overlaps most, which can be the other one.
    /// </summary>
    public static void KeepOnScreen(ToolStripDropDown menu, Control owner)
    {
        // The owner's own monitor (the notification is there), not a guess: the menu belongs beside it.
        var area = PopupForm.WorkArea(Screen.FromControl(owner));
        var bounds = menu.Bounds;
        var at = Clamp(bounds, area);
        if (at != bounds.Location && menu.Visible && bounds.Width > 0 && bounds.Height > 0)
            menu.Location = at;
    }

    /// <summary>Where a rectangle goes to lie inside the area (its top-left corner when it's bigger).</summary>
    internal static Point Clamp(Rectangle bounds, Rectangle area) => new(
        Math.Max(area.Left, Math.Min(bounds.Left, area.Right - bounds.Width)),
        Math.Max(area.Top, Math.Min(bounds.Top, area.Bottom - bounds.Height)));

    /// <summary>The held monitor while it's still connected, otherwise the primary one.</summary>
    internal static string Resolve(IEnumerable<string> connected, string? held, string primary) =>
        held is not null && connected.Contains(held) ? held : primary;
}
