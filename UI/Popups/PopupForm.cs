using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// A custom notification popup: borderless, always on top, never steals focus.
/// Slides in, counts down (paused while hovered), and slides out. Painted entirely by the theme palette.
/// </summary>
public sealed class PopupForm : Form
{
    public enum Result { None, Open, Take, CatchUp, Snooze, Dismiss, Timeout }

    /// <summary>The button between Open and Snooze/Close: "Take it" on new tickets, "Catch up" on the start summary.</summary>
    private bool HasExtraButton => _content.CanTake || _content.CanCatchUp;

    private const int BaseWidth = 380;
    private const double EnterMs = 260;
    private const double LeaveMs = 200;
    private const double TypingCharsPerSecond = 160;

    private readonly NotificationContent _content;
    private readonly PopupPalette _palette;
    private readonly double _durationMs; // 0 = until dismissed
    private readonly float _s;           // DPI scale
    private readonly Color _accent;
    private readonly Font _headingFont;
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _smallFont;
    private readonly Font _bodyBoldFont;
    private readonly Font _clientFont;
    private readonly RichText _rich;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Layout (client coordinates)
    private Rectangle _headingRect, _titleRect, _bodyRect, _footerRect, _barRect, _closeRect, _copyRect, _openRect, _snoozeRect, _takeRect, _dismissRect, _clientRect, _muteRect;
    private string _openLabel = "", _snoozeLabel = "", _closeLabel = "", _takeLabel = "", _dismissLabel = "";

    // State
    private enum Phase { Entering, Showing, Leaving }
    private Phase _phase = Phase.Entering;
    private double _phaseStart;
    private double _lastTick;
    private double _elapsed;            // countdown time, excludes hover
    private bool _hover;
    private Rectangle? _hoverButton;
    private float _y;
    private int _restX;
    private float _x = float.NaN; // glides to _restX while showing (peek tabs shift sideways at the top edge)

    // Peek (Look & sound -> Several at once): tucked at a screen edge, only a tab showing until the mouse comes.
    /// <summary>Which screen edge it peeks out from; <see cref="PeekEdge.None"/> for the other arrangements.</summary>
    public enum PeekEdge { None, Right, Left, Top }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PeekEdge Peek { get; set; }

    private float _tuck;               // 0 = out, 1 = tucked away
    private bool _seen;                // slid out by the mouse once: from then on it counts down like any other
    private double _forcedOutUntil;    // urgent and important ones come out by themselves once
    private double _leftAt = -1e9;     // a short wait after the mouse leaves, so it doesn't snap back at once
    private double _wiggleFrom = -1;   // the nudge when it arrives
    private int _paintedFill = -1;
    private int _yOffset;
    private const double WiggleMs = 900;
    private int TabSize => Px(22);

    public Result Outcome { get; private set; }

    /// <summary>Whether it has a countdown at all (not "stay until dismissed").</summary>
    public bool TimesOut => _durationMs > 0;

    /// <summary>
    /// Several popups take turns: only the one whose turn it is counts down; the others wait with a full countdown.
    /// Set by <see cref="PopupManager"/>.
    /// </summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MyTurn { get; set; } = true;

    /// <summary>Don't count down while the user is away (see <see cref="UserAway"/>). Set from the settings.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PauseWhenAway { get; set; } = true;

    private bool Away => PauseWhenAway && UserAway();
    public NotificationContent Content => _content;

    /// <summary>The Y position the stack wants this popup at; it glides there.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int TargetY
    {
        get => _targetY;
        set
        {
            if (value == _targetY)
                return;
            _targetY = value;
            _timer.Interval = BusyInterval; // glide there smoothly
        }
    }

    private int _targetY;

    // Repaint only when something visible changes: at 66 fps while animating, otherwise a few times a second
    // (enough for the terminal cursor blink and the "· 7 min ago" text). Mouse events repaint on their own.
    private const int BusyInterval = 15;
    private const int IdleInterval = 250;
    private string? _paintedBody;
    private long _paintedBlink = -1;

    public event Action<PopupForm>? Finished;

    /// <summary>Copy was clicked (ticket notifications only). The popup stays open and shows a check mark for a moment.</summary>
    public event Action<PopupForm>? CopyClicked;

    /// <summary>A choice from "Mute ▾" on the client line (<see cref="NotificationContent.MuteTarget"/>); the popup closes.</summary>
    public event Action<PopupForm, ClientChoice>? ClientChosen;

    /// <summary>The "Mute ▾" menu is open: the countdown waits, as when hovering.</summary>
    private bool _menuOpen;

    private double _copiedUntil;
    private Font? _iconFont;

