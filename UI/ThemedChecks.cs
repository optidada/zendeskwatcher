using System.Drawing.Drawing2D;
using System.Windows.Forms.VisualStyles;

namespace ZendeskWatcher.UI;

/// <summary>
/// A check box drawn in the theme's colors (Windows draws a blue box whatever the theme): an accent-filled box with a
/// tick when checked, a neutral outline when not. Same size as the Windows one, so layouts don't move.
/// </summary>
public sealed class ThemedCheckBox : CheckBox
{
    private bool _hover;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var glyph = CheckBoxRenderer.GetGlyphSize(g, CheckBoxState.UncheckedNormal);
        ThemedGlyph.Background(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = DeviceDpi / 96f;
        var size = glyph.Height;
        var box = new RectangleF(0.5f, (Height - size) / 2f, size - 1, size - 1);

        using (var path = Theme.RoundedRect(box, 2.5f * s))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.Border);
                g.FillPath(fill, path);
                using var tick = new Pen(Enabled ? Theme.OnAccent : Theme.Surface, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                var u = box.Width / 13f; // the tick in a 13-unit box
                g.DrawLines(tick, [
                    new PointF(box.X + 3 * u, box.Y + 6.8f * u), new PointF(box.X + 5.6f * u, box.Y + 9.3f * u), new PointF(box.X + 10 * u, box.Y + 4 * u)
                ]);
            }
            else
            {
                using var border = new Pen(Enabled && _hover ? Theme.Accent : Theme.Neutral, 1.2f * s);
                g.DrawPath(border, path);
            }
        }
        ThemedGlyph.Text(this, g, glyph.Width, Focused && ShowFocusCues);
    }
}

/// <summary>A radio button drawn in the theme's colors: an accent ring with a dot when selected.</summary>
public sealed class ThemedRadioButton : RadioButton
{
    private bool _hover;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var glyph = RadioButtonRenderer.GetGlyphSize(g, RadioButtonState.UncheckedNormal);
        ThemedGlyph.Background(this, g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = DeviceDpi / 96f;
        var size = glyph.Height;
        var circle = new RectangleF(0.75f, (Height - size) / 2f + 0.25f, size - 1.5f, size - 1.5f);

        var ring = !Enabled ? Theme.Border : Checked || _hover ? Theme.Accent : Theme.Neutral;
        using (var pen = new Pen(ring, 1.2f * s))
            g.DrawEllipse(pen, circle);
        if (Checked)
        {
            var dot = RectangleF.Inflate(circle, -circle.Width * 0.27f, -circle.Height * 0.27f);
            using var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.Border);
            g.FillEllipse(fill, dot);
        }
        ThemedGlyph.Text(this, g, glyph.Width, Focused && ShowFocusCues);
    }
}

internal static class ThemedGlyph
{
    public static void Background(Control control, Graphics g)
    {
        using var back = new SolidBrush(control.BackColor);
        g.FillRectangle(back, control.ClientRectangle);
    }

    /// <summary>The text next to the glyph, where Windows puts it; muted when disabled (Windows' gray is unreadable on dark themes).</summary>
    public static void Text(ButtonBase control, Graphics g, int glyphWidth, bool focusCue)
    {
        var left = glyphWidth + control.Padding.Left + 5;
        var area = new Rectangle(left, 0, control.Width - left, control.Height);
        TextRenderer.DrawText(g, control.Text, control.Font, area, control.Enabled ? control.ForeColor : Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (focusCue)
        {
            var size = TextRenderer.MeasureText(g, control.Text, control.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            ControlPaint.DrawFocusRectangle(g, new Rectangle(left - 1, (control.Height - size.Height) / 2 - 1, size.Width + 2, size.Height + 2));
        }
    }
}
