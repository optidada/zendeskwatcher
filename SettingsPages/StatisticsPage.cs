using System.Diagnostics;
using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Insights: what's waiting right now, ticket notifications of the last 30 days, and what was done with them.</summary>
internal sealed class StatisticsPage : SettingsPage
{
    private static readonly (NotificationKind Kind, string Label)[] Kinds =
    [
        (NotificationKind.NewTicket, "New tickets"), (NotificationKind.MyTicket, "My tickets"),
        (NotificationKind.CcTicket, "CC / following"), (NotificationKind.HelpColleague, "Colleagues' tickets")
    ];

    private static readonly (StatOutcome Outcome, string Label)[] Columns =
    [
        (StatOutcome.Shown, "Shown"), (StatOutcome.OpenedTicket, "Opened"),
        (StatOutcome.Took, "Took it"), (StatOutcome.Snoozed, "Snoozed"), (StatOutcome.Closed, "Closed"), (StatOutcome.TimedOut, "Timed out"),
        (StatOutcome.Copied, "Copied")
    ];

    private readonly Label _nowStatus = Theme.Label("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
    private readonly TableLayoutPanel _now = new() { ColumnCount = 3, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 4, 0, 0) };
    private readonly ToolTip _tips = new();
    private readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, Kit.CardWidth - 44);
    private readonly TableLayoutPanel _table = new() { AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 8) };
    private readonly Button _clear = Theme.SecondaryButton("Clear statistics");
    private readonly TableLayoutPanel _insights = new() { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty };

    public StatisticsPage(SettingsContext context)
        : base(context, "Insights", "What's waiting right now, ticket notifications in the last 30 days, what you did with them, and " +
            "suggestions. Counted on this PC only; nothing is sent anywhere.")
    {
        var now = new Card("Right now", null, Kit.CardWidth);
        now.Body.Controls.Add(_nowStatus);
        now.Body.Controls.Add(_now);
        Panel.Controls.Add(now);
        // Updated after every check while the window is open.
        WatchStatus.Changed += ShowNow;
        Panel.Disposed += (_, _) => WatchStatus.Changed -= ShowNow;

        var card = new Card("Last 30 days", null, Kit.CardWidth);
        card.Body.Controls.Add(_summary);
        card.Body.Controls.Add(_table);
        card.Body.Controls.Add(Theme.Label(
            "Opened: the ticket, or Zendesk for a notification about several tickets. Timed out: disappeared on its own, " +
            "or went to the Windows notification center. Copied: copied for Teams (it can be opened as well). A snoozed notification " +
            "counts again for what you do when it comes back. " +
            "Windows notifications are only counted while Zendesk Watcher runs.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        _clear.Margin = new Padding(0, 10, 0, 0);
        card.Body.Controls.Add(_clear);
        Panel.Controls.Add(card);

        var suggestions = new Card("Suggestions", "Based on the last 30 days: is a kind of notification ignored, closed right away, or just too frequent?", Kit.CardWidth);
        suggestions.Body.Controls.Add(_insights);
        Panel.Controls.Add(suggestions);

        _clear.Click += (_, _) =>
        {
            if (!ThemedDialog.Confirm(Context.Window, "Clear the statistics?",
                    "The counts of the last 30 days are removed. This can't be undone.", "Clear"))
                return;
            Context.Notifier.Stats.Clear();
            Refresh();
        };
    }

    public override void Load(Settings settings) => Refresh();

    public override void Apply(Settings settings) { } // nothing to save: the statistics are kept as they happen

    public override void OnShowing() => Refresh();

    private void Refresh()
    {
        ShowNow();
        var totals = Context.Notifier.Stats.Totals(DateOnly.FromDateTime(DateTime.Now));
        // "Opened" includes opening Zendesk from a notification about several tickets: the same reaction, no single ticket.
        int Count(NotificationKind kind, StatOutcome outcome) => outcome == StatOutcome.OpenedTicket
            ? totals.GetValueOrDefault((kind, StatOutcome.OpenedTicket)) + totals.GetValueOrDefault((kind, StatOutcome.OpenedZendesk))
            : totals.GetValueOrDefault((kind, outcome));
        int Total(StatOutcome outcome) => Kinds.Sum(k => Count(k.Kind, outcome));

        var shown = Total(StatOutcome.Shown);
        var acted = Total(StatOutcome.OpenedTicket) + Total(StatOutcome.Took);
        _summary.Text = shown == 0
            ? "No ticket notifications in the last 30 days yet."
            : $"{shown} ticket notification{(shown == 1 ? "" : "s")}. You opened or took {acted} ({Percent(acted, shown)}), " +
              $"snoozed {Total(StatOutcome.Snoozed)}, closed {Total(StatOutcome.Closed)}, and {Total(StatOutcome.TimedOut)} timed out.";

        _table.SuspendLayout();
        foreach (var old in _table.Controls.Cast<Control>().ToList())
            old.Dispose();
        _table.Controls.Clear();
        _table.ColumnStyles.Clear();
        _table.ColumnCount = Columns.Length + 1;
        // Columns size to their contents, so nothing is cut off at any display scaling.
        for (var c = 0; c <= Columns.Length; c++)
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _table.Controls.Add(Cell("", Theme.Small, Theme.TextMuted, left: true), 0, 0);
        for (var c = 0; c < Columns.Length; c++)
            _table.Controls.Add(Cell(Columns[c].Label, Theme.Small, Theme.TextMuted), c + 1, 0);

        var row = 1;
        foreach (var (kind, label) in Kinds)
        {
            _table.Controls.Add(Cell(label, Theme.Body, Theme.Text, left: true), 0, row);
            for (var c = 0; c < Columns.Length; c++)
            {
                var count = Count(kind, Columns[c].Outcome);
                _table.Controls.Add(Cell(count == 0 ? "–" : count.ToString(), Theme.Body, count == 0 ? Theme.TextMuted : Theme.Text), c + 1, row);
            }
            row++;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        ShowInsights(StatsInsights.Analyze(totals, Context.Notifier.Stats.ActiveDays(today), Context.Current(), Context.Notifier.Stats.ClientTotals(today)));

        _table.Controls.Add(Cell("Total", Theme.BodyBold, Theme.Text, left: true), 0, row);
        for (var c = 0; c < Columns.Length; c++)
            _table.Controls.Add(Cell(Total(Columns[c].Outcome).ToString(), Theme.BodyBold, Theme.Text), c + 1, row);
        _table.ResumeLayout();
    }

    private void ShowInsights(List<StatsInsights.Insight> insights)
    {
        _insights.SuspendLayout();
        foreach (var old in _insights.Controls.Cast<Control>().ToList())
            old.Dispose();
        _insights.Controls.Clear();
        _insights.ColumnStyles.Clear();
        _insights.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // the marks, so the texts line up
        _insights.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var r = 0;
        foreach (var insight in insights)
        {
            var (mark, color) = insight.Tone switch
            {
                StatsInsights.Tone.Good => ("✓", Theme.Success),
                StatsInsights.Tone.Warning => ("!", Theme.Warning),
                _ => ("•", Theme.TextMuted)
            };
            var markLabel = Theme.Label(mark, Theme.BodyBold, color);
            markLabel.Margin = new Padding(0, 2, 8, 6);
            var text = Theme.Label(insight.Text, Theme.Body, Theme.Text, Kit.CardWidth - 70);
            text.Margin = new Padding(0, 2, 0, 6);
            _insights.Controls.Add(markLabel, 0, r);
            _insights.Controls.Add(text, 1, r);
            r++;
        }
        _insights.ResumeLayout();
    }

    /// <summary>The "Right now" card: the last check's numbers, each a link to that list in Zendesk.</summary>
    private void ShowNow()
    {
        if (Panel.IsDisposed)
            return;
        var snapshot = WatchStatus.Latest;
        var settings = Context.Current();
        _nowStatus.Text = (snapshot, WatchStatus.Problem) switch
        {
            (null, { } problem) => problem,
            (null, null) => "Waiting for the first check\u2026",
            (_, { } problem) => $"From the check at {snapshot.CheckedAt:HH:mm}. {problem}",
            _ => $"From the last check, at {snapshot.CheckedAt:HH:mm}. Click a number to open the list in Zendesk."
        };

        _now.SuspendLayout();
        foreach (var old in _now.Controls.Cast<Control>().ToList())
            old.Dispose();
        _now.Controls.Clear();
        _now.Visible = snapshot is not null;
        if (snapshot is not null)
        {
            var row = 0;
            void Row(string label, Control count, params Control[] details)
            {
                _now.Controls.Add(Cell(label, Theme.Body, Theme.Text, left: true), 0, row);
                count.Anchor = AnchorStyles.Right; // numbers line up on the right
                _now.Controls.Add(count, 1, row);
                var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(14, 0, 0, 0) };
                line.Controls.AddRange(details);
                _now.Controls.Add(line, 2, row);
                row++;
            }

            // Unassigned: the total, then per group (each a link); the total is a link when it's one group.
            var groups = snapshot.Groups;
            if (groups.Count == 0)
                Row("Unassigned in your groups", Cell("\u2013", Theme.BodyBold, Theme.TextMuted), Muted("No groups chosen (Groups page)"));
            else
            {
                var details = new List<Control>();
                if (groups.Count > 1)
                    foreach (var g in groups.Where(g => g.Count > 0))
                    {
                        if (details.Count > 0)
                            details.Add(Muted("\u00b7"));
                        details.Add(Link($"{TicketWatcher.GroupLabel(g.Group)}: {g.Count}", UnassignedQuery(g.Group), $"Unassigned tickets in {TicketWatcher.GroupLabel(g.Group)}"));
                    }
                var total = groups.Count == 1
                    ? Link(snapshot.Unassigned.ToString(), UnassignedQuery(groups[0].Group), $"Unassigned tickets in {TicketWatcher.GroupLabel(groups[0].Group)}", bold: true)
                    : Cell(snapshot.Unassigned.ToString(), Theme.BodyBold, Theme.Text);
                Row("Unassigned in your groups", total, [.. details]);
                if (snapshot.OldestUnassigned is { } oldest) // its own row: next to the groups it doesn't fit
                    Row("Oldest unassigned", Cell("", Theme.Body, Theme.Text), Muted(UI.Popups.NotificationContent.Ago(DateTimeOffset.UtcNow - oldest)));
            }

            if (snapshot.Mine is var (open, pending, hold))
            {
                var parts = new List<Control>();
                foreach (var (count, label, query) in new[] { (open, "open", "status<pending"), (pending, "pending", "status:pending"), (hold, "on hold", "status:hold") })
                {
                    if (count == 0)
                        continue;
                    if (parts.Count > 0)
                        parts.Add(Muted("\u00b7"));
                    parts.Add(Link($"{count} {label}", $"assignee:me {query}", $"Your {label} tickets"));
                }
                Row("Your tickets", Link((open + pending + hold).ToString(), "assignee:me status<solved", "Your unsolved tickets", bold: true), [.. parts]);
            }
            else
                Row("Your tickets", Cell("\u2013", Theme.BodyBold, Theme.TextMuted), Muted("Not watched (Notifications page)"));

            if (snapshot.Cc is { } cc)
                Row("CC / following", Link(cc + (snapshot.CcAtLeast ? "+" : ""), "cc:me status<solved",
                        "Unsolved tickets you're CC'd on (Zendesk's search can't list followed tickets)", bold: true),
                    snapshot.CcAtLeast ? [Muted("at least: older ones may be missing")] : []);
            else
                Row("CC / following", Cell("\u2013", Theme.BodyBold, Theme.TextMuted), Muted("Not watched (Notifications page)"));
        }
        _now.ResumeLayout();

        string UnassignedQuery(WatchedGroup group) => $"group:{(group.IsNoGroup ? "none" : group.Id.ToString())} assignee:none status<solved";

        Label Link(string text, string query, string tip, bool bold = false)
        {
            var link = Kit.LinkLabel(text, bold ? Theme.BodyBold : Theme.Body);
            link.Margin = bold ? new Padding(4, 4, 0, 4) : new Padding(0, 4, 4, 4);
            _tips.SetToolTip(link, tip + " \u2013 opens in Zendesk");
            link.Click += (_, _) => OpenSearch(settings.BaseUrl, query);
            return link;
        }
    }

    private static Label Muted(string text)
    {
        var label = Theme.Label(text, Theme.Body, Theme.TextMuted);
        label.Margin = new Padding(0, 4, 4, 4);
        return label;
    }

    /// <summary>Opens a ticket search in Zendesk's agent interface.</summary>
    private static void OpenSearch(string baseUrl, string query)
    {
        var url = $"{baseUrl}/agent/search/1?type=ticket&q={Uri.EscapeDataString(query)}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {url}", ex);
        }
    }

    private static string Percent(int part, int whole) => whole == 0 ? "0%" : $"{Math.Round(100.0 * part / whole)}%";

    private static Label Cell(string text, Font font, Color color, bool left = false)
    {
        var label = Theme.Label(text, font, color);
        label.Anchor = left ? AnchorStyles.Left : AnchorStyles.Right; // numbers line up on the right
        label.Margin = left ? new Padding(0, 4, 6, 4) : new Padding(4, 4, 0, 4);
        return label;
    }

    /// <summary>For the screenshot tool: a believable month, kept in memory only.</summary>
    internal static NotificationStats SampleStats()
    {
        var stats = new NotificationStats();
        var today = DateOnly.FromDateTime(DateTime.Now);
        void Add(NotificationKind kind, StatOutcome outcome, int count)
        {
            for (var i = 0; i < count; i++)
                stats.Record(kind, outcome, today.AddDays(-(i % 20))); // spread over 20 days
        }
        Add(NotificationKind.NewTicket, StatOutcome.Shown, 48);
        Add(NotificationKind.NewTicket, StatOutcome.OpenedTicket, 14);
        Add(NotificationKind.NewTicket, StatOutcome.Took, 9);
        Add(NotificationKind.NewTicket, StatOutcome.OpenedZendesk, 2);
        Add(NotificationKind.NewTicket, StatOutcome.Snoozed, 4);
        Add(NotificationKind.NewTicket, StatOutcome.Closed, 6);
        Add(NotificationKind.NewTicket, StatOutcome.TimedOut, 17);
        Add(NotificationKind.MyTicket, StatOutcome.Shown, 63);
        Add(NotificationKind.MyTicket, StatOutcome.OpenedTicket, 31);
        Add(NotificationKind.MyTicket, StatOutcome.Closed, 12);
        Add(NotificationKind.MyTicket, StatOutcome.TimedOut, 20);
        Add(NotificationKind.CcTicket, StatOutcome.Shown, 7);
        Add(NotificationKind.CcTicket, StatOutcome.OpenedTicket, 2);
        Add(NotificationKind.CcTicket, StatOutcome.TimedOut, 5);
        Add(NotificationKind.HelpColleague, StatOutcome.Shown, 3);
        Add(NotificationKind.HelpColleague, StatOutcome.OpenedTicket, 2);
        Add(NotificationKind.HelpColleague, StatOutcome.Snoozed, 1);
        Add(NotificationKind.NewTicket, StatOutcome.Copied, 3);
        Add(NotificationKind.HelpColleague, StatOutcome.Copied, 1);
        return stats;
    }
}
