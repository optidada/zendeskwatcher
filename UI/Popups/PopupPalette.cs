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

    /// <summary>A soft glow next to the stripe and a tinted border on dark themes (off for Night: nothing bright).</summary>
    public bool Glow { get; init; } = true;

    /// <summary>Text on the accent-colored button (dark on pastel accents).</summary>
    public Color OnAccent { get; init; } = Color.White;

    /// <summary>Button corner radius at 96 dpi, as in the Settings window (<see cref="UiPalette.ButtonRadiusFor"/>).</summary>
    public float ButtonRadius { get; init; } = UiPalette.Pill;

    public required Func<string?, NotificationKind, Color> Accent { get; init; }

    public static PopupPalette For(NotificationTheme theme) => theme switch
    {
        NotificationTheme.Terminal => TerminalGreen,
        NotificationTheme.CleanLight => CleanLight,
        NotificationTheme.CozyPurple => CozyPurple,
        NotificationTheme.Optimizely => Optimizely,
        NotificationTheme.Night => Night,
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
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(191, 90, 242) : Color.FromArgb(10, 132, 255)
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

    /// <summary>The optimizely.com palette: fir greens, lime ("lfgreen") accent, light neutrals, pink and light blue.</summary>
    public static readonly PopupPalette Optimizely = new()
    {
        ButtonRadius = UiPalette.ButtonRadiusFor(NotificationTheme.Optimizely),
        Name = "Optimizely",
        Background = Color.FromArgb(0x0D, 0x3A, 0x29),    // midfir
        BackgroundEnd = Color.FromArgb(0x08, 0x25, 0x1A), // darkfir
        Border = Color.FromArgb(0x19, 0x70, 0x50),        // lightfir
        Text = Color.FromArgb(0xEF, 0xF6, 0xE9),          // neutral-2
        Muted = Color.FromArgb(0xA1, 0xAC, 0x8D),         // neutral-6
        OnAccent = Color.FromArgb(0x08, 0x25, 0x1A),      // dark text on lime/pink/blue
        FontFamily = "Segoe UI",
        Stripe = true,
        Dark = true,
        Accent = (priority, kind) => priority switch
        {
            "urgent" => Color.FromArgb(0xFF, 0x99, 0xB6), // ltpink
            "high" => Color.FromArgb(0xAB, 0xFF, 0x44),   // lfgreen
            "low" => Color.FromArgb(0xD8, 0xE4, 0xCB),    // neutral-4
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(0x71, 0xFF, 0x84) : Color.FromArgb(0x91, 0xDB, 0xDA) // green, ltblue
        }
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
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(236, 150, 222) : Color.FromArgb(184, 148, 255) // pink-lilac, lavender
        }
    };

    /// <summary>
    /// For late evenings: warm near-black (not pure black), dimmed cream text instead of white, and priority colors
    /// that are still red/amber/olive but dark enough not to flash. Little blue light, no glow.
    /// </summary>
    public static readonly PopupPalette Night = new()
    {
        ButtonRadius = UiPalette.ButtonRadiusFor(NotificationTheme.Night),
        Name = "Night",
        Background = Color.FromArgb(30, 26, 22),
        BackgroundEnd = Color.FromArgb(24, 21, 18),
        Border = Color.FromArgb(62, 54, 45),
        Text = Color.FromArgb(220, 204, 180),
        Muted = Color.FromArgb(150, 136, 116),
        OnAccent = Color.FromArgb(28, 24, 20),
        FontFamily = "Segoe UI",
        Stripe = true,
        Dark = true,
        Glow = false,
        Accent = (priority, kind) => priority switch
        {
            "urgent" => Color.FromArgb(196, 98, 84),   // dim brick red
            "high" => Color.FromArgb(200, 146, 80),    // amber
            "low" => Color.FromArgb(136, 146, 108),    // olive
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(178, 140, 122) : Color.FromArgb(170, 152, 112) // dusty rose, sand
        }
    };

    public static readonly PopupPalette CleanLight = new()
    {
        ButtonRadius = UiPalette.ButtonRadiusFor(NotificationTheme.CleanLight),
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
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(147, 51, 234) : Color.FromArgb(37, 99, 235)
        }
    };

}
