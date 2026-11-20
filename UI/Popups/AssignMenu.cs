using ZendeskWatcher.Tracking;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// "▾" next to Take it on a new-ticket notification: who could take it (<see cref="TicketWatcher.FindCandidatesAsync"/>),
/// read when the menu opens. Picking one asks first, then assigns the ticket to them.
/// </summary>
internal static class AssignMenu
{
    /// <summary>Set by the app once signed in; null: the ▾ isn't shown.</summary>
    public static Func<long, Task<(List<AssignCandidate> Candidates, string? Note)>>? Find { get; set; }

    /// <summary>A new-ticket notification came on screen: reads its group (id) early, so ▾ opens quickly. Set by the app.</summary>
    public static Action<long>? Ready { get; set; }

    /// <summary>
    /// The menu, filled in when the candidates are read. <paramref name="chosen"/>: the picked colleague;
    /// <paramref name="filled"/>: the lines are in (the menu got taller: show it again so it stays on screen).
    /// </summary>
    public static ContextMenuStrip Build(long ticketId, bool sample, Action<AssignCandidate> chosen, Action filled)
    {
        var (menu, waiting) = Start("Looking at your colleagues…");
        if (sample) // made-up colleagues, so Try it shows what it looks like; picking one does nothing
            Fill(menu, waiting, SampleCandidates(), "A sample with made-up colleagues: on real new tickets, yours. Click to assign.", ticketId, _ => { });
        else
            _ = FillAsync();
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose); // after the click is handled
        return menu;

        async Task FillAsync()
        {
            List<AssignCandidate> candidates;
            string? note;
            try
            {
                (candidates, note) = Find is { } find ? await find(ticketId) : ([], "Not signed in.");
            }
            catch (Exception ex)
            {
                Log.Error($"Could not find who could take #{ticketId}", ex);
                (candidates, note) = ([], "Couldn't read your colleagues just now.");
            }
            if (menu.IsDisposed)
                return;
            Fill(menu, waiting, candidates, note, ticketId, chosen);
            if (menu.Visible)
                filled();
        }
    }

    /// <summary>The heading and a "looking…" line, in the app theme's colors (like Options ▾).</summary>
    private static (ContextMenuStrip Menu, ToolStripItem Waiting) Start(string waitingText)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var headerFont = new Font(menu.Font, FontStyle.Bold);
        menu.Disposed += (_, _) => headerFont.Dispose();
        menu.Items.Add(new ToolStripMenuItem("Who could take it?") { Enabled = false, Font = headerFont });
        menu.Items.Add(new ToolStripSeparator());
        var waiting = new ToolStripMenuItem(waitingText) { Enabled = false };
        menu.Items.Add(waiting);
        ThemedMenu.Apply(menu, Theme.Current);
        return (menu, waiting);
    }

    /// <summary>One line per colleague ("Assign to …"), then how they were picked (or why there's nobody).</summary>
    private static void Fill(ContextMenuStrip menu, ToolStripItem waiting, List<AssignCandidate> candidates, string? note,
        long ticketId, Action<AssignCandidate> chosen)
    {
        menu.SuspendLayout();
        menu.Items.Remove(waiting);
        waiting.Dispose();
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in candidates)
        {
            var item = new ToolStripMenuItem(AssignCandidates.Line(candidate, now).Replace("&", "&&")) { ToolTipText = $"Assign #{ticketId} to {candidate.Name}" };
            item.Click += (_, _) => chosen(candidate);
            menu.Items.Add(item);
        }
        if (candidates.Count > 0)
            menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem((note ?? "Least busy first; in working hours and active in Zendesk. Click to assign.").Replace("&", "&&"))
        {
            Enabled = false, Font = Theme.Small
        });
        ThemedMenu.Apply(menu, Theme.Current); // the new lines too
        menu.ResumeLayout();
    }

    private static List<AssignCandidate> SampleCandidates()
    {
        var now = DateTimeOffset.UtcNow;
        return [new(1, "Peter Gibbons", 2, 0, now.AddMinutes(-4)), new(2, "Maurice Moss", 4, 1, now.AddMinutes(-12)), new(3, "Samir Nagheenanajar", 5, 0, null)];
    }

    /// <summary>Developer helper (--render-menu): the menu with sample colleagues, as a PNG.</summary>
    public static void RenderSample(string file)
    {
        var (menu, waiting) = Start("");
        using (menu)
        {
            Fill(menu, waiting, SampleCandidates(), null, 123456, _ => { });
            menu.Show(-3000, -3000); // off-screen: lays out the items
            menu.Items[2].Select();
            using var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
            menu.Close();
            bitmap.Save(file);
        }
    }
}