    /// <param name="scale">Screen DPI scale.</param>
    /// <param name="size">The user's notification size (1 = 100%): text, buttons and spacing grow together.</param>
    public PopupForm(NotificationContent content, PopupPalette palette, int seconds, float scale, float size = 1f)
    {
        _content = content;
        _palette = palette;
        _s = scale * size;
        _durationMs = Math.Max(0, seconds) * 1000; // 0: until dismissed
        _accent = palette.Accent(content.Priority, content.Kind);

        var family = FontExists(palette.FontFamily) ? palette.FontFamily : (palette.Terminal ? "Consolas" : "Segoe UI");
        // Font sizes are in points (Windows applies the DPI); only the user's size is added here.
        _headingFont = new Font(family, (palette.FontSize - 1f) * size, palette.Terminal ? FontStyle.Regular : FontStyle.Bold);
        _titleFont = new Font(family, (palette.FontSize + (palette.Terminal ? 0.5f : 1.5f)) * size, FontStyle.Bold);
        _bodyFont = new Font(family, palette.FontSize * size);
        _smallFont = new Font(family, (palette.FontSize - 1f) * size, palette.Terminal ? FontStyle.Regular : FontStyle.Bold);
        _bodyBoldFont = new Font(family, palette.FontSize * size, FontStyle.Bold);
        _clientFont = new Font(family, (palette.FontSize + (palette.Terminal ? 1f : 2.5f)) * size, FontStyle.Bold);
        _rich = new RichText(_bodyFont, _bodyBoldFont);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = palette.Background;
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        ClientSize = new Size(Px(BaseWidth), MeasureLayout());

        _timer.Tick += (_, _) => Tick();
    }

    // ------------------------------------------------------------------ window behaviour

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Windows 11: rounded corners and a themed 1px border (ignored on Windows 10).
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_DONOTROUND = 1, DWMWCP_ROUND = 2;
        const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

        var corner = _palette.Terminal ? DWMWCP_DONOTROUND : DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var border = _palette.Terminal
            ? unchecked((int)DWMWA_COLOR_NONE)                                           // we paint our own border
            : ColorTranslator.ToWin32(_palette.Stripe && _palette.Dark && _palette.Glow ? Blend(_palette.Border, _accent, 0.35f) : _palette.Border);
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>Where it rests horizontally (moved by deck stacking, where older cards fan out to the right).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int RestX
    {
        get => _restX;
        set => _restX = value;
    }

