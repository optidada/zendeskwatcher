using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// Extra looks for a notification card, set when it's made: a badge column on the left and words before the heading.
/// Used by "Achievement unlocked" (Look &amp; sound → How they appear).
/// </summary>
/// <param name="HeadingPrefix">Before the heading, e.g. "Achievement unlocked · " (upper case in most themes).</param>
/// <param name="TerminalCommand">The command in Terminal's heading, instead of "zendesk-watcher".</param>
/// <param name="BadgeWidth">The badge column at 96 dpi and 100%; the card gets this much wider.</param>
/// <param name="Widen">More width on top of that (room for a longer heading).</param>
/// <param name="PaintBadge">Draws the badge in the column (graphics, column, palette, accent, scale).</param>
internal sealed record PopupDecoration(string HeadingPrefix, string TerminalCommand, int BadgeWidth, int Widen,
    Action<Graphics, Rectangle, PopupPalette, Color, float> PaintBadge);

/// <summary>
/// "Achievement unlocked": the card rises from the bottom center with a small bounce (<see cref="PopupForm.Entry.Bounce"/>),
/// a pixel-art trophy on the left and "ACHIEVEMENT UNLOCKED · NEW TICKET" as its heading, with a short tune
/// (<see cref="ChipSounds.PlayAchievement"/>). One at a time; otherwise a normal notification.
/// </summary>
internal static class AchievementStyle
{
    public static readonly PopupDecoration Decoration = new("Achievement unlocked · ", "achievement-unlocked", 64, 100, PaintTrophy);

    // A 12×13 pixel trophy: # gold, o shade, h highlight, b the base.
    private static readonly string[] Trophy =
    [
        "..########..",
        "oo#hh#####oo",
        "o.#h######.o",
        "o.#h######.o",
        ".o#h######o.",
        "..o######o..",
        "...o####o...",
        ".....##.....",
        ".....oo.....",
        "....####....",
        "...bbbbbb...",
        "...bbbbbb...",
        "..bbbbbbbb.."
    ];

    /// <summary>A round medal behind the trophy (square and in the text color in Terminal), the trophy in gold.</summary>
    private static void PaintTrophy(Graphics g, Rectangle column, PopupPalette palette, Color accent, float s)
    {
        var pixel = Math.Max(2, (int)Math.Round(3 * s));
        var size = new Size(Trophy[0].Length * pixel, Trophy.Length * pixel);
        var medal = (int)(Math.Max(size.Width, size.Height) + 14 * s);
        var cx = column.Left + column.Width / 2;
        var top = column.Top + (int)(14 * s);
        var circle = new Rectangle(cx - medal / 2, top, medal, medal);

        Color gold, shade, light, baseColor;
        if (palette.Terminal)
        {
            gold = palette.Text;
            shade = Color.FromArgb(160, palette.Text);
            light = Color.FromArgb(230, 255, 230);
            baseColor = palette.Muted;
            using var pen = new Pen(palette.Border, Math.Max(1, s));
            g.DrawRectangle(pen, circle);
        }
        else
        {
            gold = Color.FromArgb(255, 196, 46);
            shade = Color.FromArgb(196, 128, 18);
            light = Color.FromArgb(255, 241, 176);
            baseColor = palette.Dark ? Color.FromArgb(150, 110, 60) : Color.FromArgb(120, 82, 40);
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(Color.FromArgb(palette.Dark ? 60 : 40, accent)))
                g.FillEllipse(fill, circle);
            using (var ring = new Pen(Color.FromArgb(palette.Dark ? 170 : 140, accent), Math.Max(1.5f, 2 * s)))
                g.DrawEllipse(ring, circle);
            g.SmoothingMode = smoothing;
        }

        var x0 = cx - size.Width / 2;
        var y0 = circle.Top + (medal - size.Height) / 2;
        using var goldBrush = new SolidBrush(gold);
        using var shadeBrush = new SolidBrush(shade);
        using var lightBrush = new SolidBrush(light);
        using var baseBrush = new SolidBrush(baseColor);
        for (var row = 0; row < Trophy.Length; row++)
        for (var col = 0; col < Trophy[row].Length; col++)
        {
            var brush = Trophy[row][col] switch { '#' => goldBrush, 'o' => shadeBrush, 'h' => lightBrush, 'b' => baseBrush, _ => null };
            if (brush is not null)
                g.FillRectangle(brush, x0 + col * pixel, y0 + row * pixel, pixel, pixel);
        }

        // Two little sparkles beside it (not in Terminal: no glitter there).
        if (!palette.Terminal)
        {
            using var spark = new SolidBrush(palette.Dark ? Color.White : Color.FromArgb(255, 210, 80));
            void Sparkle(int x, int y)
            {
                g.FillRectangle(spark, x, y - pixel, pixel, pixel * 3);
                g.FillRectangle(spark, x - pixel, y, pixel * 3, pixel);
            }
            Sparkle(circle.Right - pixel, circle.Top + pixel * 2);
            Sparkle(circle.Left + pixel, circle.Bottom - pixel * 3);
        }
    }
}
