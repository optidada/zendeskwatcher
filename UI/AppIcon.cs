using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ZendeskWatcher.UI;

/// <summary>
/// The app icon, drawn in code so it is sharp at every size: a glowing neon Z on a dark rounded square, with a
/// fiery slit-pupil eye where the diagonal crosses the middle ("always watching"). At larger sizes the eye burns
/// with flame tongues, the Z gets a cyan/magenta glitch fringe and the background faint scanlines.
/// The neon color shows the state in the tray (green watching, orange attention, gray off).
/// </summary>
public static class AppIcon
{
    public static readonly Color NeonGreen = Color.FromArgb(57, 255, 110);
    public static readonly Color NeonOrange = Color.FromArgb(255, 159, 10);
    public static readonly Color Dim = Color.FromArgb(120, 130, 125);

    private static readonly Color Background = Color.FromArgb(8, 12, 10);
    private static readonly Color BackgroundTop = Color.FromArgb(16, 28, 20);
    private static readonly Color BackgroundEdge = Color.FromArgb(28, 52, 36);

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

    /// <summary>The previous neon icon (kept for reference).</summary>
    private static Bitmap RenderNeon(int size, Color neon)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var u = size / 64f;          // design grid: 64 x 64
        var small = size < 24;
        var large = size >= 48;
        var center = new PointF(32 * u, 32 * u);

        // ---- Dark rounded square, faint scanlines, a glow of the neon behind the eye.
        var square = new RectangleF(1 * u, 1 * u, 62 * u, 62 * u);
        using var squarePath = Theme.RoundedRect(square, (small ? 10 : 14) * u);
        using (var fill = new LinearGradientBrush(square, BackgroundTop, Background, LinearGradientMode.Vertical))
            g.FillPath(fill, squarePath);

        var clip = g.Clip;
        g.SetClip(squarePath);
        if (!small)
        {
            using var halo = new GraphicsPath();
            halo.AddEllipse(center.X - 26 * u, center.Y - 20 * u, 52 * u, 40 * u);
            using var haloBrush = new PathGradientBrush(halo) { CenterColor = Color.FromArgb(70, neon), SurroundColors = [Color.Transparent] };
            g.FillPath(haloBrush, halo);
        }
        if (large)
        {
            using var scan = new Pen(Color.FromArgb(18, neon), Math.Max(1f, 0.5f * u));
            for (var y = 3f; y < 64; y += 2.5f)
                g.DrawLine(scan, 0, y * u, size, y * u);
        }
        g.Clip = clip;

        using (var rim = new Pen(Color.FromArgb(small ? 255 : 210, BackgroundEdge), Math.Max(1f, 1.2f * u)))
            g.DrawPath(rim, squarePath);

        // ---- The Z.
        using var z = new GraphicsPath();
        var (top, bottom, left, right) = small ? (11f, 53f, 12f, 52f) : (14f, 50f, 15f, 49f);
        z.AddLines([new PointF(left * u, top * u), new PointF(right * u, top * u), new PointF(left * u, bottom * u), new PointF(right * u, bottom * u)]);

        var stroke = (small ? 7.5f : size < 48 ? 6.2f : 5.6f) * u;

        var (layers, spread, alpha) = size switch { < 24 => (2, 3.0f, 45), < 48 => (4, 1.6f, 34), _ => (16, 0.75f, 14) };
        for (var i = layers; i >= 1; i--)
        {
            using var glow = Tube(Color.FromArgb(alpha, neon), stroke + i * spread * u);
            g.DrawPath(glow, z);
        }

        if (large)
        {
            // Glitch fringe: offset cyan and magenta ghosts behind the tube.
            var state = g.Save();
            g.TranslateTransform(-1.4f * u, 0.3f * u);
            using (var cyan = Tube(Color.FromArgb(110, 0, 240, 255), stroke)) g.DrawPath(cyan, z);
            g.Restore(state);
            state = g.Save();
            g.TranslateTransform(1.4f * u, -0.3f * u);
            using (var magenta = Tube(Color.FromArgb(110, 255, 40, 200), stroke)) g.DrawPath(magenta, z);
            g.Restore(state);
        }

        using (var tube = Tube(neon, stroke)) g.DrawPath(tube, z);
        if (!small)
        {
            using var core = Tube(Color.FromArgb(200, Blend(neon, Color.White, 0.65f)), stroke * 0.38f);
            g.DrawPath(core, z);
        }

