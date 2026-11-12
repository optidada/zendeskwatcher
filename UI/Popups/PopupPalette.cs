namespace ZendeskWatcher.UI.Popups;

/// <summary>Colors, fonts and effects of a popup theme.</summary>
public sealed class PopupPalette
{
    public required string Name { get; init; }
    public required Color Background { get; init; }
    public required Color BackgroundEnd { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color Muted { get; init; }
    public required string FontFamily { get; init; }
    public float FontSize { get; init; } = 9.5f;

    /// <summary>Monospace "terminal" look: square corners, ASCII countdown, typing effect, scanlines.</summary>
    public bool Terminal { get; init; }

    /// <summary>A glowing priority stripe on the left edge.</summary>
    public bool Stripe { get; init; }

    public bool Dark { get; init; }

    /// <summary>Text on the accent-colored button (dark on pastel accents).</summary>
    public Color OnAccent { get; init; } = Color.White;

    public required Func<string?, NotificationKind, Color> Accent { get; init; }

    public static PopupPalette For(NotificationTheme theme) => theme switch
    {
        NotificationTheme.Terminal => TerminalGreen,
        NotificationTheme.CleanLight => CleanLight,
        NotificationTheme.CozyPurple => CozyPurple,
        _ => NeonDark
    };

    public static readonly PopupPalette NeonDark = new()
    {
        Name = "Neon dark",
        Background = Color.FromArgb(24, 26, 36),
        BackgroundEnd = Color.FromArgb(14, 15, 22),
        Border = Color.FromArgb(48, 52, 66),
        Text = Color.FromArgb(243, 244, 246),
        Muted = Color.FromArgb(156, 163, 175),
        FontFamily = "Segoe UI",
        Stripe = true,
        Dark = true,
        Accent = (priority, kind) => priority switch
        {
            "urgent" => Color.FromArgb(255, 55, 95),
            "high" => Color.FromArgb(255, 159, 10),
            "low" => Color.FromArgb(50, 215, 75),
            _ => kind == NotificationKind.MyTicket ? Color.FromArgb(191, 90, 242) : Color.FromArgb(10, 132, 255)
        }
    };

    public static readonly PopupPalette TerminalGreen = new()
    {
        Name = "Terminal",
        Background = Color.FromArgb(5, 10, 6),
        BackgroundEnd = Color.FromArgb(5, 10, 6),
        Border = Color.FromArgb(30, 140, 70),
        Text = Color.FromArgb(57, 255, 110),
        Muted = Color.FromArgb(40, 160, 85),
        FontFamily = "Cascadia Mono",
        FontSize = 9f,
        Terminal = true,
        Dark = true,
        Accent = (priority, _) => priority is "urgent" or "high" ? Color.FromArgb(255, 95, 86) : Color.FromArgb(57, 255, 110)
    };

    /// <summary>Dusky purple, like a lamp-lit evening: warm cream text, soft pastel accents, no hard neon.</summary>
    public static readonly PopupPalette CozyPurple = new()
    {
        Name = "Cozy",
        OnAccent = Color.FromArgb(40, 26, 58),
        Background = Color.FromArgb(52, 38, 72),
        BackgroundEnd = Color.FromArgb(36, 26, 52),
        Border = Color.FromArgb(92, 72, 124),
        Text = Color.FromArgb(252, 243, 235),
        Muted = Color.FromArgb(196, 178, 214),
        FontFamily = "Segoe UI",
        FontSize = 9.75f,
        Stripe = true,
        Dark = true,
        Accent = (priority, kind) => priority switch
        {
            "urgent" => Color.FromArgb(255, 122, 144),  // soft coral
            "high" => Color.FromArgb(255, 178, 122),    // peach
            "low" => Color.FromArgb(140, 222, 182),     // mint
            _ => kind == NotificationKind.MyTicket ? Color.FromArgb(236, 150, 222) : Color.FromArgb(184, 148, 255) // pink-lilac, lavender
        }
    };

    public static readonly PopupPalette CleanLight = new()
    {
        Name = "Clean light",
        Background = Color.White,
        BackgroundEnd = Color.FromArgb(249, 250, 251),
        Border = Color.FromArgb(229, 231, 235),
        Text = Color.FromArgb(17, 24, 39),
        Muted = Color.FromArgb(107, 114, 128),
        FontFamily = "Segoe UI",
        Stripe = true,
        Accent = (priority, kind) => priority switch
        {
            "urgent" => Color.FromArgb(220, 38, 38),
            "high" => Color.FromArgb(234, 88, 12),
            "low" => Color.FromArgb(22, 163, 74),
            _ => kind == NotificationKind.MyTicket ? Color.FromArgb(147, 51, 234) : Color.FromArgb(37, 99, 235)
        }
    };

}
