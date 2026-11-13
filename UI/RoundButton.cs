namespace ZendeskWatcher.UI;

/// <summary>
/// A flat button drawn with the current theme's corner shape (<see cref="UiPalette.ButtonRadius"/>): square, rounded
/// or fully round. Colors come from the usual Button properties, so <see cref="Theme.Recolor"/> keeps working.
/// </summary>
public sealed class RoundButton : Button
{
    private bool _hover, _down;

    /// <summary>Draws a symbol instead of the text (e.g. the play triangle), in the given color and client area.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<Graphics, Color, Rectangle>? Glyph { get; set; }

    public RoundButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    // ------------------------------------------------------------------ busy

    private System.Windows.Forms.Timer? _spin;
    private float _angle;
    private string? _idleText;
    private Padding _idlePadding;

    /// <summary>Working on something (<see cref="SetBusy"/>): a small spinning arc before the text, clicks are ignored.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>
    /// Shows that the click is being worked on: a spinner before the text (optionally another text, e.g. "Checking…")
    /// until called with false, which brings the old text back. The spinner only runs while busy.
    /// </summary>
    public void SetBusy(bool busy, string? text = null)
    {
        if (busy == IsBusy)
        {
            if (busy && text is not null)
                Text = text;
            return;
        }
        IsBusy = busy;
        if (busy)
        {
            _idleText = Text;
            _idlePadding = Padding;
            if (text is not null)
                Text = text;
            Padding = new Padding(_idlePadding.Left + (int)(SpinnerSpace * DeviceDpi / 96f), _idlePadding.Top, _idlePadding.Right, _idlePadding.Bottom); // room for the spinner
            if (_spin is null)
            {
                _spin = new System.Windows.Forms.Timer { Interval = 40 };
                _spin.Tick += (_, _) => { _angle = (_angle + 24) % 360; Invalidate(); };
            }
            _spin.Start();
            Cursor = Cursors.AppStarting;
        }
        else
        {
            _spin?.Stop();
            Text = _idleText ?? Text;
            Padding = _idlePadding;
            Cursor = Cursors.Default;
        }
        Invalidate();
    }

    /// <summary>Space for the spinner, at 96 dpi.</summary>
    private const int SpinnerSpace = 22;

    protected override void OnClick(EventArgs e)
    {
        if (!IsBusy)
            base.OnClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _spin?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var behind = Parent?.BackColor is { A: > 0 } parent ? parent : Theme.Surface;
        g.Clear(behind);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var radius = Theme.ButtonRadius(rect.Height, DeviceDpi / 96f);
        var fill = Enabled && (_hover || _down) ? FlatAppearance.MouseOverBackColor : BackColor;
        var border = FlatAppearance.BorderColor;
        var text = ForeColor;
        if (!Enabled)
        {
            fill = Blend(fill, behind, 0.5f);
            border = Blend(border, behind, 0.5f);
            text = Blend(text, behind, 0.5f);
        }

        using (var path = radius > 0 ? Theme.RoundedRect(rect, radius) : Rectangle(rect))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        if (Focused && ShowFocusCues)
        {
            var inner = RectangleF.Inflate(rect, -3, -3);
            using var focus = Theme.ButtonRadius(inner.Height, DeviceDpi / 96f) is var r and > 0 ? Theme.RoundedRect(inner, r) : Rectangle(inner);
            using var dotted = new Pen(Blend(text, fill, 0.4f)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
            g.DrawPath(dotted, focus);
        }

        if (Glyph is { } glyph)
            glyph(g, text, ClientRectangle);
        else if (IsBusy)
        {
            // Spinner and text centered together: an arc that turns, then the text.
            var s = DeviceDpi / 96f;
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            var size = TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
            var arc = 12 * s;
            var gap = 8 * s;
            var left = (Width - (arc + gap + size.Width)) / 2f;
            using (var pen = new Pen(text, 2 * s) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                g.DrawArc(pen, left, (Height - arc) / 2f, arc, arc, _angle, 270);
            TextRenderer.DrawText(g, Text, Font, new Point((int)(left + arc + gap), (Height - size.Height) / 2), text, flags);
        }
        else
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rectangle(RectangleF r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddRectangle(r);
        return path;
    }

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(255,
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}
