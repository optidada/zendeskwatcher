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

    public GlobalHotkey(Keys key, bool control, bool alt, bool shift, bool win, Action pressed)
    {
        _pressed = pressed;
        Text = string.Join("+", new[] { control ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, win ? "Win" : null, key.ToString() }
            .Where(p => p is not null));
        CreateHandle(new CreateParams()); // a message-only window to receive the shortcut
        var modifiers = ModNoRepeat | (control ? ModControl : 0) | (alt ? ModAlt : 0) | (shift ? ModShift : 0) | (win ? ModWin : 0);
        Registered = RegisterHotKey(Handle, Id, modifiers, (uint)key);
        Log.Info(Registered ? $"Shortcut {Text}: Catch up" : $"Shortcut {Text} is used by another program; Catch up has no shortcut");
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
