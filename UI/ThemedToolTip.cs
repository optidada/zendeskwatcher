namespace ZendeskWatcher.UI;

/// <summary>
/// A tooltip in the app's colors instead of Windows' white or yellow box: the theme's surface, its border and text,
/// the small font, with a little padding. Settings uses the app theme (also after a theme change); notifications pass
/// their own palette's colors.
/// </summary>
public sealed class ThemedToolTip : ToolTip
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
    private const int MaxWidth = 420;

    /// <summary>Background, border and text colors; null: the app theme's (read when shown).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<(Color Back, Color Border, Color Text)>? Colors { get; init; }

    private (Color Back, Color Border, Color Text) Current => Colors?.Invoke() ?? (Theme.Surface, Theme.Border, Theme.Text);

    public ThemedToolTip()
    {
        OwnerDraw = true;
        Popup += (_, e) =>
        {
            var s = (e.AssociatedControl?.DeviceDpi ?? 96) / 96f;
            e.ToolTipSize = Measure(GetToolTip(e.AssociatedControl!) ?? "", s);
        };
        Draw += (_, e) => Paint(e.Graphics, e.Bounds, e.ToolTipText ?? "", Current, (e.AssociatedControl?.DeviceDpi ?? 96) / 96f);
    }

    /// <summary>The size a tip needs at this scale.</summary>
    public static Size Measure(string text, float s)
    {
        var size = TextRenderer.MeasureText(text, Theme.Small, new Size((int)(MaxWidth * s), int.MaxValue), Flags);
        return new Size(size.Width + (int)(16 * s), size.Height + (int)(10 * s));
    }

    /// <summary>Draws a tip: background, a 1-pixel border, the text with padding (also for the rendered samples).</summary>
    public static void Paint(Graphics g, Rectangle bounds, string text, (Color Back, Color Border, Color Text) colors, float s)
    {
        using (var fill = new SolidBrush(colors.Back))
            g.FillRectangle(fill, bounds);
        using (var pen = new Pen(colors.Border))
            g.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var inner = new Rectangle(bounds.X + (int)(8 * s), bounds.Y + (int)(5 * s), bounds.Width - (int)(16 * s), bounds.Height - (int)(10 * s));
        TextRenderer.DrawText(g, text, Theme.Small, inner, colors.Text, Flags);
    }
}
