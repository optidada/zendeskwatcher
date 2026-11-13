namespace ZendeskWatcher.UI;

/// <summary>What the tray icon shows: not checking (dim), all clear (green), unassigned tickets waiting (orange).</summary>
public enum TrayState { Off, Ok, Attention }

/// <summary>
/// The tray icon: its color for the current state, the status text (tooltip and the menu's first line), and a
/// "working" pulse while a check runs. Safe to call from any thread; changes are made on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripItem _statusItem;
    private readonly Dictionary<TrayState, Icon> _icons = new()
    {
        [TrayState.Off] = AppIcon.CreateIcon(AppIcon.Dim),
        [TrayState.Ok] = AppIcon.CreateIcon(AppIcon.NeonGreen),
        [TrayState.Attention] = AppIcon.CreateIcon(AppIcon.NeonOrange)
    };

    // "Working" pulse: shown while a check runs (after a short delay, so quick checks don't flicker).
    private readonly Dictionary<TrayState, Icon[]> _pulseFrames = [];
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 90 };
    private readonly System.Windows.Forms.Timer _pulseDelay = new() { Interval = 600 };
    private int _pulseFrame;
    private int _busy;

    /// <summary>The current state; the pulse animates its icon.</summary>
    public TrayState State { get; private set; } = TrayState.Off;

    /// <summary>A left click on the icon (like Teams or OneDrive; a double-click is two of these). Right-click shows the menu.</summary>
    public event EventHandler? LeftClick;

    /// <param name="statusItem">The menu line that shows the status text.</param>
    public TrayIcon(ContextMenuStrip menu, ToolStripItem statusItem, SynchronizationContext ui)
    {
        _ui = ui;
        _statusItem = statusItem;
        _tray = new NotifyIcon { Icon = _icons[TrayState.Off], Text = "Zendesk Watcher", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) LeftClick?.Invoke(this, EventArgs.Empty); };
        _pulseDelay.Tick += (_, _) => { _pulseDelay.Stop(); _pulseTimer.Start(); };
        _pulseTimer.Tick += (_, _) => PulseTick();
    }

    /// <summary>Shows a status text, and switches the icon if <paramref name="state"/> is given.</summary>
    /// <param name="iconHint">Only in the icon's tooltip, e.g. "click this icon to sign in" (in the menu it would be confusing).</param>
    public void SetStatus(string text, TrayState? state = null, string? iconHint = null) => _ui.Post(_ =>
    {
        _statusItem.Text = text;
        var tooltip = iconHint is null ? $"Zendesk Watcher\n{text}" : $"Zendesk Watcher\n{text} \u2013 {iconHint}";
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (state is { } s)
        {
            State = s;
            if (!_pulseTimer.Enabled)
                _tray.Icon = _icons[s];
        }
    }, null);

    /// <summary>Work started (a check against Zendesk): pulse the icon if it takes more than a moment.</summary>
    public void BeginBusy()
    {
        if (_busy++ == 0)
            _pulseDelay.Start();
    }

    public void EndBusy()
    {
        if (_busy == 0 || --_busy > 0)
            return;
        _pulseDelay.Stop();
        _pulseTimer.Stop();
        _tray.Icon = _icons[State];
    }

    /// <summary>Removes the icon right away, e.g. before the process exits for an update (no ghost icon).</summary>
    public void Hide() => _tray.Visible = false;

    private void PulseTick()
    {
        if (!_pulseFrames.TryGetValue(State, out var frames))
        {
            var color = State switch { TrayState.Attention => AppIcon.NeonOrange, TrayState.Off => AppIcon.Dim, _ => AppIcon.NeonGreen };
            _pulseFrames[State] = frames = AppIcon.CreatePulseFrames(color);
        }
        _pulseFrame = (_pulseFrame + 1) % frames.Length;
        _tray.Icon = frames[_pulseFrame];
    }

    public void Dispose()
    {
        _pulseTimer.Dispose();
        _pulseDelay.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
