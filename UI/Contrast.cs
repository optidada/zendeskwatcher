namespace ZendeskWatcher.UI;

/// <summary>
/// WCAG contrast between colors, and small guards that keep text readable: 4.5:1 for normal text (also the small and
/// muted texts), 3:1 for large headings and non-text marks. Used where a color isn't fixed by a theme (the Windows
/// accent) or where text sits on a colored fill (buttons, priority tags).
/// </summary>
public static class Contrast
{
    /// <summary>Normal and small text.</summary>
    public const double Text = 4.5;

    private static readonly Color NearBlack = Color.FromArgb(12, 12, 12), White = Color.FromArgb(255, 255, 255), Black = Color.FromArgb(0, 0, 0);

    /// <summary>The contrast ratio of two colors: 1 (equal) to 21 (black on white).</summary>
    public static double Ratio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return la > lb ? (la + 0.05) / (lb + 0.05) : (lb + 0.05) / (la + 0.05);
    }

    /// <summary>Relative luminance (WCAG): 0 black, 1 white.</summary>
    public static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(int v)
    {
        var c = v / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// <paramref name="color"/> made just lighter (on a dark background) or darker (on a light one) until it reaches
    /// <paramref name="min"/> against every background; unchanged when it already does.
    /// </summary>
    public static Color Ensure(Color color, double min, params Color[] backgrounds)
    {
        if (backgrounds.All(b => Ratio(color, b) >= min))
            return color;
        // Toward white if that can contrast more with the backgrounds (they're dark), else toward black.
        var toward = backgrounds.Min(b => Ratio(White, b)) >= backgrounds.Min(b => Ratio(Black, b)) ? White : Black;
        for (var step = 1; step <= 20; step++)
        {
            var c = Blend(color, toward, step / 20f);
            if (backgrounds.All(b => Ratio(c, b) >= min))
                return c;
        }
        return toward;
    }

    /// <summary>Text on a colored fill: <paramref name="preferred"/> when readable there, else near-black or white, whichever reads better.</summary>
    public static Color On(Color fill, Color preferred) =>
        Ratio(preferred, fill) >= Text ? preferred : Ratio(NearBlack, fill) >= Ratio(White, fill) ? NearBlack : White;

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * amount), (int)Math.Round(a.G + (b.G - a.G) * amount), (int)Math.Round(a.B + (b.B - a.B) * amount));
}
