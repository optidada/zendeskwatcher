using System.Drawing.Imaging;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// Developer helper (--render-modes &lt;dir&gt;): stills of the pill (closed and open), the ticker with three items and
/// an achievement (rising and in place), in two themes, on a plain backdrop the size of a small screen area.
/// </summary>
internal static class ModeRenders
{
    public static void Render(string dir)
    {
        Directory.CreateDirectory(dir);
        var samples = Notifier.TestSamples(null).Where(c => c.TicketCount <= 1).Take(3).ToList();
        samples[0] = samples[0] with { Priority = "urgent" };
        samples[1] = samples[1] with { Priority = "high" };
        samples[2] = samples[2] with { Priority = "normal" };
        foreach (var theme in new[] { NotificationTheme.Optimizely, NotificationTheme.Terminal })
        {
            var palette = PopupPalette.For(theme);
            var accent = palette.Accent("urgent", NotificationKind.NewTicket);
            var summary = CompactText.PillSummary(samples);

            using (var pill = PillHost.Draw(summary, palette, accent, 1f, expanded: false, hover: false))
                pill.Save(Path.Combine(dir, $"{theme}-pill-closed.png"));

            // Open: the pill with two cards below it, as PopupManager lays them out.
            var cards = samples.Take(2).Select(c => new PopupForm(c, palette, 10, 1f)).ToList();
            var achievement = new PopupForm(samples[0], palette, 10, 1f, 1f, AchievementStyle.Decoration);
            if (palette.Terminal)
                Thread.Sleep(1500); // typed out
            using (var screen = Backdrop(900, 720))
            using (var g = Graphics.FromImage(screen))
            using (var pill = PillHost.Draw(summary, palette, accent, 1f, expanded: true, hover: true))
            {
                var area = new Rectangle(0, 0, screen.Width, screen.Height);
                g.DrawImage(pill, (area.Width - pill.Width) / 2, 10);
                var y = PillHost.CardsTop(area, 1f);
                foreach (var card in cards)
                {
                    using var bitmap = Snapshot(card);
                    g.DrawImage(bitmap, (area.Width - bitmap.Width) / 2, y);
                    y += bitmap.Height + 10;
                }
                screen.Save(Path.Combine(dir, $"{theme}-pill-open.png"));
            }

            using (var ticker = TickerBar.RenderStill(samples, theme, 1400, 1100))
            using (var screen = Backdrop(1400, 140))
            using (var g = Graphics.FromImage(screen))
            {
                g.DrawImage(ticker, 0, screen.Height - ticker.Height);
                screen.Save(Path.Combine(dir, $"{theme}-ticker.png"));
            }

            using (var card = Snapshot(achievement))
            {
                foreach (var (name, rise, alpha) in new[] { ("rising", 0.55f, 0.85f), ("in-place", 1f, 1f) })
                {
                    using var screen = Backdrop(760, card.Height + 120);
                    using var g = Graphics.FromImage(screen);
                    var rest = screen.Height - 16 - card.Height;
                    var y = rest + (int)((1 - rise) * (card.Height + 40));
                    using var attributes = new ImageAttributes();
                    attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                    g.DrawImage(card, new Rectangle((screen.Width - card.Width) / 2, y, card.Width, card.Height), 0, 0, card.Width, card.Height,
                        GraphicsUnit.Pixel, attributes);
                    screen.Save(Path.Combine(dir, $"{theme}-achievement-{name}.png"));
                }
            }
            foreach (var form in cards.Append(achievement))
                form.Dispose();
        }
    }

