using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// "A ticker at the screen edge" (Look &amp; sound → How they appear): a thin bar across the bottom (or top, as the
/// position says) of the screen, scrolling each notification's one line from right to left like a news ticker.
/// Click an item to open it, right-click for Take it / Copy / Snooze / Close; the mouse over the bar pauses it; ✕
/// closes everything. Items take turns: the oldest counts down its normal time, then leaves. The bar hides when empty.
/// Cheap while scrolling: the line is drawn once into a bitmap and only copied each frame; no timer while hidden.
/// </summary>
internal sealed class TickerBar : Form
{
    /// <summary>More than this wait in line, like more than four cards do.</summary>
    public const int MaxItems = 8;

    /// <summary>Scrolling speed at 96 dpi, pixels per second.</summary>
    private const double Speed = 80;

    private const int BusyInterval = 16;  // ~60 fps while scrolling
    private const int CalmInterval = 250; // mouse over it (paused) or away: nothing moves

    private sealed class Item(NotificationContent content, double durationMs)
    {
        public NotificationContent Content { get; } = content;
        public double DurationMs { get; } = durationMs; // 0 = until closed
        public double Elapsed { get; set; }
        public DateTime ShownAt { get; } = DateTime.UtcNow;
        public int Start { get; set; }  // in the strip
        public int Width { get; set; }
        public Color Accent { get; set; }
    }

