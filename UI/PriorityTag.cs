using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// A priority as a small colored tag ("P1" on red, "P2" on orange, "P3" in the accent, "P4" grey), easier to read than
/// "(P1)" in a line of text. Used next to labels in Settings and in notification headings.
/// </summary>
public static class PriorityTag
{
    private static readonly Font TagFont = new("Segoe UI Semibold", 7.5f);

    /// <summary>"urgent" → "P1" … "low" → "P4"; null for anything else.</summary>
    public static string? Short(string? priority) => priority?.ToLowerInvariant() switch
    {
        "urgent" => "P1",
        "high" => "P2",
        "normal" => "P3",
        "low" => "P4",
        _ => null
    };

    /// <summary>The tag's color in the Settings window's theme.</summary>
    public static Color ColorOf(string priority) => priority.ToLowerInvariant() switch
    {
        "urgent" => Theme.Danger,
        "high" => Theme.Warning,
        "normal" => Theme.Accent,
        _ => Theme.Neutral
    };

    /// <summary>The size of a tag at this scale (96 dpi = 1).</summary>
    public static Size Measure(string text, float s)
    {
        var size = TextRenderer.MeasureText(text, TagFont, Size.Empty, TextFormatFlags.NoPadding);
        return new Size(size.Width + (int)(9 * s), size.Height + (int)(3 * s));
    }

    /// <summary>Draws a tag with its left edge at <paramref name="x"/>, centered on <paramref name="centerY"/>; returns its width.</summary>
    public static int Draw(Graphics g, string text, Color fill, int x, int centerY, float s)
    {
        var size = Measure(text, s);
        var rect = new Rectangle(x, centerY - size.Height / 2, size.Width, size.Height);
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundedRect(rect, 3 * s))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);
        g.SmoothingMode = mode;
        // White, or near-black where white would be too faint (orange, lime, light grey).
        TextRenderer.DrawText(g, text, TagFont, rect, Contrast.On(fill, Color.White),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return size.Width;
    }
}

/// <summary>A heading made of priority tags and an optional text, e.g. [P1] Urgent, or just [P1] [P2].</summary>
public sealed class PriorityLabel : Control
{
    private readonly string[] _priorities;

    public PriorityLabel(string text, params string[] priorities)
    {
        _priorities = priorities;
        Text = text;
        Font = Theme.Small;
        ForeColor = Theme.TextMuted;
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
    }

    private const int Gap = 4;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = DeviceDpi / 96f;
        var tags = _priorities.Sum(p => PriorityTag.Measure(PriorityTag.Short(p) ?? p, s).Width + (int)(Gap * s));
        var text = Text.Length == 0 ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var height = Math.Max(text.Height, PriorityTag.Measure("P1", s).Height) + (int)(4 * s);
        return new Size(tags + text.Width + (int)(2 * s), height);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (AutoSize) Size = GetPreferredSize(Size.Empty); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var s = DeviceDpi / 96f;
        var x = 0;
        foreach (var priority in _priorities)
            x += PriorityTag.Draw(g, PriorityTag.Short(priority) ?? priority, Enabled ? PriorityTag.ColorOf(priority) : Theme.Border, x, Height / 2, s) + (int)(Gap * s);
        if (Text.Length > 0)
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}