    /// <summary>Puts this popup above the other ones without taking the focus from what you're doing (deck stacking).</summary>
    public void RaiseWithoutActivating()
    {
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;
        if (IsHandleCreated)
            SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>How a popup comes in and goes: from the right or left edge, dropping from the top, or fading.</summary>
    public enum Entry { FromRight, FromLeft, FromTop, Fade }

    private Entry _entry = Entry.FromRight;

    /// <summary>Shows the popup at (restX, TargetY), coming in as <paramref name="entry"/> says.</summary>
    public void Present(int restX, Entry entry = Entry.FromRight)
    {
        _restX = restX;
        _entry = entry;
        _y = entry == Entry.FromTop ? TargetY - Height - Px(24) : TargetY; // drops down to its place
        if (Peek != PeekEdge.None)
        {
            _tuck = 1; // arrives already tucked: only the tab slides in
            var start = _clock.Elapsed.TotalMilliseconds;
            _wiggleFrom = start + EnterMs;
            if (string.Equals(_content.Priority, "urgent", StringComparison.OrdinalIgnoreCase) || _content.Important)
                _forcedOutUntil = start + EnterMs + 6000;
        }
        if (entry is Entry.FromTop or Entry.Fade)
            Opacity = 0;
        Location = new Point(HiddenX(1), (int)_y);
        Show();
        _phaseStart = _lastTick = _clock.Elapsed.TotalMilliseconds;
        _timer.Start();
    }

    /// <summary>What the second button does: snooze tickets, close app messages; nothing for "OK" messages (OK closes).</summary>
    private Result SecondButton =>
        _content.ResolvedAction == Notifier.ActionInfo ? Result.None
        : _content.IsTicketNotification || _content.ResolvedAction == Notifier.ActionNone ? Result.Snooze // tickets, and their samples
        : Result.Dismiss; // the start summary, welcome, update ready, sign-in, …

    private string DismissLabel => _dismissLabel;

    public void Dismiss(Result outcome)
    {
        if (_phase == Phase.Leaving)
            return;
        Outcome = outcome;
        _phase = Phase.Leaving;
        _phaseStart = _clock.Elapsed.TotalMilliseconds;
    }

    // ------------------------------------------------------------------ animation

    private void Tick()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var dt = now - _lastTick;
        _lastTick = now;
        var t = now - _phaseStart;

        // Glide vertically towards the stack position.
        _y += (TargetY - _y) * (float)Math.Min(1, dt / 90);

        int x;
        switch (_phase)
        {
            case Phase.Entering:
                var pIn = Math.Min(1, t / EnterMs);
                x = HiddenX(1 - EaseOutBack(pIn));
                if (_entry is Entry.FromTop or Entry.Fade)
                    Opacity = Math.Min(1, pIn * 1.5);
                if (pIn >= 1)
                {
                    _phase = Phase.Showing;
                    _phaseStart = now;
                }
                break;

            case Phase.Leaving:
                var pOut = Math.Min(1, t / LeaveMs);
                x = HiddenX(pOut * pOut);
                if (_entry is Entry.FromTop or Entry.Fade)
                    Opacity = 1 - pOut;
                if (pOut >= 1)
                {
                    _timer.Stop();
                    Finished?.Invoke(this);
                    Close();
                    return;
                }
                break;

            default:
                _x = float.IsNaN(_x) ? _restX : _x + (_restX - _x) * (float)Math.Min(1, dt / 90);
                x = (int)Math.Round(_x);
                if (_durationMs > 0 && !_hover && MyTurn && !Away && (Peek == PeekEdge.None || _seen))
                {
                    _elapsed += dt;
                    if (_elapsed >= _durationMs)
                        Dismiss(Result.Timeout);
                }
                break;
        }

        var peekBusy = false;
        if (Peek != PeekEdge.None)
        {
            // Leaving: stays as tucked as it is and goes from there.
            var goal = _phase == Phase.Leaving ? _tuck : _hover || _menuOpen || now < _forcedOutUntil || now - _leftAt < 400 ? 0f : 1f;
            _tuck += (goal - _tuck) * (float)Math.Min(1, dt / 80);
            if (Math.Abs(goal - _tuck) < 0.005f)
                _tuck = goal;
            if (_hover && _tuck < 0.1f)
                _seen = true;
            var span = Peek == PeekEdge.Top ? Height : Width;
            var hidden = _tuck * (span - TabSize);
            var sinceWiggle = now - _wiggleFrom;
            if (_wiggleFrom >= 0 && sinceWiggle is >= 0 and < WiggleMs)
                hidden -= _tuck * Px(12) * (float)(Math.Abs(Math.Sin(sinceWiggle / WiggleMs * 3 * Math.PI)) * (1 - sinceWiggle / WiggleMs));
            var offset = (int)Math.Round(hidden);
            if (Peek == PeekEdge.Right) x += offset;
            else if (Peek == PeekEdge.Left) x -= offset;
            else _yOffset = -offset;
            peekBusy = _tuck != goal || (_wiggleFrom >= 0 && sinceWiggle < WiggleMs);
        }

        var location = new Point(x, (int)Math.Round(_y) + (Peek == PeekEdge.Top ? _yOffset : 0));
        var moved = location != Location;
        if (moved)
            Location = location;
        if (Peek != PeekEdge.None)
            ClipToScreen(location);

        var countingDown = _phase == Phase.Showing && _durationMs > 0 && !_hover && MyTurn && !Away;
        var typing = Typed().Typing;
        var gliding = Math.Abs(TargetY - _y) > 0.5f;
        var busy = _phase != Phase.Showing || gliding || countingDown || typing || peekBusy;
        var fill = Peek != PeekEdge.None ? (int)(TabFill() * 100) : -1;

        var body = Body;
        var blink = _palette.Terminal ? _clock.ElapsedMilliseconds / 500 : 0;
        if (busy || moved || body != _paintedBody || blink != _paintedBlink || fill != _paintedFill)
        {
            _paintedFill = fill;
            _paintedBody = body;
            _paintedBlink = blink;
            Invalidate();
        }

        var interval = busy ? BusyInterval : IdleInterval;
        if (_timer.Interval != interval)
            _timer.Interval = interval;
    }

    /// <summary>The x on the way in or out: <paramref name="amount"/> 0 is in place, 1 fully outside its screen edge.</summary>
    private int HiddenX(double amount)
    {
        var hidden = Width + Px(24);
        return _entry switch
        {
            Entry.FromRight => _restX + (int)(hidden * amount),
            Entry.FromLeft => _restX - (int)(hidden * amount),
            _ => _restX // dropping or fading: no sideways movement
        };
    }

    private Rectangle _clip;

    /// <summary>
    /// A tucked notification is mostly outside the screen; with a monitor next to it, that part would show there.
    /// Only the part on this screen is drawn (and takes the mouse).
    /// </summary>
    private void ClipToScreen(Point location)
    {
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        var visible = Rectangle.Intersect(new Rectangle(location, Size), area);
        visible.Offset(-location.X, -location.Y);
        if (visible == _clip)
            return;
        _clip = visible;
        var old = Region;
        Region = visible.Size == Size ? null : new Region(visible);
        old?.Dispose();
    }

    /// <summary>How much of the tab is filled with the accent color: a little at first, all of it after 30 minutes waiting.</summary>
    private float TabFill()
    {
        var since = _content.EventTime ?? DateTimeOffset.Now - TimeSpan.FromMilliseconds(_clock.Elapsed.TotalMilliseconds);
        var minutes = (DateTimeOffset.Now - since).TotalMinutes;
        return (float)Math.Clamp(0.2 + 0.8 * minutes / 30, 0.2, 1);
    }

