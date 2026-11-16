using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// "A pill at the top" (Look &amp; sound → How they appear): a small rounded pill at the top center saying what's there
/// ("2 new · 1 P1"). Pointing at it (or a click) opens the notifications right below it, laid out like a list; moving
/// away closes them again after a moment. The cards are normal <see cref="PopupForm"/>s made by <see cref="PopupManager"/>
/// (StartHidden); this shows and hides them. A card counts down only once it has been seen (opened), like Peek.
/// Urgent and important ones, and app messages, open the pill by themselves for a few seconds.
/// </summary>
internal sealed class PillHost : Form
{
    private readonly Func<IReadOnlyList<PopupForm>> _cards;
    private readonly HashSet<PopupForm> _seen = [];
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 100 }; // only while open
    private string _text = "";
    private PopupPalette? _palette;
    private float _s = 1;
    private Color _accent = Color.Gray;
    private bool _expanded;
    private bool _held;
    private bool _hover;
    private DateTime _outsideSince = DateTime.MaxValue;
    private DateTime _openUntil = DateTime.MinValue;

    /// <summary>How long after the mouse left the pill and its cards they close.</summary>
    private static readonly TimeSpan CloseAfter = TimeSpan.FromMilliseconds(700);

    /// <summary>The pill opened or closed: the countdowns change (seen ones count down).</summary>
    public event Action? ExpandedChanged;

    public PillHost(Func<IReadOnlyList<PopupForm>> cards)
    {
        _cards = cards;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Cursor = Cursors.Hand;
        _watch.Tick += (_, _) => Watch();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x00080000, WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public bool Expanded => _expanded;

    /// <summary>Opened at least once while this card was there: it counts down (in turn) from then on.</summary>
    public bool Seen(PopupForm card) => _seen.Contains(card);

    /// <summary>The pill's top margin and height at this scale; the cards start below it.</summary>
    public static int CardsTop(Rectangle area, float scale) => area.Top + PillMargin(scale) + PillHeight(scale) + (int)(10 * scale);

    private static int PillMargin(float scale) => (int)(10 * scale);
    private static int PillHeight(float scale) => (int)(34 * scale);

    /// <summary>While you present (or a full-screen app runs) the pill and its cards hide.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Held
    {
        get => _held;
        set
        {
            if (_held == value)
                return;
            _held = value;
            Apply();
        }
    }

    /// <summary>What's there now (on screen and waiting): updates the text, and shows or hides the pill and the cards.</summary>
    public void Sync(IReadOnlyList<NotificationContent> all, PopupPalette palette, float scale)
    {
        _palette = palette;
        _s = scale;
        _text = CompactText.PillSummary(all);
        var top = all.OrderBy(c => PriorityRank(c.Priority)).FirstOrDefault();
        _accent = top is null ? palette.Muted : palette.Accent(top.Priority, top.Kind);
        _seen.RemoveWhere(c => !_cards().Contains(c));
        if (all.Count == 0)
            _expanded = false;
        Apply();
    }

    private static int PriorityRank(string? priority) => priority?.ToLowerInvariant() switch
    {
        "urgent" => 0, "high" => 1, "normal" => 2, "low" => 3, _ => 4
    };

    /// <summary>Opens by itself for a while (urgent, important, app messages), then closes unless the mouse is there.</summary>
    public void OpenFor(TimeSpan time)
    {
        _openUntil = DateTime.UtcNow + time;
        SetExpanded(true);
    }

    /// <summary>Another way of appearing was chosen: the cards show as they are and the pill goes.</summary>
    public void Release()
    {
        foreach (var card in _cards())
        {
            card.StartHidden = false;
            if (!card.Visible && !card.IsDisposed)
                card.Show();
        }
        _expanded = false;
        _watch.Stop();
        Hide();
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
            return;
        _expanded = expanded;
        _outsideSince = DateTime.MaxValue;
        Apply();
        ExpandedChanged?.Invoke();
    }

    /// <summary>Shows or hides the pill and the cards as the state says; the watch timer only runs while open.</summary>
    private void Apply()
    {
        var any = _text.Length > 0 && _palette is not null;
        if (!any || _held)
        {
            foreach (var card in _cards())
                if (card.Visible) card.Hide();
            if (Visible) Hide();
            _watch.Stop();
            return;
        }
        Render();
        if (!Visible)
            Show();
        foreach (var card in _cards())
        {
            if (card.IsDisposed)
                continue;
            if (_expanded)
            {
                _seen.Add(card);
                if (!card.Visible)
                {
                    card.Show();
                    card.RaiseWithoutActivating();
                }
            }
            else if (card.Visible)
                card.Hide();
        }
        if (_expanded) _watch.Start(); else _watch.Stop();
    }

    /// <summary>While open: closes it a moment after the mouse left the pill and its cards (not while it opened by itself).</summary>
    private void Watch()
    {
        var mouse = Cursor.Position;
        var gap = (int)(12 * _s);
        var inside = Bounds.Contains(mouse) || _cards().Any(c => c.Visible && Rectangle.Inflate(c.Bounds, gap, gap).Contains(mouse))
            || DateTime.UtcNow < _openUntil
            || _cards().Any(c => !c.IsDisposed && c.MenuOpen); // a card's menu (or its Assign question) is open
        if (inside)
            _outsideSince = DateTime.MaxValue;
        else if (_outsideSince == DateTime.MaxValue)
            _outsideSince = DateTime.UtcNow;
        else if (DateTime.UtcNow - _outsideSince >= CloseAfter)
            SetExpanded(false);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        SetExpanded(true);
        Render();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        Render();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        SetExpanded(true);
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>The pill with per-pixel transparency (smooth round ends), centered at the top of the work area.</summary>
    private void Render()
    {
        if (_palette is null)
            return;
        using var bitmap = Draw(_text, _palette, _accent, _s, _expanded, _hover);
        var area = PopupForm.WorkArea(Screen.PrimaryScreen ?? Screen.AllScreens[0]);
        var location = new Point(area.Left + (area.Width - bitmap.Width) / 2, area.Top + PillMargin(_s));
        Bounds = new Rectangle(location, bitmap.Size);
        SetBitmap(bitmap, location);
    }

    /// <summary>Draws the pill: a dot in the color of the most urgent one, the summary, and an arrow (open or closed).</summary>
    internal static Bitmap Draw(string text, PopupPalette palette, Color accent, float s, bool expanded, bool hover)
    {
        var family = palette.FontFamily;
        using (var probeFont = new Font(family, 9f))
            if (!string.Equals(probeFont.Name, family, StringComparison.OrdinalIgnoreCase))
                family = palette.Terminal ? "Consolas" : "Segoe UI";
        // Drawn on a 96 dpi bitmap: the font grows with the whole scale (screen and notification size).
        using var font = new Font(family, (palette.FontSize + 0.5f) * s, palette.Terminal ? FontStyle.Regular : FontStyle.Bold, GraphicsUnit.Point);
        var label = palette.Terminal ? $"> {text}" : text;
        var arrow = expanded ? "▴" : "▾";
        Size textSize;
        using (var probe = new Bitmap(1, 1))
        using (var measure = Graphics.FromImage(probe))
            textSize = Size.Ceiling(measure.MeasureString(label, font, PointF.Empty, StringFormat.GenericTypographic));
        var height = PillHeight(s);
        var dot = (int)(10 * s);
        var pad = (int)(14 * s);
        var arrowWidth = (int)(14 * s);
        var width = Math.Max((int)(120 * s), pad + dot + (int)(8 * s) + textSize.Width + (int)(8 * s) + arrowWidth + pad);

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var rect = new RectangleF(0.5f, 0.5f, width - 1.5f, height - 1.5f);
        using var path = palette.Terminal ? new GraphicsPath() : Theme.RoundedRect(rect, height / 2f);
        if (palette.Terminal)
            path.AddRectangle(rect);
        var back = hover ? Blend(palette.Background, palette.Dark ? Color.White : Color.Black, 0.06f) : palette.Background;
        using (var fill = new LinearGradientBrush(new Rectangle(0, 0, width, height), back, palette.BackgroundEnd, LinearGradientMode.Vertical))
            g.FillPath(fill, path);
        using (var border = new Pen(palette.Terminal ? palette.Border : Blend(palette.Border, accent, 0.45f), Math.Max(1, s)))
            g.DrawPath(border, path);

        var x = pad;
        using (var dotBrush = new SolidBrush(accent))
        {
            if (palette.Terminal) g.FillRectangle(dotBrush, x, (height - dot) / 2, dot, dot);
            else g.FillEllipse(dotBrush, x, (height - dot) / 2f, dot, dot);
        }
        x += dot + (int)(8 * s);
        using (var textBrush = new SolidBrush(palette.Text))
            g.DrawString(label, font, textBrush, x, (height - textSize.Height) / 2f, StringFormat.GenericTypographic);
        using (var arrowBrush = new SolidBrush(palette.Muted))
            g.DrawString(arrow, font, arrowBrush, width - pad - arrowWidth, (height - textSize.Height) / 2f, StringFormat.GenericTypographic);
        return bitmap;
    }

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));

    private void SetBitmap(Bitmap bitmap, Point location)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBitmap);
            var size = new SIZE { cx = bitmap.Width, cy = bitmap.Height };
            var source = new POINT();
            var top = new POINT { x = location.X, y = location.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref top, ref size, memDc, ref source, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _watch.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