    private readonly List<Item> _items = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = BusyInterval };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;
    private PopupPalette _palette = PopupPalette.For(NotificationTheme.Optimizely);
    private NotificationTheme _theme = NotificationTheme.Optimizely;
    private float _s = 1;
    private float _size = 1;
    private bool _top;
    private Font? _font;
    private Font? _badgeFont;

    // The scrolling line, drawn once per change: a GDI bitmap in a memory DC, copied with BitBlt each frame.
    private IntPtr _stripDc, _stripBitmap, _stripOld;
    private int _stripWidth;
    private double _offset; // how far the line has moved in from the right edge
    private bool _hover;
    private bool _menuOpen;
    private bool _held;
    private Point _mouse = new(-1, -1);

    /// <summary>An item left: clicked (Open), Take it, Snooze, closed, timed out, or None (no longer needed).</summary>
    public event Action<NotificationContent, PopupForm.Result>? Finished;

    /// <summary>Copy from an item's menu (ticket notifications).</summary>
    public event Action<NotificationContent>? CopyClicked;

    /// <summary>Don't count down while the user is away (Behavior); set from the settings.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PauseWhenAway { get; set; } = true;

    public TickerBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        _timer.Tick += (_, _) => Tick();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>At the top for the top positions, otherwise along the bottom.</summary>
    public static bool AtTop(Settings settings) => settings.Position is PopupPosition.TopRight or PopupPosition.TopCenter;

    public int Count => _items.Count;

    /// <summary>The items and when each appeared (to drop ones no longer needed after being away).</summary>
    public IReadOnlyList<(NotificationContent Content, DateTime ShownAt)> Items => _items.Select(i => (i.Content, i.ShownAt)).ToList();

    /// <summary>Hidden (and still) while you present or use a full-screen app; back when that's over.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Held
    {
        get => _held;
        set
        {
            if (_held == value)
                return;
            _held = value;
            UpdateVisibility();
        }
    }

    /// <summary>Adds an item at the end of the line; the bar appears if it was hidden.</summary>
    /// <param name="seconds">Its stay time; 0 = until closed.</param>
    public void Add(NotificationContent content, int seconds, PopupPalette palette, NotificationTheme theme, float dpi, float size, bool top)
    {
        var styleChanged = _palette != palette || Math.Abs(_s - dpi * size) > 0.001f || _top != top;
        _palette = palette;
        _theme = theme;
        _s = dpi * size;
        _size = size;
        _top = top;
        if (styleChanged || _font is null)
        {
            _font?.Dispose();
            _badgeFont?.Dispose();
            var family = FontExists(palette.FontFamily) ? palette.FontFamily : palette.Terminal ? "Consolas" : "Segoe UI";
            _font = new Font(family, palette.FontSize * size, palette.Terminal ? FontStyle.Regular : FontStyle.Bold);
            _badgeFont = new Font(family, (palette.FontSize - 0.5f) * size, FontStyle.Bold);
        }
        if (_items.Count == 0)
            _offset = 0; // comes in from the right edge
        _items.Add(new Item(content, Math.Max(0, seconds) * 1000.0));
        BuildStrip();
        UpdateVisibility();
    }

    /// <summary>Takes an item out of the line and reports how it went.</summary>
    public void Remove(NotificationContent content, PopupForm.Result outcome)
    {
        var index = _items.FindIndex(i => ReferenceEquals(i.Content, content));
        if (index < 0)
            return;
        var item = _items[index];
        _items.RemoveAt(index);
        if (index == 0 && _items.Count > 0)
            _offset -= _items[0].Start - item.Start; // the ones after it stay where they are on screen
        BuildStrip();
        UpdateVisibility();
        Finished?.Invoke(content, outcome);
    }

    /// <summary>Takes out (uncounted) the items that match, e.g. the same Catch up again.</summary>
    public void RemoveWhere(Func<NotificationContent, bool> match)
    {
        foreach (var item in _items.Where(i => match(i.Content)).ToList())
            Remove(item.Content, PopupForm.Result.None);
    }

    private int Px(double value) => (int)Math.Round(value * _s);
    private int BarHeight => Px(30);
    private int BadgeWidth => Px(46);
    private int CloseWidth => Px(_palette.Terminal ? 40 : 30); // "[x]" is wider than ✕
    private Rectangle CloseRect => new(Width - CloseWidth, 0, CloseWidth, BarHeight);
    private Rectangle View => new(BadgeWidth, 0, Math.Max(0, Width - BadgeWidth - CloseWidth), BarHeight);

    /// <summary>One loop of the line: at least the width of the view, so a short line crosses the whole bar.</summary>
    private int Period => Math.Max(_stripWidth, View.Width);

    /// <summary>Made only for a still (--render-modes): never shown.</summary>
    private bool _still;

    private void UpdateVisibility()
    {
        if (_still)
            return;
        if (_items.Count == 0 || _held)
        {
            _timer.Stop();
            if (Visible)
                Hide();
            return;
        }
        var area = PopupForm.WorkArea(Screen.PrimaryScreen ?? Screen.AllScreens[0]);
        Bounds = new Rectangle(area.Left, _top ? area.Top : area.Bottom - BarHeight, area.Width, BarHeight);
        if (!Visible)
            Show();
        _lastTick = _clock.Elapsed.TotalMilliseconds;
        _timer.Start();
        Invalidate();
    }

    // ------------------------------------------------------------------ the line

    private string Separator => _palette.Terminal ? "  |  " : "   •   ";

    /// <summary>Draws all items once into the strip: a priority tag, the item's line, a separator.</summary>
    private void BuildStrip()
    {
        FreeStrip();
        if (_items.Count == 0 || _font is null)
        {
            _stripWidth = 0;
            return;
        }
        const TextFormatFlags Flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        var height = BarHeight;
        var widths = new List<int>();
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
        {
            foreach (var item in _items)
                widths.Add(TagWidth(item.Content) + TextRenderer.MeasureText(g, Line(item.Content), _font, Size.Empty, Flags).Width);
            widths.Add(TextRenderer.MeasureText(g, Separator, _font, Size.Empty, Flags).Width);
        }
        var separator = widths[^1];
        _stripWidth = widths.Take(_items.Count).Sum() + separator * _items.Count;

        using var strip = new Bitmap(Math.Max(1, _stripWidth), height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(strip))
        {
            g.Clear(_palette.Background);
            var x = 0;
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                item.Start = x;
                item.Width = widths[i];
                item.Accent = _palette.Accent(item.Content.Priority, item.Content.Kind);
                var tag = PriorityTag.Short(item.Content.Priority);
                if (tag is not null && !_palette.Terminal)
                    x += PriorityTag.Draw(g, tag, item.Accent, x, height / 2, _s) + Px(6);
                else if (tag is not null)
                {
                    var text = $"[{tag}] ";
                    var w = TextRenderer.MeasureText(g, text, _font, Size.Empty, Flags).Width;
                    TextRenderer.DrawText(g, text, _font, new Rectangle(x, 0, w, height), item.Accent, Flags);
                    x += w;
                }
                var line = Line(item.Content);
                var lineWidth = item.Start + widths[i] - x;
                TextRenderer.DrawText(g, line, _font, new Rectangle(x, 0, lineWidth, height), _palette.Text, Flags);
                x += lineWidth;
                TextRenderer.DrawText(g, Separator, _font, new Rectangle(x, 0, separator, height), _palette.Muted, Flags);
                x += separator;
            }
        }
        var screen = GetDC(IntPtr.Zero);
        _stripDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        _stripBitmap = strip.GetHbitmap();
        _stripOld = SelectObject(_stripDc, _stripBitmap);
        if (_offset >= Period)
            _offset %= Period;
        Invalidate();
    }

    private static string Line(NotificationContent content) => CompactText.TickerItem(content);

    private int TagWidth(NotificationContent content)
    {
        if (PriorityTag.Short(content.Priority) is not { } tag)
            return 0;
        if (!_palette.Terminal)
            return PriorityTag.Measure(tag, _s).Width + Px(6);
        return TextRenderer.MeasureText($"[{tag}] ", _font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
    }

    /// <summary>The accent of the most urgent item (the edge and the count).</summary>
    private Color EdgeAccent => _items.OrderBy(i => Rank(i.Content.Priority)).FirstOrDefault()?.Accent ?? _palette.Accent("normal", NotificationKind.NewTicket);

    // A frame of the moving line is put together here (plain GDI) and copied to the window in one go: no WM_PAINT,
    // no GDI+, about a tenth of the work of repainting. Full paints (shown, hover, a change) go through OnPaint.
    private IntPtr _frameDc, _frameBitmap, _frameOld;
    private Size _frameSize;

    private void DrawFrame()
    {
        var view = View;
        if (!IsHandleCreated || !Visible || view.Width <= 0 || _stripDc == IntPtr.Zero)
            return;
        if (_frameDc == IntPtr.Zero || _frameSize != view.Size)
        {
            FreeFrame();
            var screen = GetDC(IntPtr.Zero);
            _frameDc = CreateCompatibleDC(screen);
            _frameBitmap = CreateCompatibleBitmap(screen, view.Width, view.Height);
            ReleaseDC(IntPtr.Zero, screen);
            _frameOld = SelectObject(_frameDc, _frameBitmap);
            _frameSize = view.Size;
        }
        Fill(new Rectangle(0, 0, view.Width, view.Height), _palette.Background);
        foreach (var origin in Origins())
        {
            var left = Math.Max(view.Left, origin);
            var right = Math.Min(view.Right, origin + _stripWidth);
            if (right > left)
                BitBlt(_frameDc, left - view.Left, 0, right - left, view.Height, _stripDc, left - origin, 0, 0x00CC0020 /* SRCCOPY */);
        }
        if (Current is { } current)
        {
            var remaining = Math.Clamp(1 - current.Elapsed / current.DurationMs, 0, 1);
            foreach (var origin in Origins())
            {
                var rect = new Rectangle(origin + current.Start, BarHeight - Px(3), (int)(current.Width * remaining), Px(2));
                rect.Intersect(view);
                rect.Offset(-view.Left, 0);
                if (rect.Width > 0)
                    Fill(rect, current.Accent);
            }
        }
        Fill(new Rectangle(0, _top ? BarHeight - Px(2) : 0, view.Width, Px(2)), EdgeAccent);
        var window = GetDC(Handle);
        BitBlt(window, view.Left, 0, view.Width, view.Height, _frameDc, 0, 0, 0x00CC0020);
        ReleaseDC(Handle, window);
    }

    private void Fill(Rectangle rect, Color color)
    {
        var brush = CreateSolidBrush(ColorTranslator.ToWin32(color));
        var r = new RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        FillRect(_frameDc, ref r, brush);
        DeleteObject(brush);
    }

    private void FreeFrame()
    {
        if (_frameDc == IntPtr.Zero)
            return;
        SelectObject(_frameDc, _frameOld);
        DeleteObject(_frameBitmap);
        DeleteDC(_frameDc);
        _frameDc = _frameBitmap = _frameOld = IntPtr.Zero;
    }

    private void FreeStrip()
    {
        if (_stripDc == IntPtr.Zero)
            return;
        SelectObject(_stripDc, _stripOld);
        DeleteObject(_stripBitmap);
        DeleteDC(_stripDc);
        _stripDc = _stripBitmap = _stripOld = IntPtr.Zero;
    }

    /// <summary>Where the copies of the line start (x in the window); usually one, two while one loop follows another.</summary>
    private IEnumerable<int> Origins()
    {
        var view = View;
        if (_stripWidth == 0 || view.Width == 0)
            yield break;
        var first = view.Right - (int)Math.Round(_offset);
        for (var x = first - Period; x < view.Right; x += Period)
            if (x + _stripWidth > view.Left)
                yield return x;
    }

    private Item? ItemAt(Point at)
    {
        if (!View.Contains(at))
            return null;
        foreach (var origin in Origins())
        {
            var local = at.X - origin;
            if (_items.FirstOrDefault(i => local >= i.Start && local < i.Start + i.Width) is { } item)
                return item;
        }
        return null;
    }

    /// <summary>Whose turn it is to count down: the oldest that times out (the others wait, as cards do).</summary>
    private Item? Current => _items.FirstOrDefault(i => i.DurationMs > 0);

    // ------------------------------------------------------------------ animation

    private void Tick()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var dt = Math.Min(200, now - _lastTick);
        _lastTick = now;
        var away = PauseWhenAway && PopupForm.UserAway();
        var paused = _hover || _menuOpen;
        if (!paused && !away)
        {
            _offset += Speed * _s * dt / 1000;
            if (Period > 0 && _offset >= Period)
                _offset -= Period;
            if (Current is { } current)
            {
                current.Elapsed += dt;
                if (current.Elapsed >= current.DurationMs)
                {
                    Remove(current.Content, PopupForm.Result.Timeout);
                    return;
                }
            }
            DrawFrame();
        }
        var interval = paused || away ? CalmInterval : BusyInterval;
        if (_timer.Interval != interval)
            _timer.Interval = interval;
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e) => PaintBar(e.Graphics, ClientSize, e.ClipRectangle);

    /// <param name="clip">What needs painting: while scrolling only the line, so the count and ✕ aren't drawn again each frame.</param>
    private void PaintBar(Graphics g, Size size, Rectangle? clip = null)
    {
        var bounds = new Rectangle(Point.Empty, size);
        var all = clip is not { } c || c.Left < BadgeWidth || c.Right > size.Width - CloseWidth;
        var view = new Rectangle(BadgeWidth, 0, Math.Max(0, size.Width - BadgeWidth - CloseWidth), BarHeight);
        using (var back = new SolidBrush(_palette.Background))
            g.FillRectangle(back, all ? bounds : view);

        // The line: copied from the strip, clipped to the view.
        if (_stripDc != IntPtr.Zero)
        {
            var hdc = g.GetHdc();
            try
            {
                foreach (var origin in Origins())
                {
                    var left = Math.Max(view.Left, origin);
                    var right = Math.Min(view.Right, origin + _stripWidth);
                    if (right > left)
                        BitBlt(hdc, left, 0, right - left, BarHeight, _stripDc, left - origin, 0, 0x00CC0020 /* SRCCOPY */);
                }
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        // The countdown of the one whose turn it is: a thin line under it that shrinks.
        if (Current is { } current)
        {
            var remaining = Math.Clamp(1 - current.Elapsed / current.DurationMs, 0, 1);
            using var brush = new SolidBrush(current.Accent);
            foreach (var origin in Origins())
            {
                var rect = new Rectangle(origin + current.Start, BarHeight - Px(3), (int)(current.Width * remaining), Px(2));
                rect.Intersect(view);
                if (rect.Width > 0)
                    g.FillRectangle(brush, rect);
            }
        }

        // The edge facing the screen in the accent of the most urgent item; the count on the left, ✕ on the right.
        var accent = EdgeAccent;
        using (var edge = new SolidBrush(accent))
            g.FillRectangle(edge, 0, _top ? BarHeight - Px(2) : 0, size.Width, Px(2));
        if (!all)
            return;
        using (var line = new Pen(_palette.Border))
        {
            g.DrawLine(line, view.Left - 1, Px(6), view.Left - 1, BarHeight - Px(6));
            g.DrawLine(line, view.Right, Px(6), view.Right, BarHeight - Px(6));
        }
        if (_badgeFont is not null)
        {
            const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            var count = _items.Count.ToString();
            if (_palette.Terminal)
                TextRenderer.DrawText(g, $"[{count}]", _badgeFont, new Rectangle(0, 0, BadgeWidth, BarHeight), accent, Center);
            else
            {
                var badge = new Rectangle(Px(10), Px(7), BadgeWidth - Px(20), BarHeight - Px(14));
                var smoothing = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = Theme.RoundedRect(badge, badge.Height / 2f))
                using (var fill = new SolidBrush(accent))
                    g.FillPath(fill, path);
                g.SmoothingMode = smoothing;
                TextRenderer.DrawText(g, count, _badgeFont, badge, Contrast.On(accent, Color.White), Center);
            }
            var close = new Rectangle(size.Width - CloseWidth, 0, CloseWidth, BarHeight);
            TextRenderer.DrawText(g, _palette.Terminal ? "[x]" : "✕", _badgeFont, close,
                close.Contains(_mouse) ? _palette.Text : _palette.Muted, Center);
        }
    }

    private static int Rank(string? priority) => priority?.ToLowerInvariant() switch
    {
        "urgent" => 0, "high" => 1, "normal" => 2, "low" => 3, _ => 4
    };

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var wasOnClose = CloseRect.Contains(_mouse);
        _mouse = e.Location;
        if (!_hover)
        {
            _hover = true;
            Invalidate();
        }
        Cursor = ItemAt(e.Location) is not null || CloseRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        if (wasOnClose != CloseRect.Contains(e.Location))
            Invalidate(CloseRect);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _mouse = new Point(-1, -1);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && CloseRect.Contains(e.Location))
        {
            foreach (var item in _items.ToList())
                Remove(item.Content, PopupForm.Result.Dismiss);
            return;
        }
        if (ItemAt(e.Location) is not { } clicked)
            return;
        if (e.Button == MouseButtons.Left)
            Remove(clicked.Content, PopupForm.Result.Open);
        else if (e.Button == MouseButtons.Right)
            ShowMenu(clicked, e.Location);
    }

    /// <summary>The buttons a card has, as a menu: Open, Take it, Copy, Snooze, Close.</summary>
    private void ShowMenu(Item item, Point at)
    {
        var content = item.Content;
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        void Add(string text, Action click) => menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => click()));
        var info = content.ResolvedAction == Notifier.ActionInfo;
        Add(info ? "Close" : content.TicketCount > 1 ? "See all" : content.TicketId is null ? "Open" : $"Open #{content.TicketId}",
            () => Remove(content, PopupForm.Result.Open));
        if (content.CanTake)
            Add("Take it", () => Remove(content, PopupForm.Result.Take));
        var ticket = content.IsTicketNotification || content.ResolvedAction == Notifier.ActionNone;
        if (ticket)
            Add("Copy", () => CopyClicked?.Invoke(content));
        if (ticket && !info)
            Add("Snooze", () => Remove(content, PopupForm.Result.Snooze));
        if (!info)
            Add("Close", () => Remove(content, PopupForm.Result.Dismiss));
        ThemedMenu.Apply(menu, UiPalette.For(_theme));
        _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            BeginInvoke(menu.Dispose);
        };
        menu.Show(this, at);
    }

    // ------------------------------------------------------------------ developer helper

    /// <summary>A still of the bar with these items, scrolled a little (for --render-modes).</summary>
    internal static Bitmap RenderStill(IReadOnlyList<NotificationContent> contents, NotificationTheme theme, int width, double offset)
    {
        using var bar = new TickerBar { _still = true };
        foreach (var content in contents)
            bar.Add(content, 10, PopupPalette.For(theme), theme, 1f, 1f, top: false);
        bar.Size = new Size(width, bar.BarHeight);
        bar._offset = offset;
        bar._items[0].Elapsed = 4000; // its countdown partly gone
        var bitmap = new Bitmap(width, bar.BarHeight);
        using (var g = Graphics.FromImage(bitmap))
            bar.PaintBar(g, bitmap.Size);
        return bitmap;
    }

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
            _font?.Dispose();
            _badgeFont?.Dispose();
        }
        FreeStrip();
        FreeFrame();
        base.Dispose(disposing);
    }

    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
}