    /// <summary>
    /// The tab: the strip that stays on screen while tucked, in the accent color filling up the longer it waits, with
    /// an arrow pointing where it slides out. Fades as the notification comes out.
    /// </summary>
    private void PaintPeekTab(Graphics g)
    {
        if (Peek == PeekEdge.None || _tuck < 0.02f)
            return;
        var tab = TabSize;
        var strip = Peek switch
        {
            PeekEdge.Right => new Rectangle(0, 0, tab, Height),
            PeekEdge.Left => new Rectangle(Width - tab, 0, tab, Height),
            _ => new Rectangle(0, Height - tab, Width, tab)
        };
        var alpha = (int)(255 * Math.Clamp(_tuck * 1.4f - 0.2f, 0, 1));
        using (var back = new SolidBrush(Color.FromArgb(alpha, Blend(_palette.Background, _accent, 0.25f))))
            g.FillRectangle(back, strip);

        var fill = TabFill();
        var filled = Peek == PeekEdge.Top
            ? new Rectangle(strip.Left, strip.Top, (int)(strip.Width * fill), strip.Height)
            : new Rectangle(strip.Left, strip.Bottom - (int)(strip.Height * fill), strip.Width, (int)(strip.Height * fill));
        using (var front = new SolidBrush(Color.FromArgb(alpha, _accent)))
            g.FillRectangle(front, filled);

        var arrow = Peek switch { PeekEdge.Right => "\u2039", PeekEdge.Left => "\u203a", _ => "\u02c5" };
        var arrowRect = Peek == PeekEdge.Top ? strip : new Rectangle(strip.Left, strip.Top + strip.Height / 2 - tab, strip.Width, tab * 2);
        DrawText(g, arrow, _titleFont, arrowRect, Color.FromArgb(alpha, _palette.Dark ? Color.White : Color.Black),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private static double EaseOutBack(double p)
    {
        const double c1 = 1.2, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(p - 1, 3) + c1 * Math.Pow(p - 1, 2);
    }

    // ------------------------------------------------------------------ layout

    private int MeasureLayout()
    {
        var pad = Px(16);
        var left = pad + (_palette.Stripe ? Px(6) : 0);
        var width = Px(BaseWidth) - left - pad;
        var y = pad - Px(2);

        _closeLabel = _palette.Terminal ? "[x]" : "✕";
        var closeSize = Px(22);
        _closeRect = new Rectangle(Px(BaseWidth) - pad - closeSize + Px(4), y - Px(2), closeSize, closeSize);

        // Copy, left of ✕: only on notifications about tickets (and their "Try it" samples, which aren't counted).
        var copyWidth = _palette.Terminal ? TextRenderer.MeasureText("[copied]", _smallFont).Width : closeSize; // a word in Terminal
        _copyRect = _content.IsTicketNotification || _content.ResolvedAction == Notifier.ActionNone
            ? new Rectangle(_closeRect.Left - copyWidth - Px(2), _closeRect.Top, copyWidth, closeSize)
            : Rectangle.Empty;

        var headingHeight = TextRenderer.MeasureText("Ag", _headingFont).Height;
        _headingRect = new Rectangle(left, y, (_copyRect.IsEmpty ? _closeRect.Left : _copyRect.Left) - left - Px(4), headingHeight);
        y += headingHeight + Px(6);

        // "Client: Contoso", large: the first thing to see.
        if (!string.IsNullOrWhiteSpace(_content.Client))
        {
            var clientHeight = TextRenderer.MeasureText("Ag", _clientFont).Height;
            _clientRect = new Rectangle(left, y, width, clientHeight);
            y += clientHeight + Px(2);

            // "Mute" at the right end of the client line: no more notifications from this client (Groups → Clients).
            if (_content.MuteTarget is not null)
            {
                var muteSize = TextRenderer.MeasureText(MuteLabel, _smallFont, Size.Empty, TextFormatFlags.NoPadding);
                var muteWidth = muteSize.Width + Px(8);
                _muteRect = new Rectangle(_clientRect.Right - muteWidth, _clientRect.Top, muteWidth, clientHeight);
                _clientRect.Width -= muteWidth + Px(6);
            }
            else
                _muteRect = Rectangle.Empty;
        }
        else
        {
            _clientRect = _muteRect = Rectangle.Empty;
        }

        var titleHeight = Measure(_content.Title, _titleFont, width, maxLines: 2);
        _titleRect = new Rectangle(left, y, width, titleHeight);
        y += titleHeight + Px(4);

        var bodyHeight = string.IsNullOrWhiteSpace(Body) ? 0 : _rich.Measure(Body, width, _content.MaxBodyLines);
        _bodyRect = new Rectangle(left, y, width, bodyHeight);
        y += bodyHeight + (bodyHeight > 0 ? Px(12) : Px(8));

        if (_palette.Terminal)
        {
            // ASCII countdown line above the footer.
            var lineHeight = TextRenderer.MeasureText("Ag", _bodyFont).Height;
            _barRect = new Rectangle(left, y, width, lineHeight);
            y += lineHeight + Px(6);
        }

        // The main button says what a click does.
        _openLabel = _content.ResolvedAction switch
        {
            Notifier.ActionUpdate => _palette.Terminal ? "[ update ]" : "Update now",
            Notifier.ActionSignIn => _palette.Terminal ? "[ sign in ]" : "Sign in",
            Notifier.ActionSettings => _palette.Terminal ? "[ settings ]" : "Open Settings",
            Notifier.ActionInfo => _palette.Terminal ? "[ close ]" : "Close",
            _ => _palette.Terminal ? "[ open ]" : _content.TicketId is null ? "Open Zendesk" : "Open"
        };
        // The second button: Snooze on ticket notifications, Close on everything else (summary, welcome, update), none
        // on quick "OK" messages.
        _snoozeLabel = SecondButton switch
        {
            Result.Snooze => _palette.Terminal ? "[ snooze ]" : "Snooze",
            Result.Dismiss => _palette.Terminal ? "[ close ]" : "Close",
            _ => ""
        };
        var buttonHeight = Px(30);
        _footerRect = new Rectangle(left, y, width, buttonHeight);
        _takeLabel = _content.CanCatchUp ? (_palette.Terminal ? "[ catch up ]" : "Catch up") : (_palette.Terminal ? "[ take ]" : "Take it");
        _dismissLabel = _palette.Terminal ? "[ close ]" : "Close";

        // Right to left: Open, Take it, Snooze; Close (the same as ✕ and right-click) always at the bottom left, so it's
        // in the same place on every notification. If they don't fit, slimmer buttons; if still not, no extra Close.
        var withDismiss = SecondButton == Result.Snooze;
        int Fit(int padding, bool dismiss) =>
            new[] { _openLabel, HasExtraButton ? _takeLabel : "", _snoozeLabel, dismiss ? DismissLabel : "" }
                .Where(l => l.Length > 0).Sum(l => TextRenderer.MeasureText(l, _smallFont).Width + Px(padding) + Px(8)) - Px(8);
        var padding = _palette.Terminal ? 8 : 28;
        if (Fit(padding, withDismiss) > width && !_palette.Terminal)
            padding = 16;
        if (Fit(padding, withDismiss) > width && _palette.Terminal)
            padding = 0; // the brackets already separate them
        if (Fit(padding, withDismiss) > width)
            withDismiss = false;
        int WidthOf(string label) => label.Length == 0 ? 0 : TextRenderer.MeasureText(label, _smallFont).Width + Px(padding);

        var openWidth = WidthOf(_openLabel);
        var snoozeWidth = WidthOf(_snoozeLabel);
        _openRect = new Rectangle(left + width - openWidth, y, openWidth, buttonHeight);
        var takeWidth = HasExtraButton ? WidthOf(_takeLabel) : 0;
        _takeRect = HasExtraButton ? new Rectangle(_openRect.Left - Px(8) - takeWidth, y, takeWidth, buttonHeight) : Rectangle.Empty;
        var snoozeRight = HasExtraButton ? _takeRect.Left : _openRect.Left;
        _snoozeRect = snoozeWidth == 0 ? new Rectangle(snoozeRight, y, 0, buttonHeight)
            : SecondButton == Result.Dismiss ? new Rectangle(left, y, snoozeWidth, buttonHeight) // "Close" on app messages: bottom left too
            : new Rectangle(snoozeRight - Px(8) - snoozeWidth, y, snoozeWidth, buttonHeight);
        var dismissWidth = withDismiss ? WidthOf(DismissLabel) : 0;
        _dismissRect = new Rectangle(left, y, dismissWidth, buttonHeight);

        y += buttonHeight + pad;

        if (!_palette.Terminal)
            _barRect = new Rectangle(0, y - Px(3), Px(BaseWidth), Px(3));

        return y;
    }

    private static int Measure(string text, Font font, int width, int maxLines)
    {
        var lineHeight = TextRenderer.MeasureText("Ag", font).Height;
        var size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        return Math.Min(size.Height, lineHeight * maxLines);
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = ClientRectangle;

        // Background
        using (var bg = new LinearGradientBrush(bounds, _palette.Background, _palette.BackgroundEnd, LinearGradientMode.Vertical))
            g.FillRectangle(bg, bounds);

        if (_palette.Terminal)
            PaintTerminalChrome(g, bounds);
        else if (_palette.Stripe)
            PaintStripe(g, bounds);

        // Heading + close
        var heading = _palette.Terminal
            ? $"> zendesk-watcher --{(_content.Kind == NotificationKind.NewTicket ? "new" : "update")}{(_content.PriorityShort is { } p ? $" {p.Replace(" ", "")}" : "")}{(_content.Context is null ? "" : $"  grp:{_content.Context}")}"
            : _content.FullHeading.ToUpperInvariant();
        var headingColor = _palette.Terminal ? _palette.Muted : _palette.Dark ? _accent : Blend(_accent, Color.Black, 0.1f);
        DrawText(g, heading, _headingFont, _headingRect, headingColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        var closeHover = _hoverButton == _closeRect;
        if (closeHover && !_palette.Terminal)
        {
            using var hoverBrush = new SolidBrush(Color.FromArgb(_palette.Dark ? 40 : 18, _palette.Text));
            g.FillEllipse(hoverBrush, _closeRect);
        }
        DrawText(g, _closeLabel, _smallFont, _closeRect, closeHover ? _palette.Text : _palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        if (!_copyRect.IsEmpty)
        {
            var copyHover = _hoverButton == _copyRect;
            var copied = _clock.Elapsed.TotalMilliseconds < _copiedUntil;
            if (copyHover && !_palette.Terminal)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(_palette.Dark ? 40 : 18, _palette.Text));
                g.FillEllipse(hoverBrush, _copyRect);
            }
            var color = copied ? _accent : copyHover ? _palette.Text : _palette.Muted;
            if (_palette.Terminal)
                DrawText(g, copied ? "[copied]" : "[copy]", _smallFont, _copyRect, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            else
            {
                // Segoe MDL2 Assets: E8C8 = copy, E73E = check mark.
                _iconFont ??= new Font("Segoe MDL2 Assets", _smallFont.SizeInPoints);
                DrawText(g, copied ? "\uE73E" : "\uE8C8", _iconFont, _copyRect, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        // Title and body (typed out in the terminal theme)
        if (!_clientRect.IsEmpty)
            PaintClient(g);
        var (title, body, typing) = Typed();
        DrawText(g, title, _titleFont, _titleRect, _palette.Text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (_bodyRect.Height > 0)
            _rich.Draw(g, body, _bodyRect, _content.MaxBodyLines, _palette.Terminal ? _palette.Text : _palette.Muted, _palette.Text);
        if (_palette.Terminal && (typing || (_clock.ElapsedMilliseconds / 500) % 2 == 0))
        {
            if (body.Length > 0 || (!typing && _bodyRect.Height > 0))
                PaintCursorAt(g, _rich.End(body, _bodyRect, _content.MaxBodyLines), _rich.LineHeight);
            else
                PaintCursor(g, title, _titleRect);
        }

        PaintFooter(g);
        PaintCountdown(g);

        PaintPeekTab(g);
    }

    private void PaintStripe(Graphics g, Rectangle bounds)
    {
        var stripe = Px(4);
        using (var brush = new SolidBrush(_accent))
            g.FillRectangle(brush, 0, 0, stripe, bounds.Height);

        if (_palette.Dark && _palette.Glow)
        {
            // Soft neon glow next to the stripe.
            for (var i = 1; i <= 6; i++)
            {
                using var glow = new SolidBrush(Color.FromArgb(Math.Max(4, 46 - i * 7), _accent));
                g.FillRectangle(glow, stripe, 0, Px(2) * i, bounds.Height);
            }
        }
    }

    private void PaintTerminalChrome(Graphics g, Rectangle bounds)
    {
        using (var scan = new Pen(Color.FromArgb(14, _palette.Text)))
        {
            for (var y = 0; y < bounds.Height; y += Px(3))
                g.DrawLine(scan, 0, y, bounds.Width, y);
        }

        using var border = new Pen(_palette.Border);
        g.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
    }

    /// <summary>"Client:" small and muted, the name large in the text color (bottoms aligned, so they share a baseline).</summary>
    private void PaintClient(Graphics g)
    {
        var label = _palette.Terminal ? "client:" : "Client:";
        var labelSize = TextRenderer.MeasureText(g, label, _bodyFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var nameHeight = _clientRect.Height;
        var baselineShift = nameHeight - labelSize.Height - Px(2);
        DrawText(g, label, _bodyFont, new Rectangle(_clientRect.Left, _clientRect.Top + baselineShift, labelSize.Width, labelSize.Height),
            _palette.Muted, TextFormatFlags.SingleLine);
        var nameLeft = _clientRect.Left + labelSize.Width + Px(6);
        DrawText(g, _content.Client!, _clientFont, new Rectangle(nameLeft, _clientRect.Top, _clientRect.Right - nameLeft, nameHeight),
            _palette.Text, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        if (!_muteRect.IsEmpty)
        {
            // On the name's baseline, like the "Client:" label; brighter on hover.
            var muteHeight = TextRenderer.MeasureText(g, MuteLabel, _smallFont, Size.Empty, TextFormatFlags.NoPadding).Height;
            var muteRect = new Rectangle(_muteRect.Left, _muteRect.Bottom - muteHeight - Px(3), _muteRect.Width, muteHeight);
            DrawText(g, MuteLabel, _smallFont, muteRect, _hoverButton == _muteRect ? _palette.Text : _palette.Muted,
                TextFormatFlags.SingleLine | TextFormatFlags.Right);
        }
    }

    private string MuteLabel => _palette.Terminal ? "[mute ▾]" : "Mute ▾";

    private void PaintCursorAt(Graphics g, Point at, int lineHeight)
    {
        using var brush = new SolidBrush(_palette.Text);
        g.FillRectangle(brush, at.X + Px(1), at.Y + Px(2), Px(7), lineHeight - Px(4));
    }

    private void PaintCursor(Graphics g, string text, Rectangle rect)
    {
        // Approximate the end of the last line of text.
        var lines = text.Split('\n');
        var last = lines[^1];
        var font = rect == _titleRect ? _titleFont : _bodyFont;
        var size = TextRenderer.MeasureText(g, last, font, new Size(rect.Width, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var full = TextRenderer.MeasureText(g, text, font, new Size(rect.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        var lineHeight = TextRenderer.MeasureText("Ag", font).Height;
        var x = rect.Left + Math.Min(size.Width % Math.Max(1, rect.Width), rect.Width - Px(8));
        var y = rect.Top + Math.Min(full.Height, rect.Height) - lineHeight;
        using var brush = new SolidBrush(_palette.Text);
        g.FillRectangle(brush, x + Px(1), y + Px(2), Px(7), lineHeight - Px(4));
    }

    private void PaintFooter(Graphics g)
    {
        if (_dismissRect.Width > 0)
            PaintButton(g, _dismissRect, DismissLabel, primary: false);
        if (_snoozeRect.Width > 0)
            PaintButton(g, _snoozeRect, _snoozeLabel, primary: false);
        if (HasExtraButton)
            PaintButton(g, _takeRect, _takeLabel, primary: false);
        PaintButton(g, _openRect, _openLabel, primary: true);
    }

    private void PaintButton(Graphics g, Rectangle rect, string label, bool primary)
    {
        var hover = _hoverButton == rect;

        if (_palette.Terminal)
        {
            if (hover)
            {
                using var inv = new SolidBrush(_palette.Text);
                g.FillRectangle(inv, rect);
            }
            DrawText(g, label, _smallFont, rect, hover ? _palette.Background : (primary ? _palette.Text : _palette.Muted),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var radius = _palette.ButtonRadius == UiPalette.Pill ? rect.Height / 2f : Math.Min(_palette.ButtonRadius * _s, rect.Height / 2f);
        using var path = Theme.RoundedRect(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), radius);
        if (primary)
        {
            using var fill = new SolidBrush(hover ? Blend(_accent, _palette.Dark ? Color.White : Color.Black, 0.15f) : _accent);
            g.FillPath(fill, path);
            DrawText(g, label, _smallFont, rect, _palette.OnAccent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            using var fill = new SolidBrush(Color.FromArgb(hover ? (_palette.Dark ? 40 : 20) : 0, _palette.Text));
            using var pen = new Pen(_palette.Dark ? Color.FromArgb(70, _palette.Text) : _palette.Border);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            DrawText(g, label, _smallFont, rect, _palette.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private void PaintCountdown(Graphics g)
    {
        var remaining = _durationMs <= 0 ? 1 : Math.Clamp(1 - _elapsed / _durationMs, 0, 1);

        if (_palette.Terminal)
        {
            const int cells = 24;
            var filled = (int)Math.Round(cells * remaining);
            var seconds = _durationMs <= 0 ? "--" : $"{Math.Ceiling((_durationMs - _elapsed) / 1000)}s";
            var bar = _durationMs <= 0
                ? "[ pinned until dismissed ]"
                : $"[{new string('█', filled)}{new string('░', cells - filled)}] {seconds}{(_hover ? " (paused)" : !MyTurn ? " (waiting)" : Away ? " (you're away)" : "")}";
            DrawText(g, bar, _bodyFont, _barRect, _palette.Muted, TextFormatFlags.SingleLine);
            return;
        }

        if (_durationMs <= 0)
            return;

        var width = (int)(_barRect.Width * remaining);
        using var track = new SolidBrush(Color.FromArgb(_palette.Dark ? 30 : 16, _palette.Text));
        g.FillRectangle(track, _barRect);
        if (width > 0)
        {
            using var brush = new LinearGradientBrush(_barRect, _accent, Blend(_accent, _palette.Dark ? Color.White : _accent, 0.35f), LinearGradientMode.Horizontal);
            g.FillRectangle(brush, new Rectangle(_barRect.X, _barRect.Y, width, _barRect.Height));
        }
    }

    /// <summary>The body as of now (e.g. "Created 14:02 · 5 min ago" keeps counting while shown).</summary>
    private string Body => _content.DisplayBody(DateTimeOffset.UtcNow);

    private (string Title, string Body, bool Typing) Typed()
    {
        if (!_palette.Terminal)
            return (_content.Title, Body, false);

        var shown = (int)((_clock.Elapsed.TotalMilliseconds) / 1000 * TypingCharsPerSecond);
        var title = _content.Title;
        var body = Body;
        var bodyLength = Markdown.Strip(body).Length; // what shows, without the ** markers
        if (shown >= title.Length + bodyLength)
            return (title, body, false);

        return shown <= title.Length
            ? (title[..shown], "", true)
            : (title, Markdown.Truncate(body, shown - title.Length), true);
    }

    private static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseEnter(EventArgs e) { SetHover(true, _hoverButton); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        _leftAt = _clock.Elapsed.TotalMilliseconds;
        _forcedOutUntil = 0; // came out by itself and was looked at: it may tuck away again
        SetHover(false, null);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        SetHover(true, new[] { _closeRect, _copyRect, _muteRect, _openRect, _snoozeRect, _takeRect, _dismissRect }.Cast<Rectangle?>().FirstOrDefault(r => r!.Value.Contains(e.Location)));
        base.OnMouseMove(e);
    }

    /// <summary>Hover pauses the countdown and highlights buttons; repaint only when that changes.</summary>
    private void SetHover(bool hover, Rectangle? button)
    {
        hover |= _menuOpen;
        if (hover == _hover && button == _hoverButton)
            return;
        _hover = hover;
        _hoverButton = button;
        if (hover && Peek != PeekEdge.None)
            _timer.Interval = BusyInterval; // slide out smoothly
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (Peek != PeekEdge.None && _tuck > 0.3f)
            return; // still sliding out: a click on the tab only brings it out
        if (e.Button == MouseButtons.Right || _closeRect.Contains(e.Location) || _dismissRect.Contains(e.Location))
            Dismiss(Result.Dismiss);
        else if (_muteRect.Contains(e.Location))
        {
            var menu = ClientMenu.Build(_content.MuteTarget!, choice =>
            {
                ClientChosen?.Invoke(this, choice);
                Dismiss(Result.Dismiss);
            });
            _menuOpen = true;
            menu.Closed += (_, _) => { _menuOpen = false; SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)), null); };
            menu.Show(this, new Point(_muteRect.Left, _muteRect.Bottom));
        }
        else if (_copyRect.Contains(e.Location))
        {
            CopyClicked?.Invoke(this);
            _copiedUntil = _clock.Elapsed.TotalMilliseconds + 1500;
            Invalidate();
        }
        else if (_snoozeRect.Contains(e.Location))
            Dismiss(SecondButton);
        else if (HasExtraButton && _takeRect.Contains(e.Location))
            Dismiss(_content.CanCatchUp ? Result.CatchUp : Result.Take);
        else
            Dismiss(Result.Open); // the Open button or anywhere on the card
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Developer helper: saves each sample notification in each popup theme as a PNG.</summary>
    public static void RenderSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var theme in new[] { NotificationTheme.Optimizely, NotificationTheme.NeonDark, NotificationTheme.Terminal, NotificationTheme.CleanLight, NotificationTheme.CozyPurple, NotificationTheme.Night, NotificationTheme.Retro })
        {
            var i = 0;
            foreach (var content in Notifier.AllSamples())
            {
                using var form = new PopupForm(content, PopupPalette.For(theme), 0, 1f) { _phase = Phase.Showing };
                using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(dir, $"{theme}-{i++}.png"));
            }
        }
    }

    private int Px(int value) => (int)Math.Round(value * _s);

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));

    private static bool FontExists(string family)
    {
        using var font = new Font(family, 9f);
        return string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _headingFont.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
            _bodyBoldFont.Dispose();
            _clientFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private static volatile bool _sessionLocked;

    /// <summary>Follows Windows+L / automatic locking (Windows tells running apps). Started once by the popup manager.</summary>
    internal static void WatchSessionLock() => Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
            _sessionLocked = true;
        else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            _sessionLocked = false;
    };

    /// <summary>No mouse or keyboard input for this long: the user is away, and popups don't count down.</summary>
    internal static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(1);

    /// <summary>
    /// True when the screen is locked, or there's been no input for <see cref="AwayAfter"/>: countdowns pause, so notifications are still
    /// there when the user comes back. Cheap (one Windows call), checked on each animation tick.
    /// </summary>
    internal static bool UserAway()
    {
        if (_sessionLocked)
            return true; // locked: away right away, not only after a minute
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return false;
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return idleMs >= AwayAfter.TotalMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
