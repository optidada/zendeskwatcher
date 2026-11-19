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

    /// <summary>Native: the window border in the accent color and small Windows 11 corners (crisper than the round ones).</summary>
    public bool AccentFrame { get; init; }

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
        NotificationTheme.Retro => Retro,
        NotificationTheme.WindowsNative => Native(),
        _ => NeonDark
    };

    /// <summary>
    /// Native: Windows 11, edgier. Light or dark and the accent follow Windows (read each time, so a change in Windows
    /// shows on the next notification); crisp border, no glow, a bold priority stripe in the accent.
    /// </summary>
    public static PopupPalette Native()
    {
        var dark = UiPalette.WindowsUsesDarkMode();
        var accent = UiPalette.SystemAccent() ?? Color.FromArgb(0, 120, 212);
        return new PopupPalette
        {
            ButtonRadius = UiPalette.ButtonRadiusFor(NotificationTheme.WindowsNative),
            Name = "Native",
            Dark = dark,
            // Mica-like: a soft grey tint rather than white (or a deep grey in dark mode), like Windows 11's own windows.
            Background = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243),
            BackgroundEnd = dark ? Color.FromArgb(24, 24, 24) : Color.FromArgb(235, 235, 235),
            Border = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(196, 196, 196),
            Text = dark ? Color.White : Color.FromArgb(12, 12, 12),
            Muted = dark ? Color.FromArgb(176, 176, 176) : Color.FromArgb(92, 92, 92),
            FontFamily = "Segoe UI",
            Stripe = true,
            Glow = false,
            AccentFrame = true,
            OnAccent = accent.R * 0.299 + accent.G * 0.587 + accent.B * 0.114 > 170 ? Color.FromArgb(12, 12, 12) : Color.White,
            Accent = (priority, _) => priority switch
            {
                "urgent" => Color.FromArgb(232, 17, 35),
                "high" => Color.FromArgb(247, 99, 12),
                // A red or orange Windows accent would look urgent on every ticket: then Windows blue.
                _ => accent.GetHue() is < 50 or > 330 && accent.GetSaturation() > 0.35f ? Color.FromArgb(0, 120, 212) : accent
            }
        };
    }

    public static readonly PopupPalette NeonDark = new()
    {
        Name = "Neon dark",
        OnAccent = Color.FromArgb(8, 12, 24), // dark text: white is too faint on the bright neon colors
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

    /// <summary>
    /// The Optimizely brand pack: white (Neutral 1) backdrop, Dark Fir text and outline (the only stroke color), the
    /// brand's bright accents (LFGreen, Light Pink, Light Blue, Grass) on stripes and buttons with Dark Fir text on them.
    /// No gradients, no glow. Text in an accent color is darkened until readable (<see cref="Contrast"/>).
    /// </summary>
    public static readonly PopupPalette Optimizely = new()
    {
        ButtonRadius = UiPalette.ButtonRadiusFor(NotificationTheme.Optimizely),
        Name = "Optimizely",
        Background = Color.FromArgb(0xFF, 0xFF, 0xFF),    // neutral-1
        BackgroundEnd = Color.FromArgb(0xFF, 0xFF, 0xFF), // no gradients
        Border = Color.FromArgb(0x08, 0x25, 0x1A),        // darkfir
        Text = Color.FromArgb(0x08, 0x25, 0x1A),          // darkfir
        Muted = Color.FromArgb(0x4A, 0x5A, 0x52),         // dark fir greyed: lightfir text read as harsh green
        OnAccent = Color.FromArgb(0x08, 0x25, 0x1A),      // dark text on lime/pink/blue
        FontFamily = "Segoe UI",
        Stripe = true,
        Glow = false,
        Accent = (priority, kind) => priority switch
        {
            "urgent" => Color.FromArgb(0xFF, 0x99, 0xB6), // ltpink
            "high" => Color.FromArgb(0xAB, 0xFF, 0x44),   // lfgreen
            "low" => Color.FromArgb(0xC3, 0xCE, 0xAF),    // neutral-5
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(0x7D, 0xDD, 0x3D) : Color.FromArgb(0x91, 0xDB, 0xDA) // grass, ltblue
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

    /// <summary>
    /// Classic 80s home computer desktop: deep blue, crisp white text, orange accents, nearly square corners. Priority
    /// colors in the same bold, flat style. Goes with the chiptune sounds.
    /// </summary>
    public static readonly PopupPalette Retro = new()
    {
        ButtonRadius = UiPalette.ButtonRadiusFor(NotificationTheme.Retro),
        Name = "Retro",
        Background = Color.FromArgb(0, 62, 136),
        BackgroundEnd = Color.FromArgb(0, 46, 108),
        Border = Color.FromArgb(70, 130, 200),
        Text = Color.FromArgb(255, 255, 255),
        Muted = Color.FromArgb(176, 204, 238),
        OnAccent = Color.FromArgb(0, 30, 70),
        FontFamily = "Segoe UI",
        Stripe = true,
        Dark = true,
        Glow = false,
        Accent = (priority, kind) => priority switch
        {
            "urgent" => Color.FromArgb(255, 92, 92),    // red
            "high" => Color.FromArgb(255, 136, 0),      // the classic orange
            "low" => Color.FromArgb(150, 220, 150),     // soft green
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(255, 200, 80) : Color.FromArgb(120, 200, 255) // yellow, sky blue
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
            "high" => Color.FromArgb(194, 65, 12),   // orange and green dark enough for white button text
            "low" => Color.FromArgb(21, 128, 61),
            _ => kind is NotificationKind.MyTicket or NotificationKind.CcTicket ? Color.FromArgb(147, 51, 234) : Color.FromArgb(37, 99, 235)
        }
    };

}
