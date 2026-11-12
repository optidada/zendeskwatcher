namespace ZendeskWatcher.UI;

/// <summary>
/// A flat button drawn with the current theme's corner shape (<see cref="UiPalette.ButtonRadius"/>): square, rounded
/// or fully round. Colors come from the usual Button properties, so <see cref="Theme.Recolor"/> keeps working.
/// </summary>
public sealed class RoundButton : Button
{
    private bool _hover, _down;

    public RoundButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
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

        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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
