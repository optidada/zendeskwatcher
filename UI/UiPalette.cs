using Microsoft.Win32;

namespace ZendeskWatcher.UI;

/// <summary>
/// Colors of the Settings window for one appearance. Every role gets a distinct color (nudged by one unit if two
/// would be equal), so <see cref="Theme.Recolor"/> can tell the roles apart when switching palettes.
/// </summary>
public sealed record UiPalette(
    Color Window, Color Surface, Color Border, Color Text, Color TextMuted,
    Color Accent, Color AccentHover, Color AccentSoft, Color OnAccent,
    Color Success, Color Warning, Color Danger, Color Neutral,
    Color Sidebar, Color SidebarHover, Color SidebarText, Color SidebarTitle,
    bool Dark)
{
    /// <summary>A button radius meaning "fully round" (a pill).</summary>
    public const float Pill = -1;

    /// <summary>Corner radius of buttons at 96 dpi: 0 is square, <see cref="Pill"/> fully round. Decided by the theme.</summary>
    public float ButtonRadius { get; init; } = 6;

    /// <summary>The theme's button shape; the popups use the same (see <see cref="Popups.PopupPalette.ButtonRadius"/>).</summary>
    public static float ButtonRadiusFor(NotificationTheme theme) => theme switch
    {
        NotificationTheme.Terminal => 0,
        NotificationTheme.Retro => 2,         // nearly square, like the old desktops
        NotificationTheme.NeonDark or NotificationTheme.CozyPurple => Pill,
        NotificationTheme.CleanLight => 8,
        NotificationTheme.WindowsNative => 2, // Native: like Windows 11, a bit squarer and edgier
        _ => 10                               // Optimizely: the brand wants rounded corners
    };

    /// <summary>
    /// Links in texts: the accent, unless it's too close to the body text to tell apart (Terminal's green, a pale or
    /// dark Windows accent); then a cyan on dark (as the Terminal notifications use) or a blue on light. Not a role:
    /// <see cref="Theme.Recolor"/> sets link colors from the target palette directly.
    /// </summary>
    public Color Link => LinkOverride ?? (Distance(Accent, Text) >= 150 ? Accent
        : Dark ? (Distance(Rgb(90, 220, 255), Text) >= 150 ? Rgb(90, 220, 255) : Rgb(255, 200, 60))
        : (Distance(Rgb(0, 95, 184), Text) >= 150 ? Rgb(0, 95, 184) : Rgb(170, 60, 0)));

    /// <summary>A theme's own link color, when the accent differs from the text but too subtly (Night's muted amber).</summary>
    public Color? LinkOverride { get; init; }

    /// <summary>A link under the mouse (or pressed).</summary>
    public Color LinkHover => Link == Accent ? AccentHover : Blend(Link, Dark ? Color.White : Color.Black, 0.25f);

    /// <summary>How different two colors look ("redmean" distance: 0 equal, about 765 black to white).</summary>
    private static double Distance(Color a, Color b)
    {
        var mean = (a.R + b.R) / 2.0;
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt((2 + mean / 256) * dr * dr + 4 * dg * dg + (2 + (255 - mean) / 256) * db * db);
    }

    /// <summary>All colors, in a fixed order (matches the constructor).</summary>
    public Color[] Roles =>
    [
        Window, Surface, Border, Text, TextMuted, Accent, AccentHover, AccentSoft, OnAccent,
        Success, Warning, Danger, Neutral, Sidebar, SidebarHover, SidebarText, SidebarTitle
    ];

    public static UiPalette For(NotificationTheme theme) => (theme switch
    {
        NotificationTheme.NeonDark => NeonDark,
        NotificationTheme.Terminal => Terminal,
        NotificationTheme.WindowsNative => WindowsNative(),
        NotificationTheme.CozyPurple => CozyPurple,
        NotificationTheme.Optimizely => Optimizely,
        NotificationTheme.Night => Night,
        NotificationTheme.Retro => Retro,
        _ => CleanLight
    }).Distinct() with { ButtonRadius = ButtonRadiusFor(theme) };

    private static readonly UiPalette CleanLight = new(
        Window: Rgb(243, 244, 246), Surface: Rgb(255, 255, 255), Border: Rgb(229, 231, 235), Text: Rgb(17, 24, 39), TextMuted: Rgb(100, 107, 121),
        Accent: Rgb(37, 99, 235), AccentHover: Rgb(29, 78, 216), AccentSoft: Rgb(239, 246, 255), OnAccent: Rgb(255, 255, 255),
        Success: Rgb(21, 128, 61), Warning: Rgb(180, 83, 9), Danger: Rgb(210, 32, 32), Neutral: Rgb(134, 141, 153),
        Sidebar: Rgb(17, 24, 39), SidebarHover: Rgb(31, 41, 55), SidebarText: Rgb(209, 213, 219), SidebarTitle: Rgb(255, 255, 255),
        Dark: false);

    private static readonly UiPalette NeonDark = new(
        Window: Rgb(14, 15, 22), Surface: Rgb(24, 26, 36), Border: Rgb(48, 52, 66), Text: Rgb(243, 244, 246), TextMuted: Rgb(156, 163, 175),
        Accent: Rgb(10, 132, 255), AccentHover: Rgb(64, 156, 255), AccentSoft: Rgb(20, 40, 72), OnAccent: Rgb(8, 12, 24),
        Success: Rgb(50, 215, 75), Warning: Rgb(255, 159, 10), Danger: Rgb(255, 55, 95), Neutral: Rgb(120, 127, 141),
        Sidebar: Rgb(9, 10, 15), SidebarHover: Rgb(30, 33, 46), SidebarText: Rgb(156, 163, 175), SidebarTitle: Rgb(255, 255, 255),
        Dark: true) { LinkOverride = Rgb(70, 160, 255) }; // a lighter blue: the accent is too dark for links on the selected-row tint

    private static readonly UiPalette Terminal = new(
        Window: Rgb(5, 10, 6), Surface: Rgb(10, 20, 12), Border: Rgb(30, 110, 55), Text: Rgb(57, 255, 110), TextMuted: Rgb(44, 168, 90),
        Accent: Rgb(57, 255, 110), AccentHover: Rgb(130, 255, 170), AccentSoft: Rgb(14, 44, 24), OnAccent: Rgb(5, 10, 6),
        Success: Rgb(57, 255, 110), Warning: Rgb(255, 200, 60), Danger: Rgb(255, 95, 86), Neutral: Rgb(36, 140, 70),
        Sidebar: Rgb(2, 5, 3), SidebarHover: Rgb(12, 30, 16), SidebarText: Rgb(40, 190, 90), SidebarTitle: Rgb(57, 255, 110),
        Dark: true);

    /// <summary>
    /// The Optimizely brand pack: Neutral 3 backdrop, white cards, Dark Fir text and sidebar, Fir fills (buttons, toggles,
    /// selected choices) with LFGreen on them, like the logo (pure LFGreen fills were too stark on white). Dark
    /// Fir text, a muted Dark Fir grey for secondary text (Light Fir read as harsh green), Dark Blue links (LFGreen is unreadable as text on light). Pinks for warnings
    /// and errors (the brand has no red or amber).
    /// </summary>
    private static readonly UiPalette Optimizely = new(
        Window: Rgb(0xE4, 0xF0, 0xDA), Surface: Rgb(0xFF, 0xFF, 0xFF), Border: Rgb(0xC3, 0xCE, 0xAF), Text: Rgb(0x08, 0x25, 0x1A), TextMuted: Rgb(0x4A, 0x5A, 0x52),
        Accent: Rgb(0x0D, 0x3A, 0x29), AccentHover: Rgb(0x19, 0x70, 0x50), AccentSoft: Rgb(0xEF, 0xF6, 0xE9), OnAccent: Rgb(0xAB, 0xFF, 0x44),
        Success: Rgb(0x22, 0x7D, 0x47), Warning: Rgb(0x8F, 0x47, 0x64), Danger: Rgb(0xB0, 0x2E, 0x5C), Neutral: Rgb(0xA1, 0xAC, 0x8D),
        Sidebar: Rgb(0x08, 0x25, 0x1A), SidebarHover: Rgb(0x0D, 0x3A, 0x29), SidebarText: Rgb(0xD8, 0xE4, 0xCB), SidebarTitle: Rgb(0xAB, 0xFF, 0x44),
        Dark: false) { LinkOverride = Rgb(0x00, 0x7B, 0x79) };

    private static readonly UiPalette CozyPurple = new(
        Window: Rgb(32, 24, 46), Surface: Rgb(46, 34, 64), Border: Rgb(78, 61, 104), Text: Rgb(252, 243, 235), TextMuted: Rgb(190, 172, 210),
        Accent: Rgb(184, 148, 255), AccentHover: Rgb(202, 174, 255), AccentSoft: Rgb(70, 52, 100), OnAccent: Rgb(34, 22, 52),
        Success: Rgb(140, 222, 182), Warning: Rgb(255, 178, 122), Danger: Rgb(255, 122, 144), Neutral: Rgb(140, 124, 162),
        Sidebar: Rgb(25, 18, 36), SidebarHover: Rgb(54, 40, 76), SidebarText: Rgb(206, 190, 224), SidebarTitle: Rgb(255, 236, 222),
        Dark: true);

    /// <summary>For late evenings: warm near-black, dimmed cream text, an amber accent; little blue light.</summary>
    private static readonly UiPalette Night = new(
        Window: Rgb(20, 17, 14), Surface: Rgb(30, 26, 22), Border: Rgb(60, 52, 44), Text: Rgb(220, 204, 180), TextMuted: Rgb(164, 149, 128),
        Accent: Rgb(200, 146, 80), AccentHover: Rgb(216, 166, 102), AccentSoft: Rgb(56, 44, 32), OnAccent: Rgb(24, 20, 16),
        Success: Rgb(136, 156, 100), Warning: Rgb(212, 160, 82), Danger: Rgb(204, 104, 90), Neutral: Rgb(140, 126, 108),
        Sidebar: Rgb(15, 13, 11), SidebarHover: Rgb(36, 31, 26), SidebarText: Rgb(188, 172, 148), SidebarTitle: Rgb(222, 186, 128),
        Dark: true) { LinkOverride = Rgb(245, 160, 55) }; // a brighter amber: the muted accent looks too much like the cream text

    /// <summary>Classic 80s home computer desktop: deep blue window, darker blue sidebar, white text, orange accent.</summary>
    private static readonly UiPalette Retro = new(
        Window: Rgb(0, 46, 108), Surface: Rgb(0, 62, 136), Border: Rgb(60, 118, 190), Text: Rgb(255, 255, 255), TextMuted: Rgb(192, 216, 244),
        Accent: Rgb(255, 148, 24), AccentHover: Rgb(255, 170, 70), AccentSoft: Rgb(20, 88, 168), OnAccent: Rgb(0, 30, 70),
        Success: Rgb(150, 220, 150), Warning: Rgb(255, 200, 80), Danger: Rgb(255, 140, 140), Neutral: Rgb(110, 150, 205),
        Sidebar: Rgb(0, 32, 80), SidebarHover: Rgb(0, 62, 136), SidebarText: Rgb(200, 220, 245), SidebarTitle: Rgb(255, 148, 24),
        Dark: true) { LinkOverride = Rgb(255, 200, 110) }; // light amber: the orange is too dark for links on the light-blue tint

    /// <summary>Follows the Windows light/dark app mode and accent color.</summary>
    /// <summary>
    /// Native: Windows 11's look, edgier. Light or dark and the accent color follow Windows (Settings → Personalization),
    /// with more contrast than Windows' soft greys and crisp borders.
    /// </summary>
    private static UiPalette WindowsNative()
    {
        var system = SystemAccent() ?? Rgb(0, 120, 212);
        // The accent is also link and label text: darkened (light mode) or lightened (dark mode) just enough to read on
        // the window, cards and its own soft tint, as Windows does with its accent shades.
        if (!WindowsUsesDarkMode())
        {
            var soft = Blend(system, Color.White, 0.9f);
            var accent = Contrast.Ensure(system, Contrast.Text, Rgb(238, 238, 238), Color.White, soft);
            return new UiPalette(
                Window: Rgb(238, 238, 238), Surface: Rgb(255, 255, 255), Border: Rgb(204, 204, 204), Text: Rgb(12, 12, 12), TextMuted: Rgb(92, 92, 92),
                Accent: accent, AccentHover: Blend(accent, Color.Black, 0.15f), AccentSoft: soft, OnAccent: OnColor(accent, Blend(accent, Color.Black, 0.15f)),
                Success: Rgb(16, 124, 16), Warning: Rgb(157, 93, 0), Danger: Rgb(196, 43, 28), Neutral: Rgb(100, 100, 100),
                // A light sidebar like the Windows Settings app: what tells Native apart from Clean light at a glance.
                Sidebar: Rgb(228, 228, 228), SidebarHover: Rgb(214, 214, 214), SidebarText: Rgb(28, 28, 28), SidebarTitle: Rgb(0, 0, 0),
                Dark: false);
        }

        var darkSoft = Blend(system, Rgb(28, 28, 28), 0.78f);
        var darkAccent = Contrast.Ensure(system, Contrast.Text, Rgb(14, 14, 14), Rgb(28, 28, 28), darkSoft);
        return new UiPalette(
            Window: Rgb(14, 14, 14), Surface: Rgb(28, 28, 28), Border: Rgb(66, 66, 66), Text: Rgb(255, 255, 255), TextMuted: Rgb(176, 176, 176),
            Accent: darkAccent, AccentHover: Blend(darkAccent, Color.White, 0.15f), AccentSoft: darkSoft,
            OnAccent: OnColor(darkAccent, Blend(darkAccent, Color.White, 0.15f)),
            Success: Rgb(108, 203, 95), Warning: Rgb(252, 225, 0), Danger: Rgb(255, 153, 164), Neutral: Rgb(128, 128, 128),
            Sidebar: Rgb(6, 6, 6), SidebarHover: Rgb(34, 34, 34), SidebarText: Rgb(204, 204, 204), SidebarTitle: Rgb(255, 255, 255),
            Dark: true);
    }

    /// <summary>Readable text on the accent button and its hover shade: white, or near-black on a light accent (e.g. yellow).</summary>
    private static Color OnColor(Color accent, Color hover)
    {
        var dark = Rgb(12, 12, 12);
        return Math.Min(Contrast.Ratio(Color.White, accent), Contrast.Ratio(Color.White, hover)) >= Contrast.Text ? Rgb(255, 255, 255)
            : Math.Min(Contrast.Ratio(dark, accent), Contrast.Ratio(dark, hover)) >= Math.Min(Contrast.Ratio(Color.White, accent), Contrast.Ratio(Color.White, hover))
                ? dark : Rgb(255, 255, 255);
    }

    internal static bool WindowsUsesDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    internal static Color? SystemAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is not int abgr)
            return null;
        return Rgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF); // stored as 0xAABBGGRR
    }

    /// <summary>Makes every role color unique, so palette switches can map roles one to one.</summary>
    private UiPalette Distinct()
    {
        var seen = new HashSet<int>();
        var roles = Roles;
        for (var i = 0; i < roles.Length; i++)
        {
            var c = roles[i];
            for (var step = 1; !seen.Add(c.ToArgb()); step++)
            {
                var delta = step % 2 == 1 ? (step + 1) / 2 : -(step / 2);
                c = Rgb(roles[i].R, roles[i].G, Math.Clamp(roles[i].B + delta, 0, 255));
            }
            roles[i] = c;
        }
        return new UiPalette(roles[0], roles[1], roles[2], roles[3], roles[4], roles[5], roles[6], roles[7], roles[8],
            roles[9], roles[10], roles[11], roles[12], roles[13], roles[14], roles[15], roles[16], Dark) { LinkOverride = LinkOverride };
    }

    private static Color Rgb(int r, int g, int b) => Color.FromArgb(255, r, g, b);

    private static Color Blend(Color a, Color b, float amount) => Rgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}