    /// <summary>
    /// Developer helper (--try-mode &lt;List|Deck|Peek|Pill|Ticker|Achievement&gt; &lt;seconds&gt; &lt;file&gt;): shows three
    /// silent samples (Short stay times) in that way on screen, without the tray app, twice, and writes to the file how
    /// they ended, the CPU while shown and idle afterwards (&lt;seconds&gt; in the last round), timer messages while idle,
    /// and handles / GDI / USER objects / memory after each round.
    /// </summary>
    public static void Try(PopupStacking mode, int seconds, string file)
    {
        var settings = new Settings { Stacking = mode, TicketDisplayLength = DisplayLength.Short, PausePopupsWhenAway = false };
        foreach (var style in new[] { settings.NewTicketStyle, settings.MyTicketStyle, settings.HelpStyle, settings.CcTicketStyle })
            style.Sound = ToastSound.Silent;
        var outcomes = new List<string>();
        var manager = new PopupManager((_, _) => { }, _ => { }, (content, result) => outcomes.Add($"{result} after {DateTime.Now:HH:mm:ss}"), _ => { });
        var samples = Notifier.TestSamples(null).Where(c => c.TicketCount <= 1).Take(3)
            .Select((c, i) => c with { Priority = i == 1 ? "normal" : "low" }).ToList();

        // Two rounds of three samples, each until all three have closed (at most 60 s), then idle: the CPU while they're
        // there and after they're gone, timer messages while idle (a timer left running), handles and memory per round.
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var timers = new TimerCounter();
        Application.AddMessageFilter(timers);
        string Resources()
        {
            process.Refresh();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            return $"handles {process.HandleCount}, GDI {GetGuiResources(process.Handle, 0)}, USER {GetGuiResources(process.Handle, 1)}, " +
                $"private {process.PrivateMemorySize64 / 1024 / 1024} MB, working set {process.WorkingSet64 / 1024 / 1024} MB";
        }
        double Cpu() { process.Refresh(); return process.TotalProcessorTime.TotalMilliseconds; }
        void Pump(Func<bool> until, TimeSpan max)
        {
            var start = DateTime.UtcNow;
            while (!until() && DateTime.UtcNow - start < max)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }
        string Idle(int idleSeconds)
        {
            timers.Count = 0;
            var cpu = Cpu();
            Pump(() => false, TimeSpan.FromSeconds(idleSeconds));
            var used = Cpu() - cpu;
            return $"idle {idleSeconds} s: CPU {used:0} ms = {used / (idleSeconds * 10.0):0.00}%, WM_TIMER {timers.Count}";
        }

        var lines = new List<string> { $"{mode}: start {Resources()}", "  baseline " + Idle(3) };
        for (var round = 1; round <= 2; round++)
        {
            var closedBefore = outcomes.Count;
            var cpu = Cpu();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var sample in samples)
                manager.Show(sample with { IsSample = true }, settings);
            // Settled (in place, counting down or waiting): 2 s after showing, for 5 s, if none was closed meanwhile.
            Pump(() => outcomes.Count > closedBefore, TimeSpan.FromSeconds(2));
            var settledCpu = Cpu();
            Pump(() => outcomes.Count > closedBefore, TimeSpan.FromSeconds(5));
            var settled = outcomes.Count == closedBefore ? $"settled 5 s: {(Cpu() - settledCpu) / 50.0:0.00}%; " : "settled: (one closed early); ";
            Pump(() => outcomes.Count >= closedBefore + 3, TimeSpan.FromSeconds(53));
            var shown = clock.Elapsed.TotalSeconds;
            var used = Cpu() - cpu;
            lines.Add($"  round {round}: {settled}closed {outcomes.Count - closedBefore}/3 in {shown:0.0} s ({string.Join(", ", outcomes.Skip(closedBefore))}), CPU {used:0} ms = {used / (shown * 10):0.00}% while shown");
            if (outcomes.Count < closedBefore + 3)
                foreach (var sample in samples)
                    manager.CloseSame(sample with { IsSample = true });
            lines.Add("    after " + Idle(round == 2 ? Math.Max(3, seconds) : 3) + "; " + Resources());
        }
        File.AppendAllText(file, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        Application.RemoveMessageFilter(timers);
    }

    private sealed class TimerCounter : IMessageFilter
    {
        public int Count;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == 0x0113)
                Count++;
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);

    private static Bitmap Snapshot(PopupForm form)
    {
        var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }

    /// <summary>A plain, desktop-like backdrop (a soft gradient).</summary>
    private static Bitmap Backdrop(int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, width, height),
            Color.FromArgb(70, 96, 130), Color.FromArgb(150, 170, 190), System.Drawing.Drawing2D.LinearGradientMode.Vertical);
        g.FillRectangle(brush, 0, 0, width, height);
        return bitmap;
    }
}
