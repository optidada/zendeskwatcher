using System.Windows.Forms.VisualStyles;

namespace ZendeskWatcher.UI;

/// <summary>
/// Windows draws the text of a disabled check box / radio button in a fixed dark gray, which is unreadable on the
/// dark themes. These redraw the text of a disabled control in the theme's muted color.
/// </summary>
public sealed class ThemedCheckBox : CheckBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Enabled)
            DisabledText.Paint(this, e.Graphics, CheckBoxRenderer.GetGlyphSize(e.Graphics, CheckBoxState.UncheckedDisabled).Width);
    }
}

public sealed class ThemedRadioButton : RadioButton
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!Enabled)
            DisabledText.Paint(this, e.Graphics, RadioButtonRenderer.GetGlyphSize(e.Graphics, RadioButtonState.UncheckedDisabled).Width);
    }
}

internal static class DisabledText
{
    /// <summary>Paints over the text next to the glyph with the same text in <see cref="Theme.TextMuted"/>.</summary>
    public static void Paint(ButtonBase control, Graphics g, int glyphWidth)
    {
        var left = glyphWidth + control.Padding.Left + 5;
        var area = new Rectangle(left, 0, control.Width - left, control.Height);
        using (var back = new SolidBrush(control.BackColor))
            g.FillRectangle(back, area);
        TextRenderer.DrawText(g, control.Text, control.Font, area, Theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
