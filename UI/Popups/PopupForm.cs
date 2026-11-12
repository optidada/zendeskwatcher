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
    public enum Result { None, Open, Take, Snooze, Dismiss, Timeout }

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
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Layout (client coordinates)
    private Rectangle _headingRect, _titleRect, _bodyRect, _footerRect, _barRect, _closeRect, _openRect, _snoozeRect, _takeRect, _chipRect;
    private string _openLabel = "", _snoozeLabel = "", _closeLabel = "", _takeLabel = "", _chipLabel = "";

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

    public Result Outcome { get; private set; }
    public NotificationContent Content => _content;

    /// <summary>The Y position the stack wants this popup at; it glides there.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int TargetY { get; set; }

    public event Action<PopupForm>? Finished;

    public PopupForm(NotificationContent content, PopupPalette palette, ToastDisplay display, float scale)
    {
        _content = content;
        _palette = palette;
        _s = scale;
        _durationMs = display switch
        {
            ToastDisplay.Long => 25_000,
            ToastDisplay.UntilDismissed => 0,
            _ => 7_000
        };
        _accent = palette.Accent(content.Priority, content.Kind);

        var family = FontExists(palette.FontFamily) ? palette.FontFamily : (palette.Terminal ? "Consolas" : "Segoe UI");
        _headingFont = new Font(family, palette.FontSize - 1f, palette.Terminal ? FontStyle.Regular : FontStyle.Bold);
        _titleFont = new Font(family, palette.FontSize + (palette.Terminal ? 0.5f : 1.5f), FontStyle.Bold);
        _bodyFont = new Font(family, palette.FontSize);
        _smallFont = new Font(family, palette.FontSize - 1f, palette.Terminal ? FontStyle.Regular : FontStyle.Bold);

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
            : ColorTranslator.ToWin32(_palette.Stripe && _palette.Dark ? Blend(_palette.Border, _accent, 0.35f) : _palette.Border);
        DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    /// <summary>Shows the popup sliding in from the screen edge at (restX, TargetY).</summary>
    public void Present(int restX)
    {
        _restX = restX;
        _y = TargetY;
        Location = new Point(restX + Width + Px(24), TargetY);
        Show();
        _phaseStart = _lastTick = _clock.Elapsed.TotalMilliseconds;
        _timer.Start();
    }

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
        var hidden = Width + Px(24);

        // Glide vertically towards the stack position.
        _y += (TargetY - _y) * (float)Math.Min(1, dt / 90);

        int x;
        switch (_phase)
        {
            case Phase.Entering:
                var pIn = Math.Min(1, t / EnterMs);
                x = _restX + (int)(hidden * (1 - EaseOutBack(pIn)));
                if (pIn >= 1)
                {
                    _phase = Phase.Showing;
                    _phaseStart = now;
                }
                break;

            case Phase.Leaving:
                var pOut = Math.Min(1, t / LeaveMs);
                x = _restX + (int)(hidden * pOut * pOut);
                if (pOut >= 1)
                {
                    _timer.Stop();
                    Finished?.Invoke(this);
                    Close();
                    return;
                }
                break;

            default:
                x = _restX;
                if (_durationMs > 0 && !_hover)
                {
                    _elapsed += dt;
                    if (_elapsed >= _durationMs)
                        Dismiss(Result.Timeout);
                }
                break;
        }

        var location = new Point(x, (int)Math.Round(_y));
        if (location != Location)
            Location = location;

        Invalidate();
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

        var headingHeight = TextRenderer.MeasureText("Ag", _headingFont).Height;
        _headingRect = new Rectangle(left, y, width - closeSize, headingHeight);
        y += headingHeight + Px(6);

        var titleHeight = Measure(_content.Title, _titleFont, width, maxLines: 2);
        _titleRect = new Rectangle(left, y, width, titleHeight);
        y += titleHeight + Px(4);

        var bodyHeight = string.IsNullOrWhiteSpace(_content.Body) ? 0 : Measure(_content.Body, _bodyFont, width, maxLines: 5);
        _bodyRect = new Rectangle(left, y, width, bodyHeight);
        y += bodyHeight + (bodyHeight > 0 ? Px(12) : Px(8));

        if (_palette.Terminal)
        {
            // ASCII countdown line above the footer.
            var lineHeight = TextRenderer.MeasureText("Ag", _bodyFont).Height;
            _barRect = new Rectangle(left, y, width, lineHeight);
            y += lineHeight + Px(6);
        }

        // Priority chip: left of the buttons, or on its own line above them when it doesn't fit.
        _chipLabel = _content.PriorityLabel is { } priority
            ? (_palette.Terminal ? $"prio:{priority.ToUpperInvariant().Replace(" ", "")}" : $"● {priority.ToUpperInvariant()}")
            : "";
        var chipText = _chipLabel.Length > 0 ? TextRenderer.MeasureText(_chipLabel, _smallFont) : Size.Empty;
        var chipSize = chipText.IsEmpty ? Size.Empty : new Size(chipText.Width + Px(_palette.Terminal ? 0 : 12), chipText.Height + Px(6));

        _openLabel = _palette.Terminal ? "[ open ]" : _content.TicketId is null ? "Open Zendesk" : "Open";
        _snoozeLabel = _palette.Terminal ? "[ snooze ]" : "Snooze";
        var buttonHeight = Px(30);
        _footerRect = new Rectangle(left, y, width, buttonHeight);
        var openWidth = TextRenderer.MeasureText(_openLabel, _smallFont).Width + Px(_palette.Terminal ? 8 : 28);
        var snoozeWidth = TextRenderer.MeasureText(_snoozeLabel, _smallFont).Width + Px(_palette.Terminal ? 8 : 28);
        _openRect = new Rectangle(left + width - openWidth, y, openWidth, buttonHeight);
        _takeLabel = _palette.Terminal ? "[ take ]" : "Take it";
        var takeWidth = _content.CanTake ? TextRenderer.MeasureText(_takeLabel, _smallFont).Width + Px(_palette.Terminal ? 8 : 28) : 0;
        _takeRect = _content.CanTake ? new Rectangle(_openRect.Left - Px(8) - takeWidth, y, takeWidth, buttonHeight) : Rectangle.Empty;
        var snoozeRight = _content.CanTake ? _takeRect.Left : _openRect.Left;
        _snoozeRect = new Rectangle(snoozeRight - Px(8) - snoozeWidth, y, snoozeWidth, buttonHeight);

        if (!chipSize.IsEmpty && left + chipSize.Width + Px(8) > _snoozeRect.Left)
        {
            _chipRect = new Rectangle(new Point(left, y), chipSize);
            var shift = chipSize.Height + Px(8);
            y += shift;
            _footerRect.Offset(0, shift);
            _openRect.Offset(0, shift);
            _takeRect.Offset(0, _content.CanTake ? shift : 0);
            _snoozeRect.Offset(0, shift);
        }
        else if (!chipSize.IsEmpty)
        {
            _chipRect = new Rectangle(new Point(left, y + (buttonHeight - chipSize.Height) / 2), chipSize);
        }
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
            ? $"> zendesk-watcher --{(_content.Kind == NotificationKind.NewTicket ? "new" : "update")}{(_content.Context is null ? "" : $"  grp:{_content.Context}")}"
            : (_content.Context is null ? _content.Heading : $"{_content.Heading} · {_content.Context}").ToUpperInvariant();
        var headingColor = _palette.Terminal ? _palette.Muted : _palette.Dark ? _accent : Blend(_accent, Color.Black, 0.1f);
        DrawText(g, heading, _headingFont, _headingRect, headingColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        var closeHover = _hoverButton == _closeRect;
        if (closeHover && !_palette.Terminal)
        {
            using var hoverBrush = new SolidBrush(Color.FromArgb(_palette.Dark ? 40 : 18, _palette.Text));
            g.FillEllipse(hoverBrush, _closeRect);
        }
        DrawText(g, _closeLabel, _smallFont, _closeRect, closeHover ? _palette.Text : _palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // Title and body (typed out in the terminal theme)
        var (title, body, typing) = Typed();
        DrawText(g, title, _titleFont, _titleRect, _palette.Text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (_bodyRect.Height > 0)
            DrawText(g, body, _bodyFont, _bodyRect, _palette.Terminal ? _palette.Text : _palette.Muted, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (_palette.Terminal && (typing || (_clock.ElapsedMilliseconds / 500) % 2 == 0))
            PaintCursor(g, typing ? (body.Length > 0 ? body : title) : (_bodyRect.Height > 0 ? body : title), typing && body.Length == 0 ? _titleRect : (_bodyRect.Height > 0 ? _bodyRect : _titleRect));

        PaintFooter(g);
        PaintCountdown(g);
    }

    private void PaintStripe(Graphics g, Rectangle bounds)
    {
        var stripe = Px(4);
        using (var brush = new SolidBrush(_accent))
            g.FillRectangle(brush, 0, 0, stripe, bounds.Height);

        if (_palette.Dark)
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
        // Priority chip on the left.
        if (_chipLabel.Length > 0)
        {
            var chip = _chipRect;
            var label = _chipLabel;
            if (!_palette.Terminal)
            {
                using var path = Theme.RoundedRect(chip, chip.Height / 2f);
                using var fill = new SolidBrush(Color.FromArgb(_palette.Dark ? 40 : 24, _accent));
                g.FillPath(fill, path);
            }
            DrawText(g, label, _smallFont, chip, _accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        PaintButton(g, _snoozeRect, _snoozeLabel, primary: false);
        if (_content.CanTake)
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

        using var path = Theme.RoundedRect(new RectangleF(rect.X + 0.5f, rect.Y + 0.5f, rect.Width - 1, rect.Height - 1), rect.Height / 2f);
        if (primary)
        {
            using var fill = new SolidBrush(hover ? Blend(_accent, _palette.Dark ? Color.White : Color.Black, 0.15f) : _accent);
            g.FillPath(fill, path);
            DrawText(g, label, _smallFont, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
                : $"[{new string('█', filled)}{new string('░', cells - filled)}] {seconds}{(_hover ? " (paused)" : "")}";
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

    private (string Title, string Body, bool Typing) Typed()
    {
        if (!_palette.Terminal)
            return (_content.Title, _content.Body, false);

        var shown = (int)((_clock.Elapsed.TotalMilliseconds) / 1000 * TypingCharsPerSecond);
        var title = _content.Title;
        var body = _content.Body;
        if (shown >= title.Length + body.Length)
            return (title, body, false);

        return shown <= title.Length
            ? (title[..shown], "", true)
            : (title, body[..(shown - title.Length)], true);
    }

    private static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseEnter(EventArgs e) { _hover = true; base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _hoverButton = null;
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hover = true;
        _hoverButton = new[] { _closeRect, _openRect, _snoozeRect, _takeRect }.Cast<Rectangle?>().FirstOrDefault(r => r!.Value.Contains(e.Location));
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right || _closeRect.Contains(e.Location))
            Dismiss(Result.Dismiss);
        else if (_snoozeRect.Contains(e.Location))
            Dismiss(Result.Snooze);
        else if (_content.CanTake && _takeRect.Contains(e.Location))
            Dismiss(Result.Take);
        else
            Dismiss(Result.Open); // the Open button or anywhere on the card
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Developer helper: saves each sample notification in each popup theme as a PNG.</summary>
    public static void RenderSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var theme in new[] { NotificationTheme.NeonDark, NotificationTheme.Terminal, NotificationTheme.CleanLight })
        {
            var i = 0;
            foreach (var content in Notifier.AllSamples())
            {
                using var form = new PopupForm(content, PopupPalette.For(theme), ToastDisplay.UntilDismissed, 1f) { _phase = Phase.Showing };
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
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
