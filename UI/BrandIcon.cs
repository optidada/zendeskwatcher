using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ZendeskWatcher.UI;

/// <summary>
/// The app icon in the Optimizely style (as on optimizely.com): a chunky, rounded letter with a lime face and a solid
/// dark-green extrusion towards the lower left, flat, on a transparent background. Our letter is a Z (not the logo),
/// optionally with a small flat eye in the diagonal ("watching"). The face color shows the state in the tray.
/// </summary>
public static class BrandIcon
{
    public static readonly Color Lime = Color.FromArgb(0xAB, 0xFF, 0x44);
    public static readonly Color DarkGreen = Color.FromArgb(0x0D, 0x3A, 0x29);
    public static readonly Color Orange = Color.FromArgb(0xFF, 0xA9, 0x2E);
    public static readonly Color Gray = Color.FromArgb(0xB4, 0xBC, 0xB8);
    private static readonly Color DarkGray = Color.FromArgb(0x3C, 0x42, 0x40);

    /// <param name="shine">0 = normal; up to 1 = the face blended towards white (for the "working" pulse).</param>
    public static Bitmap Render(int size, Color face, bool eye, float shine = 0)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        var u = size / 64f;            // design grid 64 x 64
        var small = size < 24;
        var side = face == Gray ? DarkGray : DarkGreen;
        if (shine > 0)
            face = Color.FromArgb(
                (int)(face.R + (255 - face.R) * shine), (int)(face.G + (255 - face.G) * shine), (int)(face.B + (255 - face.B) * shine));

        // The Z as a thick stroke with rounded corners, turned into a solid outline.
        var stroke = (small ? 13f : 11.5f) * u;
        var (left, right, top, bottom) = (small ? 12f : 15f, small ? 50f : 52f, small ? 9f : 8f, small ? 47f : 46f);
        using var z = new GraphicsPath();
        z.AddLines([new PointF(left * u, top * u), new PointF(right * u, top * u), new PointF(left * u, bottom * u), new PointF(right * u, bottom * u)]);
        using (var pen = new Pen(Color.Black, stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            z.Widen(pen);

        // Solid extrusion: the shape stamped repeatedly towards the lower left, like the "O".
        var depth = (small ? 6f : 9f) * u;
        var steps = Math.Max(2, (int)Math.Ceiling(depth));
        using (var sideBrush = new SolidBrush(side))
        {
            for (var i = steps; i >= 1; i--)
            {
                var d = depth * i / steps;
                var state = g.Save();
                g.TranslateTransform(-d, d);
                g.FillPath(sideBrush, z);
                g.Restore(state);
            }
        }

        using (var faceBrush = new SolidBrush(face))
            g.FillPath(faceBrush, z);

        if (eye)
        {
            // A flat almond in the dark color with a slit pupil in the face color, on the diagonal.
            var c = new PointF((left + right) / 2f * u, (top + bottom) / 2f * u);
            var w = (small ? 22f : 24f) * u;
            var h = (small ? 13f : 12f) * u;
            using var almond = Almond(c, w, h);
            using (var sideBrush = new SolidBrush(side))
                g.FillPath(sideBrush, almond);
            using var pupil = Almond(c, h * 0.42f, h * 0.86f, vertical: true);
            using var faceBrush = new SolidBrush(face);
            g.FillPath(faceBrush, pupil);
        }

        return bitmap;
    }

    private static GraphicsPath Almond(PointF c, float width, float height, bool vertical = false)
    {
        var path = new GraphicsPath();
        if (!vertical)
        {
            var l = new PointF(c.X - width / 2, c.Y);
            var r = new PointF(c.X + width / 2, c.Y);
            path.AddBezier(l, new PointF(c.X - width / 4, c.Y - height * 0.7f), new PointF(c.X + width / 4, c.Y - height * 0.7f), r);
            path.AddBezier(r, new PointF(c.X + width / 4, c.Y + height * 0.7f), new PointF(c.X - width / 4, c.Y + height * 0.7f), l);
        }
        else
        {
            var t = new PointF(c.X, c.Y - height / 2);
            var b = new PointF(c.X, c.Y + height / 2);
            path.AddBezier(t, new PointF(c.X + width * 0.7f, c.Y - height / 4), new PointF(c.X + width * 0.7f, c.Y + height / 4), b);
            path.AddBezier(b, new PointF(c.X - width * 0.7f, c.Y + height / 4), new PointF(c.X - width * 0.7f, c.Y - height / 4), t);
        }
        path.CloseFigure();
        return path;
    }

    /// <summary>Developer helper: previews of both variants and all states, on light and dark backgrounds.</summary>
    public static void ExportPreviews(string dir)
    {
        Directory.CreateDirectory(dir);
        int[] sizes = [256, 64, 32, 16];
        Color[] faces = [Lime, Orange, Gray];
        foreach (var eye in new[] { false, true })
        {
            foreach (var bg in new[] { Color.White, Color.FromArgb(32, 32, 32) })
            {
                const int pad = 16;
                var width = pad + faces.Length * (sizes.Sum() + pad * sizes.Length) + pad;
                using var sheet = new Bitmap(width, 256 + 2 * pad);
                using var g = Graphics.FromImage(sheet);
                g.Clear(bg);
                var x = pad;
                foreach (var face in faces)
                {
                    foreach (var s in sizes)
                    {
                        using var icon = Render(s, face, eye);
                        g.DrawImage(icon, x, pad + (256 - s) / 2, s, s);
                        x += s + pad;
                    }
                    x += pad;
                }
                sheet.Save(Path.Combine(dir, $"{(eye ? "eye" : "plain")}-{(bg == Color.White ? "light" : "dark")}.png"));
            }
            // The tray "working" pulse, 64 px per frame.
            using (var strip = new Bitmap(12 * 72, 72))
            using (var sg = Graphics.FromImage(strip))
            {
                sg.Clear(Color.FromArgb(32, 32, 32));
                for (var i = 0; i < 12; i++)
                {
                    var t = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 12));
                    using var frame = Render(64, Lime, false, 0.55f * t);
                    sg.DrawImage(frame, i * 72 + 4, 4, 64, 64);
                }
                strip.Save(Path.Combine(dir, "pulse.png"));
            }
            using var big = Render(256, Lime, eye);
            big.Save(Path.Combine(dir, $"{(eye ? "eye" : "plain")}-256.png"));
        }
    }
}
