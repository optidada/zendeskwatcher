using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI;

/// <summary>
/// A keyboard shortcut that works from anywhere (Windows' RegisterHotKey), e.g. Ctrl+Alt+Z for Catch up. When another
/// program already has the combination, it's skipped (and logged) rather than taken from that program.
/// </summary>
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;
    private const int Id = 1;

    private readonly Action _pressed;

    /// <summary>Registered: the shortcut works. False when another program has it.</summary>
    public bool Registered { get; }

    /// <summary>How the shortcut is written, e.g. "Ctrl+Alt+Z".</summary>
    public string Text { get; }

    /// <param name="what">What it does, for the log ("Catch up").</param>
    /// <param name="quiet">Only log when another program has it (registered often, e.g. while notifications show).</param>
    public GlobalHotkey(Keys key, bool control, bool alt, bool shift, bool win, Action pressed, string what = "Catch up", bool quiet = false)
    {
        _pressed = pressed;
        Text = string.Join("+", new[] { control ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, win ? "Win" : null, key.ToString() }
            .Where(p => p is not null));
        CreateHandle(new CreateParams()); // a message-only window to receive the shortcut
        // On many layouts Ctrl+Alt is AltGr (Polish AltGr+S = ś): a key AltGr types with isn't taken, so nobody loses
        // a letter while typing (Swedish: O, S, T, X, Z type nothing with AltGr).
        if (control && alt && !shift && !win && AltGrTypes(key))
        {
            Log.Info($"Shortcut {Text} types a character with AltGr on this keyboard layout; {what} has no shortcut");
            return;
        }
        var modifiers = ModNoRepeat | (control ? ModControl : 0) | (alt ? ModAlt : 0) | (shift ? ModShift : 0) | (win ? ModWin : 0);
        Registered = RegisterHotKey(Handle, Id, modifiers, (uint)key);
        if (!Registered)
            Log.Info($"Shortcut {Text} is used by another program; {what} has no shortcut");
        else if (!quiet)
            Log.Info($"Shortcut {Text}: {what}");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == Id)
            _pressed();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered)
            UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }

    /// <summary>Whether AltGr (Ctrl+Alt) with this key types a character on the keyboard layout in use.</summary>
    private static bool AltGrTypes(Keys key)
    {
        try
        {
            var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
            var state = new byte[256];
            state[0x11] = state[0x12] = 0x80; // VK_CONTROL, VK_MENU down
            state[0xA2] = state[0xA5] = 0x80; // left Ctrl, right Alt (what AltGr sends)
            var buffer = new System.Text.StringBuilder(8);
            var scan = MapVirtualKeyEx((uint)key, 0, layout);
            // Flag 4: leave the keyboard's dead-key state alone.
            return ToUnicodeEx((uint)key, scan, state, buffer, buffer.Capacity, 4, layout) > 0 && !char.IsControl(buffer[0]);
        }
        catch (Exception)
        {
            return false; // can't tell: take it, as before
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, System.Text.StringBuilder buffer, int bufferSize, uint flags, IntPtr layout);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
