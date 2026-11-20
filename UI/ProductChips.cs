namespace ZendeskWatcher.UI;

/// <summary>
/// The picked products as buttons in a row (wrapping), each "Name ×": a click removes it. Painted in one control, like
/// the products on a client's card, but in the accent color so they stand out.
/// </summary>
public sealed class ProductChips : Control
{
    private List<(string Value, string Name)> _items = [];
    private readonly List<(Rectangle Rect, string Value)> _hits = [];
    private int _hover = -1;
    private readonly ThemedToolTip _tip = new(); // in the app's colors

    /// <summary>A product's × was clicked: its value.</summary>
    public event Action<string>? Removed;

    /// <summary>Shown when nothing is picked.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";

    /// <summary>Shown under the picked products, in an amber band (a caution); "" for none.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string CautionText { get; set; } = "";

    public ProductChips()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    public void SetItems(IEnumerable<(string Value, string Name)> items)
    {
        _items = [.. items];
        _hover = -1;
        Height = Arrange(null);
        Invalidate();
    }

    private float S => DeviceDpi / 96f;
    private int P(int px) => (int)Math.Round(px * S);

    private const TextFormatFlags Measure = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    private const TextFormatFlags Line = Measure | TextFormatFlags.EndEllipsis;

    /// <summary>Places (and with <paramref name="g"/> paints) the chips; returns the height they need.</summary>
    private int Arrange(Graphics? g)
    {
        _hits.Clear();
        var font = Theme.Body;
        var textHeight = TextRenderer.MeasureText("Ag", font, Size.Empty, Measure).Height;
        if (_items.Count == 0)
        {
            // Nothing picked is a state worth noticing (every product notifies): a soft accent band with an accent edge.
            var band = new Rectangle(0, P(2), Width - 1, textHeight + P(16));
            if (g is not null)
            {
                using var soft = new SolidBrush(Theme.AccentSoft);
                if (Math.Min(Theme.ButtonRadius(band.Height, S), band.Height / 2f) is var radius and > 0)
                {
                    using var path = Theme.RoundedRect(band, Math.Min(radius, P(6)));
                    g.FillPath(soft, path);
                }
                else
                    g.FillRectangle(soft, band);
                using var edge = new SolidBrush(Theme.Accent);
                g.FillRectangle(edge, band.X, band.Y + P(4), P(3), band.Height - P(8));
                TextRenderer.DrawText(g, EmptyText, font, new Rectangle(P(14), band.Y + P(8), Width - P(20), textHeight), Theme.Text, Line);
            }
            return band.Bottom + P(6);
        }
        var chipHeight = textHeight + P(12);
        const string Cross = "×";
        var crossWidth = TextRenderer.MeasureText(Cross, font, Size.Empty, Measure).Width;
        int x = 0, y = P(2);
        for (var i = 0; i < _items.Count; i++)
        {
            var name = _items[i].Name;
            var textWidth = Math.Min(Width - P(36) - crossWidth, TextRenderer.MeasureText(name, font, Size.Empty, Measure).Width + 1);
            var chipWidth = P(12) + textWidth + P(8) + crossWidth + P(10);
            if (x > 0 && x + chipWidth > Width)
            {
                x = 0;
                y += chipHeight + P(6);
            }
            var rect = new Rectangle(x, y, chipWidth, chipHeight);
            _hits.Add((rect, _items[i].Value));
            if (g is not null)
            {
                using var brush = new SolidBrush(i == _hover ? Theme.AccentHover : Theme.Accent);
                // Square-cornered themes have no radius (a zero arc can't be drawn).
                if (Math.Min(Theme.ButtonRadius(chipHeight, S), chipHeight / 2f) is var radius and > 0)
                {
                    using var path = Theme.RoundedRect(rect, radius);
                    g.FillPath(brush, path);
                }
                else
                    g.FillRectangle(brush, rect);
                var textY = y + (chipHeight - textHeight) / 2;
                TextRenderer.DrawText(g, name, font, new Rectangle(x + P(12), textY, textWidth, textHeight), Theme.OnAccent, Line);
                TextRenderer.DrawText(g, Cross, font, new Rectangle(x + P(12) + textWidth + P(8), textY, crossWidth, textHeight), Theme.OnAccent, Line);
            }
            x += chipWidth + P(6);
        }
        y += chipHeight + P(4);
        if (CautionText.Length > 0)
            y = Caution(g, y + P(6)) + P(4);
        return y;
    }

    /// <summary>
    /// The caution under the picks (e.g. the Product field can be wrong): a soft amber band with an amber edge and a ⚠,
    /// the text wrapped in the normal text color. Returns where it ends.
    /// </summary>
    private int Caution(Graphics? g, int top)
    {
        const TextFormatFlags Wrap = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        var font = Theme.Body;
        var signWidth = TextRenderer.MeasureText("⚠", font, Size.Empty, Measure).Width;
        var textLeft = P(14) + signWidth + P(6);
        var textWidth = Math.Max(1, Width - textLeft - P(10));
        var textHeight = TextRenderer.MeasureText(CautionText, font, new Size(textWidth, int.MaxValue), Wrap).Height;
        var band = new Rectangle(0, top, Width - 1, textHeight + P(16));
        if (g is not null)
        {
            var w = Theme.Warning;
            var surface = Theme.Surface;
            using var soft = new SolidBrush(Color.FromArgb(surface.R + (w.R - surface.R) * 15 / 100, surface.G + (w.G - surface.G) * 15 / 100, surface.B + (w.B - surface.B) * 15 / 100));
            if (Math.Min(Theme.ButtonRadius(band.Height, S), P(6)) is var radius and > 0)
            {
                using var path = Theme.RoundedRect(band, radius);
                g.FillPath(soft, path);
            }
            else
                g.FillRectangle(soft, band);
            using var edge = new SolidBrush(w);
            g.FillRectangle(edge, band.X, band.Y + P(4), P(3), band.Height - P(8));
            TextRenderer.DrawText(g, "⚠", font, new Rectangle(P(14), band.Y + P(8), signWidth, textHeight), w, Measure);
            TextRenderer.DrawText(g, CautionText, font, new Rectangle(textLeft, band.Y + P(8), textWidth, textHeight), Theme.Text, Wrap);
        }
        return band.Bottom;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Arrange(e.Graphics);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var height = Arrange(null);
        if (height != Height)
            Height = height;
    }

    private int HitAt(Point p) => _hits.FindIndex(h => h.Rect.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = HitAt(e.Location);
        if (hover == _hover)
            return;
        _hover = hover;
        Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        _tip.SetToolTip(this, hover >= 0 ? "Remove" : null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0)
            return;
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && HitAt(e.Location) is >= 0 and var i)
            Removed?.Invoke(_hits[i].Value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }
}
