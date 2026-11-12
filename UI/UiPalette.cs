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
        _ => CleanLight
    }).Distinct();

    private static readonly UiPalette CleanLight = new(
        Window: Rgb(243, 244, 246), Surface: Rgb(255, 255, 255), Border: Rgb(229, 231, 235), Text: Rgb(17, 24, 39), TextMuted: Rgb(107, 114, 128),
        Accent: Rgb(37, 99, 235), AccentHover: Rgb(29, 78, 216), AccentSoft: Rgb(239, 246, 255), OnAccent: Rgb(255, 255, 255),
        Success: Rgb(22, 163, 74), Warning: Rgb(217, 119, 6), Danger: Rgb(220, 38, 38), Neutral: Rgb(156, 163, 175),
        Sidebar: Rgb(17, 24, 39), SidebarHover: Rgb(31, 41, 55), SidebarText: Rgb(209, 213, 219), SidebarTitle: Rgb(255, 255, 255),
        Dark: false);

    private static readonly UiPalette NeonDark = new(
        Window: Rgb(14, 15, 22), Surface: Rgb(24, 26, 36), Border: Rgb(48, 52, 66), Text: Rgb(243, 244, 246), TextMuted: Rgb(156, 163, 175),
        Accent: Rgb(10, 132, 255), AccentHover: Rgb(64, 156, 255), AccentSoft: Rgb(20, 40, 72), OnAccent: Rgb(255, 255, 255),
        Success: Rgb(50, 215, 75), Warning: Rgb(255, 159, 10), Danger: Rgb(255, 55, 95), Neutral: Rgb(107, 114, 128),
        Sidebar: Rgb(9, 10, 15), SidebarHover: Rgb(30, 33, 46), SidebarText: Rgb(156, 163, 175), SidebarTitle: Rgb(255, 255, 255),
        Dark: true);

    private static readonly UiPalette Terminal = new(
        Window: Rgb(5, 10, 6), Surface: Rgb(10, 20, 12), Border: Rgb(30, 110, 55), Text: Rgb(57, 255, 110), TextMuted: Rgb(40, 160, 85),
        Accent: Rgb(57, 255, 110), AccentHover: Rgb(130, 255, 170), AccentSoft: Rgb(14, 44, 24), OnAccent: Rgb(5, 10, 6),
        Success: Rgb(57, 255, 110), Warning: Rgb(255, 200, 60), Danger: Rgb(255, 95, 86), Neutral: Rgb(30, 120, 60),
        Sidebar: Rgb(2, 5, 3), SidebarHover: Rgb(12, 30, 16), SidebarText: Rgb(40, 190, 90), SidebarTitle: Rgb(57, 255, 110),
        Dark: true);

    /// <summary>optimizely.com: darkfir page, midfir cards, lightfir borders, lime accent, light neutrals.</summary>
    private static readonly UiPalette Optimizely = new(
        Window: Rgb(0x08, 0x25, 0x1A), Surface: Rgb(0x0D, 0x3A, 0x29), Border: Rgb(0x19, 0x70, 0x50), Text: Rgb(0xEF, 0xF6, 0xE9), TextMuted: Rgb(0xA1, 0xAC, 0x8D),
        Accent: Rgb(0xAB, 0xFF, 0x44), AccentHover: Rgb(0xC4, 0xFF, 0x7A), AccentSoft: Rgb(0x14, 0x52, 0x3B), OnAccent: Rgb(0x08, 0x25, 0x1A),
        Success: Rgb(0x71, 0xFF, 0x84), Warning: Rgb(0xFF, 0xC4, 0x6B), Danger: Rgb(0xFF, 0x99, 0xB6), Neutral: Rgb(0x71, 0x78, 0x63),
        Sidebar: Rgb(0x05, 0x1A, 0x12), SidebarHover: Rgb(0x0D, 0x3A, 0x29), SidebarText: Rgb(0xD8, 0xE4, 0xCB), SidebarTitle: Rgb(0xAB, 0xFF, 0x44),
        Dark: true);

    private static readonly UiPalette CozyPurple = new(
        Window: Rgb(32, 24, 46), Surface: Rgb(46, 34, 64), Border: Rgb(78, 61, 104), Text: Rgb(252, 243, 235), TextMuted: Rgb(190, 172, 210),
        Accent: Rgb(184, 148, 255), AccentHover: Rgb(202, 174, 255), AccentSoft: Rgb(70, 52, 100), OnAccent: Rgb(34, 22, 52),
        Success: Rgb(140, 222, 182), Warning: Rgb(255, 178, 122), Danger: Rgb(255, 122, 144), Neutral: Rgb(124, 108, 146),
        Sidebar: Rgb(25, 18, 36), SidebarHover: Rgb(54, 40, 76), SidebarText: Rgb(206, 190, 224), SidebarTitle: Rgb(255, 236, 222),
        Dark: true);

    /// <summary>Follows the Windows light/dark app mode and accent color.</summary>
    private static UiPalette WindowsNative()
    {
        var accent = SystemAccent() ?? Rgb(0, 120, 212);
        var hover = Blend(accent, Color.Black, 0.15f);
        if (!WindowsUsesDarkMode())
            return CleanLight with { Accent = accent, AccentHover = hover, AccentSoft = Blend(accent, Color.White, 0.92f) };

        return new UiPalette(
            Window: Rgb(32, 32, 32), Surface: Rgb(43, 43, 43), Border: Rgb(64, 64, 64), Text: Rgb(255, 255, 255), TextMuted: Rgb(170, 170, 170),
            Accent: accent, AccentHover: Blend(accent, Color.White, 0.15f), AccentSoft: Blend(accent, Rgb(43, 43, 43), 0.8f), OnAccent: Rgb(255, 255, 255),
            Success: Rgb(108, 203, 95), Warning: Rgb(252, 225, 0), Danger: Rgb(255, 153, 164), Neutral: Rgb(120, 120, 120),
            Sidebar: Rgb(25, 25, 25), SidebarHover: Rgb(45, 45, 45), SidebarText: Rgb(200, 200, 200), SidebarTitle: Rgb(255, 255, 255),
            Dark: true);
    }

    private static bool WindowsUsesDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private static Color? SystemAccent()
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
            roles[9], roles[10], roles[11], roles[12], roles[13], roles[14], roles[15], roles[16], Dark);
    }

    private static Color Rgb(int r, int g, int b) => Color.FromArgb(255, r, g, b);

    private static Color Blend(Color a, Color b, float amount) => Rgb(
        (int)(a.R + (b.R - a.R) * amount), (int)(a.G + (b.G - a.G) * amount), (int)(a.B + (b.B - a.B) * amount));
}
