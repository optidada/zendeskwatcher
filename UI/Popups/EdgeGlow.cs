using System.Runtime.InteropServices;
using ZendeskWatcher.Tracking;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// A thin colored glow that pulses around the edges of the screen the notifications are on (Look &amp; sound → "Glow at
/// the screen edge"), for Urgent tickets, SLA breaches and important clients (<see cref="EdgeGlowRule"/>). A
/// click-through, never-activated layered window: drawn once with per-pixel alpha, then only its overall alpha changes
/// for three gentle pulses (~2.5 s), after which it's destroyed. No timer runs when nothing glows.
/// </summary>
internal sealed class EdgeGlow : NativeWindow, IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(2500);
    private const int Pulses = 3;
    private const byte MaxAlpha = 235;

    private static EdgeGlow? _current;
    /// <summary>Notifications arriving together glow once: no new glow this soon after the last one ended.</summary>
    private static readonly TimeSpan Calm = TimeSpan.FromSeconds(10);
    private static DateTime _lastEnded = DateTime.MinValue;
    private static NotificationContent? _whenBack;
    private static Color _whenBackColor;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Action? _done;
    private bool _disposed;

    /// <summary>
    /// Glows for <paramref name="content"/> when the setting is on and the rule says so. While the user is away (or
    /// presenting) it waits until they're back (<see cref="UserBack"/>), so the glow isn't wasted on an empty room.
    /// </summary>
    public static void Consider(NotificationContent content, Settings settings)
    {
        if (!settings.EdgeGlow)
            return;
        var reason = EdgeGlowRule.For(content, DateTimeOffset.UtcNow, content.IsSample);
        if (reason == GlowReason.None)
            return;
        var color = reason == GlowReason.ImportantClient
            ? PopupPalette.For(settings.ActiveNotificationTheme).Accent(content.Priority, content.Kind)
            : PriorityTag.ColorOf("urgent");
        if (PopupForm.UserAway() || !PopupManager.CanShowPopups())
        {
            _whenBack = content;
            _whenBackColor = color;
            return;
        }
        Flash(color);
    }

    /// <summary>The user is back: the glow that waited, if its notification is still on screen.</summary>
    public static void UserBack(Func<NotificationContent, bool> stillShown)
    {
        if (_whenBack is not { } content)
            return;
        _whenBack = null;
        if (stillShown(content))
            Flash(_whenBackColor);
    }

    /// <summary>
    /// Pulses the glow once on the monitor notifications appear on (<see cref="NotificationScreen"/>); ignored while one is running or just
    /// after one (a burst of notifications glows once, not again and again).
    /// </summary>
    public static void Flash(Color color, Action? done = null)
    {
        if (_current != null || DateTime.UtcNow - _lastEnded < Calm)
        {
            done?.Invoke();
            return;
        }
        try
        {
            _current = new EdgeGlow(color, NotificationScreen.Current, done);
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the screen-edge glow", ex);
            _current = null;
            done?.Invoke();
        }
    }

    /// <summary>Developer helper (--test-glow): one glow in the priority color, then exit.</summary>
    public static void RunTest()
    {
        using var context = new ApplicationContext();
        var timer = new System.Windows.Forms.Timer { Interval = 300 }; // after the message loop runs
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            Flash(PriorityTag.ColorOf("urgent"), () => context.ExitThread());
        };
        timer.Start();
        Application.Run(context);
    }

    private EdgeGlow(Color color, Screen screen, Action? done)
    {
        _done = done;
        var bounds = screen.Bounds;
        CreateHandle(new CreateParams
        {
            Caption = "Zendesk Watcher glow",
            Style = unchecked((int)WS_POPUP),
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height
        });
        try
        {
            Draw(color, bounds, NotificationScreen.ScaleOf(screen)); // that monitor's own scaling
        }
        catch
        {
            // Not shown: no invisible window or timer left behind (Flash logs it).
            _timer.Dispose();
            DestroyHandle();
            throw;
        }
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        _timer.Tick += (_, _) => Animate();
        _clock.Start();
        _timer.Start();
    }

    /// <summary>Draws the glow once into a premultiplied 32-bit bitmap and hands it to the window, fully transparent.</summary>
    private void Draw(Color color, Rectangle bounds, float scale)
    {
        int w = bounds.Width, h = bounds.Height;
        var core = Math.Max(2, (int)Math.Round(4 * scale));   // the bright line at the edge
        var band = Math.Max(core + 4, (int)Math.Round(22 * scale)); // with the soft fade inwards
        band = Math.Min(band, Math.Min(w, h) / 2);

        // Alpha by distance from the nearest edge: full at the edge, then easing out to nothing.
        var alphaAt = new byte[band];
        for (var d = 0; d < band; d++)
        {
            var f = d < core ? 1.0 : 1.0 - (d - core + 1) / (double)(band - core + 1);
            alphaAt[d] = (byte)Math.Round(255 * (d < core ? 1.0 : f * f * 0.75));
        }
        int Pixel(int distance)
        {
            if (distance >= band)
                return 0;
            int a = alphaAt[distance];
            return (a << 24) | (color.R * a / 255 << 16) | (color.G * a / 255 << 8) | (color.B * a / 255);
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bitmap = CreateDIBSection(screenDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("CreateDIBSection failed");
        }
        try
        {
            // The DIB starts all zero (transparent): only the bands around the edges are written.
            var row = new int[w];
            var side = new int[band];
            for (var x = 0; x < band; x++)
                side[x] = Pixel(x);
            for (var y = 0; y < h; y++)
            {
                var dy = Math.Min(y, h - 1 - y);
                var offset = (long)y * w * 4;
                if (dy < band)
                {
                    for (var x = 0; x < w; x++)
                        row[x] = Pixel(Math.Min(dy, Math.Min(x, w - 1 - x)));
                    Marshal.Copy(row, 0, bits + (nint)offset, w);
                }
                else
                {
                    Marshal.Copy(side, 0, bits + (nint)offset, band);
                    Array.Reverse(side);
                    Marshal.Copy(side, 0, bits + (nint)(offset + (w - band) * 4L), band);
                    Array.Reverse(side);
                }
            }

            var old = SelectObject(memDc, bitmap);
            var size = new SIZE { cx = w, cy = h };
            var source = new POINT();
            var position = new POINT { x = bounds.X, y = bounds.Y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 0, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screenDc, ref position, ref size, memDc, ref source, 0, ref blend, ULW_ALPHA);
            SelectObject(memDc, old);
        }
        finally
        {
            DeleteObject(bitmap); // the window keeps its own copy
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Three soft pulses: only the window's overall alpha changes, the picture stays.</summary>
    private void Animate()
    {
        var t = _clock.Elapsed.TotalMilliseconds / Duration.TotalMilliseconds;
        if (t >= 1)
        {
            Dispose();
            return;
        }
        var phase = t * Pulses % 1.0;
        var level = Math.Sin(Math.PI * phase);
        var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Round(MaxAlpha * level * level), AlphaFormat = AC_SRC_ALPHA };
        UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, ULW_ALPHA);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        DestroyHandle();
        if (_current == this)
            _current = null;
        _lastEnded = DateTime.UtcNow;
        _done?.Invoke();
    }

    /// <summary>Clicks go through to whatever is underneath, even if Windows asks.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084, WM_MOUSEACTIVATE = 0x0021, HTTRANSPARENT = -1, MA_NOACTIVATE = 3;
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = HTTRANSPARENT;
            return;
        }
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    private const uint WS_POPUP = 0x80000000;
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    private const int SW_SHOWNOACTIVATE = 4, ULW_ALPHA = 2;
    private const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    /// <summary>Only the blend changes: no new picture, position or size.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
}
