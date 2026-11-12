using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ZendeskWatcher.UI;

/// <summary>
/// The app icon, drawn in code so it is sharp at every size: a glowing neon Z on a dark rounded square,
/// with two alert hare ears on top ("on the lookout"). The neon color shows the state in the tray.
/// </summary>
public static class AppIcon
{
    public static readonly Color NeonGreen = Color.FromArgb(57, 255, 110);
    public static readonly Color NeonOrange = Color.FromArgb(255, 159, 10);
    public static readonly Color Dim = Color.FromArgb(120, 130, 125);

    private static readonly Color Background = Color.FromArgb(10, 16, 12);
    private static readonly Color BackgroundEdge = Color.FromArgb(26, 46, 32);

    /// <summary>Renders the icon at the given pixel size.</summary>
    public static Bitmap Render(int size, Color neon)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var u = size / 64f;          // design grid: 64 x 64
        var small = size < 24;       // no ears and less glow when tiny

        // Dark rounded square with a faint inner rim.
        var square = new RectangleF(1 * u, 1 * u, 62 * u, 62 * u);
        using (var path = Theme.RoundedRect(square, (small ? 10 : 14) * u))
        {
            using (var fill = new LinearGradientBrush(square, Color.FromArgb(18, 30, 22), Background, LinearGradientMode.Vertical))
                g.FillPath(fill, path);
            using var rim = new Pen(Color.FromArgb(small ? 255 : 200, BackgroundEdge), Math.Max(1f, 1.2f * u));
            g.DrawPath(rim, path);
        }

        using var shape = new GraphicsPath();
        if (small)
        {
            // Bigger Z filling the square, so it reads at 16 px.
            shape.AddLines([new PointF(16 * u, 16 * u), new PointF(48 * u, 16 * u), new PointF(16 * u, 48 * u), new PointF(48 * u, 48 * u)]);
        }
        else
        {
            // Hare ears: two tall, slightly tilted loops rising from the Z's top bar.
            shape.StartFigure();
            shape.AddBezier(new PointF(24 * u, 22 * u), new PointF(17 * u, 12 * u), new PointF(17 * u, 3 * u), new PointF(22 * u, 4 * u));
            shape.AddBezier(new PointF(22 * u, 4 * u), new PointF(27 * u, 5 * u), new PointF(29 * u, 14 * u), new PointF(29 * u, 22 * u));
            shape.StartFigure();
            shape.AddBezier(new PointF(35 * u, 22 * u), new PointF(35 * u, 13 * u), new PointF(39 * u, 4 * u), new PointF(44 * u, 5 * u));
            shape.AddBezier(new PointF(44 * u, 5 * u), new PointF(48 * u, 7 * u), new PointF(45 * u, 15 * u), new PointF(40 * u, 22 * u));

            // The Z.
            shape.StartFigure();
            shape.AddLines([new PointF(17 * u, 23 * u), new PointF(47 * u, 23 * u), new PointF(18 * u, 51 * u), new PointF(48 * u, 51 * u)]);
        }

        // Neon: many faint glow layers (smooth falloff), then the bright tube with a white-hot core.
        // Less glow at medium sizes so the shape stays crisp.
        var stroke = (small ? 7.5f : size < 48 ? 5.8f : 5.2f) * u;
        var (layers, spread, alpha) = size switch
        {
            < 24 => (2, 3.0f, 45),
            < 48 => (4, 1.6f, 34),
            _ => (16, 0.75f, 14)
        };
        for (var i = layers; i >= 1; i--)
        {
            using var glow = new Pen(Color.FromArgb(alpha, neon), stroke + i * spread * u)
            {
                LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round
            };
            g.DrawPath(glow, shape);
        }

        using (var tube = new Pen(neon, stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawPath(tube, shape);

        if (!small)
        {
            using var core = new Pen(Color.FromArgb(200, Blend(neon, Color.White, 0.65f)), stroke * 0.38f)
            {
                LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round
            };
            g.DrawPath(core, shape);
        }

        return bitmap;
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

    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
