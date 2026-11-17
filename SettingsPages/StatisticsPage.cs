using System.Diagnostics;
using ZendeskWatcher.Tracking;
using ZendeskWatcher.Zendesk;
using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Insights: what's waiting right now, ticket notifications of the last 30 days, and what was done with them.</summary>
internal sealed class StatisticsPage : SettingsPage
{
    private static readonly (NotificationKind Kind, string Label)[] Kinds =
    [
        (NotificationKind.NewTicket, "New tickets"), (NotificationKind.MyTicket, "My tickets"),
        (NotificationKind.CcTicket, "CC / following"), (NotificationKind.HelpColleague, "Colleagues")
    ];

    private static readonly (StatOutcome Outcome, string Label)[] Columns =
    [
        (StatOutcome.Shown, "Shown"), (StatOutcome.Acted, "Acted on"), (StatOutcome.OpenedTicket, "Opened"),
        (StatOutcome.Took, "Took"), (StatOutcome.Snoozed, "Snoozed"), (StatOutcome.Closed, "Closed"), (StatOutcome.TimedOut, "Timed out"),
        (StatOutcome.Copied, "Copied")
    ];

    private readonly Label _nowStatus = Theme.Label("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
    private readonly Label _teamTitle = Theme.Label("Your team", Theme.BodyBold, Theme.Text);
    private readonly Label _teamStatus = Theme.Linked("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
    private readonly GridTable _team = new() { Margin = new Padding(0, 2, 0, 0) };

    /// <summary>How often the team's tickets are read again, at most (only while Insights is shown).</summary>
    private static readonly TimeSpan TeamRefresh = TimeSpan.FromMinutes(10);

    /// <summary>The team as last read: for which account and groups, when, the region, the rows, and which status names count as escalated / scheduled.</summary>
    private static (string Key, DateTime At, TimeRegion Region, List<WorkloadRow> Rows, string[] Escalated, string[] Scheduled)? _teamCache;
    private int _teamVersion;
    private readonly TableLayoutPanel _now = new() { ColumnCount = 3, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 4, 0, 0) };
    private readonly ToolTip _tips = new();
    private readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, Kit.CardWidth - 44);
    private readonly GridTable _table = new() { Margin = new Padding(0, 8, 0, 8) };
    private readonly Button _clear = Theme.SecondaryButton("Clear statistics");
    private readonly TableLayoutPanel _insights = new() { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty };

    public StatisticsPage(SettingsContext context)
        : base(context, "Insights", "What's waiting now, how you used notifications, and tips. Counted on this PC only.")
    {
        var now = new Card("Tickets right now", null, Kit.CardWidth);
        now.Body.Controls.Add(_nowStatus);
        now.Body.Controls.Add(_now);
        _teamTitle.Margin = new Padding(0, 14, 0, 0);
        now.Body.Controls.Add(_teamTitle);
        now.Body.Controls.Add(_teamStatus);
        now.Body.Controls.Add(_team);
        Panel.Controls.Add(now);
        // Updated after every check while the window is open.
        WatchStatus.Changed += ShowNow;
        Panel.Disposed += (_, _) => WatchStatus.Changed -= ShowNow;

        var card = new Card("How you used notifications · 30 days", null, Kit.CardWidth);
        card.Body.Controls.Add(_summary);
        card.Body.Controls.Add(_table);
        card.Body.Controls.Add(Theme.Label(
            "Acted on: opened, taken or copied. A snoozed notification counts for what you do when it comes back.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        _clear.Margin = new Padding(0, 10, 0, 0);
        card.Body.Controls.Add(_clear);
        Panel.Controls.Add(card);

        var suggestions = new Card("Tips for your notifications", "From how you used them in the last 30 days.", Kit.CardWidth);
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

    public override void OnShowing()
    {
        Refresh();
        RefreshTeam();
    }

    // ------------------------------------------------------------------ your team

    /// <summary>
    /// Your region's teammates in your monitored groups (from their Zendesk time zones, like the Colleagues page), and
    /// their open tickets in those groups. Read when Insights is shown, at most every 10 minutes.
    /// </summary>
    private async void RefreshTeam()
    {
        var version = ++_teamVersion;
        var settings = Context.Current();
        var groups = settings.WatchedGroups.Where(g => !g.IsNoGroup).Select(g => g.Id).Order().ToList();
        if (Context.Client is not { } client || Context.Me is not { } me)
        {
            ShowTeam(_teamCache?.Region, _teamCache?.Rows, "Sign in to see your team.");
            return;
        }
        if (groups.Count == 0)
        {
            ShowTeam(null, null, "Choose groups to monitor (Groups page) to see your team.");
            return;
        }
        var key = $"{settings.Subdomain}|{string.Join(',', groups)}";
        if (_teamCache is { } cached && cached.Key == key && DateTime.UtcNow - cached.At < TeamRefresh)
        {
            ShowTeam(cached.Region, cached.Rows, null, cached.At, cached.Escalated, cached.Scheduled);
            return;
        }
        if (_teamCache is null || _teamCache.Value.Key != key)
            ShowTeam(null, null, "Loading your team\u2026");
        try
        {
            var region = TimeRegions.Mine(me);
            var members = await Task.WhenAll(groups.Select(id => client.GetGroupMembersAsync(id, CancellationToken.None)));
            var team = members.SelectMany(m => m)
                .Where(u => u.Role != "end-user" && (u.Id == me.Id || TimeRegions.Of(TimeRegions.ZoneOf(u)) == region))
                .DistinctBy(u => u.Id)
                .ToDictionary(u => u.Id, u => u.Id == me.Id ? "You" : u.Name ?? $"User {u.Id}");
            var tickets = await Task.WhenAll(groups.Select(id => client.GetAssignedOpenTicketsAsync(id, CancellationToken.None))
                .Concat(groups.Select(id => client.GetRecentlySolvedTicketsAsync(id, TeamWorkload.SolvedDays, CancellationToken.None))));
            var statuses = (await client.GetCustomStatusesAsync(CancellationToken.None))
                .Where(s => s.AgentLabel is { Length: > 0 }).ToDictionary(s => s.Id, s => s.AgentLabel!);
            var rows = TeamWorkload.Count(tickets.SelectMany(t => t), team, me.Id, statuses);
            string[] Names(Func<string, bool> which) => [.. statuses.Values.Where(which).Distinct().Order()];
            _teamCache = (key, DateTime.UtcNow, region, rows, Names(TeamWorkload.IsEscalatedStatus), Names(n => TicketRules.IsScheduled(n)));
            if (version == _teamVersion && !Panel.IsDisposed)
            {
                var c = _teamCache.Value;
                ShowTeam(c.Region, c.Rows, null, c.At, c.Escalated, c.Scheduled);
            }
        }
        catch (Exception ex)
        {
            if (ex is not AuthRequiredException)
                Log.Error("Insights: could not read the team's tickets", ex);
            if (version == _teamVersion && !Panel.IsDisposed)
                ShowTeam(_teamCache?.Region, _teamCache?.Rows, "Couldn't read your team's tickets just now.");
        }
    }

    /// <summary>For the screenshot tool: a sample team, as if just read (some Open moved to In progress, Escalated split up).</summary>
    internal static void SetTeamSample(string subdomain, IEnumerable<long> groups, TimeRegion region, List<WorkloadRow> rows) =>
        _teamCache = ($"{subdomain}|{string.Join(',', groups.Order())}", DateTime.UtcNow, region, rows.Select(SampleRow).ToList(),
            ["Escalated internally", "Escalated to Engineering"], ["Scheduled"]);

    private static WorkloadRow SampleRow(WorkloadRow row) => row.EscalatedBy is not null ? row : row with
    {
        Open = row.Open - row.Open / 3, InProgress = row.InProgress + row.Open / 3,
        EscalatedBy = new Dictionary<string, int>
        {
            ["Escalated to Engineering"] = (row.Escalated + 1) / 2, ["Escalated internally"] = row.Escalated / 2
        }
    };

    private void ShowTeam(TimeRegion? region, List<WorkloadRow>? rows, string? message, DateTime? at = null,
        string[]? escalated = null, string[]? scheduled = null)
    {
        var key = $"{region}|{rows?.GetHashCode()}|{message}|{at:O}";
        if (key == _teamShown)
            return;
        _teamShown = key;
        _teamTitle.Text = region is { } r ? $"Your team \u00b7 {TimeRegions.Name(r)}" : "Your team";
        _teamStatus.Text = message ?? (rows is { Count: > 0 }
            ? $"Busiest first. Click a name to see their tickets; point at a heading for details. Updated " +
                $"{(at is { } read ? Clock.Time(read.ToLocalTime()) : "")}."
            : "No colleagues in your region in these groups.");
        _team.Visible = rows is { Count: > 0 };
        if (rows is { Count: > 0 })
        {
            // One column per escalated status when there are several (e.g. to Engineering, internally), else one for all.
            List<(string Text, string Tip, Func<WorkloadRow, int> Count)> columns =
            [
                ("", "", _ => 0),
                ("Open", "New and Open, also other open statuses", r => r.Open),
                ("In progress", "Statuses with “in progress” in their name", r => r.InProgress),
                ("P1/P2", "Of the Open and In progress ones: Urgent or High", r => r.UrgentOrHigh)
            ];
            if (escalated is { Length: > 1 })
                foreach (var status in escalated)
                    columns.Add((TeamWorkload.ShortEscalatedName(status), status, r => r.EscalatedBy?.GetValueOrDefault(status) ?? 0));
            else
                columns.Add(("Escalated", escalated is { Length: > 0 } e ? e[0] : "No status with “escalat” in its name", r => r.Escalated));
            columns.Add(("Scheduled", scheduled is { Length: > 0 } s ? string.Join(", ", s) : "No status with “scheduled” in its name",
                r => r.Scheduled));
            columns.Add(("Pending", "Pending and On-hold: waiting on the client or someone else", r => r.Pending));
            columns.Add(("Solved", $"Solved or closed in the last {TeamWorkload.SolvedDays} days", r => r.Solved));

            var table = new List<GridTable.Cell[]>
            {
                columns.Select(c => c.Text == "P1/P2"
                    ? new GridTable.Cell("", Theme.Small, Tip: c.Tip, Tags: ["urgent", "high"])
                    : new GridTable.Cell(c.Text, Theme.Small, GridTable.Ink.Muted, c.Tip.Length > 0 ? c.Tip : null)).ToArray()
            };
            var baseUrl = Context.Current().BaseUrl;
            var shortNames = TeamWorkload.ShortNames(rows.Select(r => r.Name));
            foreach (var row in rows)
            {
                var query = $"assignee:{row.AssigneeId} status<solved";
                var name = new GridTable.Cell(shortNames[row.Name], row.IsMe ? Theme.BodyBold : Theme.Body, GridTable.Ink.Link,
                    $"{(row.IsMe ? "Your" : $"{row.Name}'s")} unsolved tickets – opens in Zendesk", () => OpenSearch(baseUrl, query));
                table.Add([name, .. columns.Skip(1).Select(c => Number(c.Count(row), Theme.Body))]);
            }
            _team.MaxWidth = TableRoom(_team);
            _team.SetRows(table);
        }
    }

    /// <summary>The width a table may use in its card (names give way when it's wider).</summary>
    private static int TableRoom(Control table) => table.Parent?.Parent is Card card
        ? card.Width - card.Padding.Horizontal - table.Margin.Horizontal
        : int.MaxValue;

    private static GridTable.Cell Number(int n, Font font) =>
        new(n == 0 ? "–" : n.ToString(), font, n == 0 ? GridTable.Ink.Muted : GridTable.Ink.Text);

    /// <summary>What each part last showed: shown again unchanged, it isn't rebuilt (about 100 labels).</summary>
    private string? _statsShown, _nowShown, _teamShown;

    private void Refresh()
    {
        Panel.SuspendLayout(); // one layout for everything below, not one per label
        try
        {
            ShowNow();
            ShowStats();
        }
        finally
        {
            Panel.ResumeLayout(true);
        }
    }

    private void ShowStats()
    {
        var totals = Context.Notifier.Stats.Totals(DateOnly.FromDateTime(DateTime.Now));
        var today_ = DateOnly.FromDateTime(DateTime.Now);
        var key = string.Join(";", totals.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.Outcome).Select(kv => $"{kv.Key}={kv.Value}"))
                  + $"|{Context.Notifier.Stats.ActiveDays(today_)}|{today_}|{Context.Current().ToJson().GetHashCode()}";
        if (key == _statsShown)
            return;
        _statsShown = key;
        // "Opened" includes opening Zendesk from a notification about several tickets: the same reaction, no single ticket.
        int Count(NotificationKind kind, StatOutcome outcome) => outcome == StatOutcome.OpenedTicket
            ? totals.GetValueOrDefault((kind, StatOutcome.OpenedTicket)) + totals.GetValueOrDefault((kind, StatOutcome.OpenedZendesk))
            : totals.GetValueOrDefault((kind, outcome));
        int Total(StatOutcome outcome) => Kinds.Sum(k => Count(k.Kind, outcome));

        var shown = Total(StatOutcome.Shown);
        var acted = Total(StatOutcome.Acted);
        _summary.Text = shown == 0
            ? "No ticket notifications in the last 30 days yet."
            : $"{shown} ticket notification{(shown == 1 ? "" : "s")}. You acted on {acted} ({Percent(acted, shown)}), not on " +
              $"{Math.Max(0, shown - acted)}; {Total(StatOutcome.Snoozed)} snoozed, {Total(StatOutcome.Closed)} closed and {Total(StatOutcome.TimedOut)} timed out.";

        List<GridTable.Cell[]> table = [[new("", Theme.Small), .. Columns.Select(c => new GridTable.Cell(c.Label, Theme.Small, GridTable.Ink.Muted))]];
        foreach (var (kind, label) in Kinds)
            table.Add([new(label, Theme.Body), .. Columns.Select(c => Number(Count(kind, c.Outcome), Theme.Body))]);
        table.Add([new("Total", Theme.BodyBold), .. Columns.Select(c => new GridTable.Cell(Total(c.Outcome).ToString(), Theme.BodyBold))]);
        _table.SetRows(table);

        var today = DateOnly.FromDateTime(DateTime.Now);
        ShowInsights(StatsInsights.Analyze(totals, Context.Notifier.Stats.ActiveDays(today), Context.Current(), Context.Notifier.Stats.ClientTotals(today)));
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
        var key = $"{snapshot?.CheckedAt:O}|{WatchStatus.Problem}|{snapshot?.GetHashCode()}";
        if (key == _nowShown)
            return;
        _nowShown = key;
        var settings = Context.Current();
        _nowStatus.Text = (snapshot, WatchStatus.Problem) switch
        {
            (null, { } problem) => problem,
            (null, null) => "Waiting for the first check\u2026",
            (_, { } problem) => $"From the check at {Clock.Time(snapshot.CheckedAt)}. {problem}",
            _ => $"From the last check, at {Clock.Time(snapshot.CheckedAt)}. Click a number to open the list in Zendesk."
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
        var label = Theme.Linked(text, Theme.Body, Theme.TextMuted);
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
        Add(NotificationKind.NewTicket, StatOutcome.Acted, 29);
        Add(NotificationKind.MyTicket, StatOutcome.Acted, 40);
        Add(NotificationKind.CcTicket, StatOutcome.Acted, 3);
        Add(NotificationKind.HelpColleague, StatOutcome.Acted, 3);
        return stats;
    }
}
