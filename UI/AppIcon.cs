using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ZendeskWatcher.UI;

/// <summary>
/// The app icon for the tray, windows and the exe: the Optimizely-style Z drawn by <see cref="BrandIcon"/>, so it is
/// sharp at every size. The state colors (named after the original neon icon) map to lime = watching,
/// orange = unassigned tickets waiting, gray = off / not signed in. Also writes the .ico and PNG files.
/// </summary>
public static class AppIcon
{
    public static readonly Color NeonGreen = Color.FromArgb(57, 255, 110);
    public static readonly Color NeonOrange = Color.FromArgb(255, 159, 10);
    public static readonly Color Dim = Color.FromArgb(120, 130, 125);


    /// <summary>
    /// Renders the icon at the given pixel size: the Optimizely-style Z (<see cref="BrandIcon"/>), the same plain Z at
    /// every size (tray, taskbar, window, exe). The state color maps to lime / orange / gray.
    /// </summary>
    public static Bitmap Render(int size, Color neon) => Render(size, neon, 0);

    private static Bitmap Render(int size, Color neon, float shine)
    {
        var face = neon == NeonOrange ? BrandIcon.Orange : neon == Dim ? BrandIcon.Gray : BrandIcon.Lime;
        return BrandIcon.Render(size, face, eye: false, shine); // the plain Z everywhere, same as in the tray
    }

    /// <summary>
    /// Frames of a gentle pulse (the face brightens and fades back) for the tray while the app is working.
    /// </summary>
    public static Icon[] CreatePulseFrames(Color neon, int count = 12)
    {
        var size = SystemInformation.SmallIconSize.Width;
        return Enumerable.Range(0, count).Select(i =>
        {
            var t = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / count)); // 0 → 1 → 0, smooth
            using var bitmap = Render(size, neon, 0.55f * t);
            return ToIcon(bitmap);
        }).ToArray();
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    /// <summary>A Windows icon for the tray or a window, at the system's small-icon size for the current DPI.</summary>
    public static Icon CreateIcon(Color neon, int size = 0)
    {
        size = size > 0 ? size : SystemInformation.SmallIconSize.Width;
        using var bitmap = Render(size, neon);
        var handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries) for the exe.</summary>
    public static void SaveIco(string path, Color neon, params int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            using var bmp = Render(s, neon);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return (Size: s, Data: ms.ToArray());
        }).ToList();

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0);            // reserved
        w.Write((short)1);            // type: icon
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);         // palette
            w.Write((byte)0);         // reserved
            w.Write((short)1);        // planes
            w.Write((short)32);       // bits per pixel
            w.Write(data.Length);
            w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images)
            w.Write(data);
    }

    /// <summary>The icon as a PNG file (Windows toasts need an image file for the logo).</summary>
    public static string SavePng(string path, int size)
    {
        using var bmp = Render(size, NeonGreen);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }


    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