        // ---- The eye, set into the diagonal.
        // Tiny sizes: a bigger, bolder eye (the diagonal is hidden behind it anyway) so the slit still reads.
        var eyeW = (small ? 44f : 30f) * u;
        var eyeH = (small ? 25f : 15.5f) * u;
        using var eye = EyeShape(center, eyeW, eyeH);
        using var socket = EyeShape(center, eyeW + (small ? 6f : 6f) * u, eyeH + (small ? 6f : 6f) * u);

        if (!small)
        {
            // Flame tongues licking outwards around the eye.
            using var flames = FlameShape(center, eyeW / 2 + 2 * u, eyeH / 2 + 2 * u, (large ? 11f : 7f) * u, large ? 14 : 10);
            using var flameBrush = new PathGradientBrush(flames)
            {
                CenterPoint = center,
                CenterColor = Color.FromArgb(230, Blend(neon, Color.FromArgb(255, 240, 160), 0.5f)),
                SurroundColors = [Color.FromArgb(0, neon)],
                FocusScales = new PointF(0.55f, 0.45f)
            };
            g.FillPath(flameBrush, flames);
        }

        // A dark socket separates the eye from the Z's tube so it reads at small sizes.
        using (var socketBrush = new SolidBrush(Background)) g.FillPath(socketBrush, socket);

        // Iris: white-hot center burning out to the neon and a dark rim.
        using (var iris = new PathGradientBrush(eye)
        {
            CenterPoint = center,
            CenterColor = Blend(neon, Color.FromArgb(255, 250, 210), small ? 0.15f : 0.7f),
            SurroundColors = [Blend(neon, Color.Black, small ? 0f : 0.35f)],
            FocusScales = new PointF(0.25f, 0.35f)
        })
        {
            g.FillPath(iris, eye);
        }
        using (var lid = new Pen(neon, Math.Max(1f, (small ? 1.6f : 1.4f) * u))) g.DrawPath(lid, eye);

        // Slit pupil.
        using var pupil = new GraphicsPath();
        var pw = (small ? 4.8f : 3.4f) * u;
        var ph = eyeH * (small ? 0.95f : 0.92f);
        pupil.AddBezier(center.X, center.Y - ph / 2, center.X + pw, center.Y - ph / 4, center.X + pw, center.Y + ph / 4, center.X, center.Y + ph / 2);
        pupil.AddBezier(center.X, center.Y + ph / 2, center.X - pw, center.Y + ph / 4, center.X - pw, center.Y - ph / 4, center.X, center.Y - ph / 2);
        using (var black = new SolidBrush(Color.FromArgb(5, 5, 5))) g.FillPath(black, pupil);

        if (!small)
        {
            // A tiny glint makes it look alive.
            using var glint = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            g.FillEllipse(glint, center.X + 3.5f * u, center.Y - 4.5f * u, 2.4f * u, 2.4f * u);
        }

        return bitmap;
    }

    private static Pen Tube(Color color, float width) =>
        new(color, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };

    /// <summary>An almond / lens shape: two arcs meeting in sharp corners left and right.</summary>
    private static GraphicsPath EyeShape(PointF c, float width, float height)
    {
        var path = new GraphicsPath();
        var l = new PointF(c.X - width / 2, c.Y);
        var r = new PointF(c.X + width / 2, c.Y);
        path.AddBezier(l, new PointF(c.X - width / 4, c.Y - height * 0.68f), new PointF(c.X + width / 4, c.Y - height * 0.68f), r);
        path.AddBezier(r, new PointF(c.X + width / 4, c.Y + height * 0.68f), new PointF(c.X - width / 4, c.Y + height * 0.68f), l);
        path.CloseFigure();
        return path;
    }

    /// <summary>A ring of flame tongues around an ellipse; deterministic, so the icon is the same every time.</summary>
    private static GraphicsPath FlameShape(PointF c, float rx, float ry, float reach, int tongues)
    {
        var points = new List<PointF>();
        var rng = new Random(1337);
        for (var i = 0; i < tongues; i++)
        {
            var a0 = i * 2 * Math.PI / tongues;
            var a1 = (i + 0.5) * 2 * Math.PI / tongues;
            // Taller tongues at the top and bottom of the eye, like fire rising off it.
            var vertical = (float)Math.Abs(Math.Sin(a1));
            var length = reach * (0.45f + 0.75f * vertical) * (0.7f + 0.6f * (float)rng.NextDouble());
            points.Add(new PointF(c.X + rx * (float)Math.Cos(a0), c.Y + ry * (float)Math.Sin(a0)));
            points.Add(new PointF(c.X + (rx + length) * (float)Math.Cos(a1), c.Y + (ry + length) * (float)Math.Sin(a1)));
        }
        var path = new GraphicsPath();
        path.AddClosedCurve(points.ToArray(), 0.6f);
        return path;
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
