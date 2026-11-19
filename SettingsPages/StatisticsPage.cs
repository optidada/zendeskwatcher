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
    private readonly Label _teamStatus = Theme.Linked("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
    private readonly GridTable _team = new() { Margin = new Padding(0, 2, 0, 0), ShrinkColumn = 2 }; // the summary gives way, not the names

    /// <summary>How often the group's tickets are read again, at most (only while Insights is shown).</summary>
    private static readonly TimeSpan TeamRefresh = TimeSpan.FromMinutes(10);

    /// <summary>A group "Who could take it" read this recently is used as it is (<see cref="GroupWorkloadCache"/>).</summary>
    private static readonly TimeSpan SharedDataAge = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Team workload as last read for one choice of groups, kept in cache\team-workload.json (one per choice) so Insights shows it
    /// right away, also after switching groups or a restart (names, time zones and counts only): for which account, person
    /// and groups (Key), when, the rows, and each person's time zone.
    /// </summary>
    private sealed class SavedTeam
    {
        public string Key { get; set; } = "";
        public DateTime At { get; set; }
        public List<WorkloadRow> Rows { get; set; } = [];
        public Dictionary<long, string?> Zones { get; set; } = [];
    }

    /// <summary>Key → the team as last read; at most <see cref="TeamsKept"/>, the oldest go.</summary>
    private static Dictionary<string, SavedTeam>? _teams;
    private const int TeamsKept = 8;

    private const string TeamCacheName = "team-workload", TeamChoiceCacheName = "team-group";

    /// <summary>
    /// The group Team workload shows (0: all monitored groups) and the order; remembered on this PC (cache\team-group.json)
    /// right away, not a setting to save.
    /// </summary>
    private static long? _teamGroup;
    private static bool _busiestFirst = true;

    private readonly ChoiceBox _teamGroups = new() { Width = 190, Visible = false };
    private bool _fillingTeamGroups;
    private int _teamVersion;
    private readonly TableLayoutPanel _now = new() { ColumnCount = 3, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 4, 0, 0) };
    private readonly ToolTip _tips = new();
    private readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, Kit.CardWidth - 44);
    private readonly GridTable _table = new() { Margin = new Padding(0, 8, 0, 8) };
    private readonly Button _clear = Theme.SecondaryButton("Clear statistics");
    private readonly TableLayoutPanel _insights = new() { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty };

    public StatisticsPage(SettingsContext context)
        : base(context, "Insights", "Your tickets, your team's workload, how you used notifications, and tips. Counted on this PC only.")
    {
        var now = new Card("Your tickets", null, Kit.CardWidth);
        now.Body.Controls.Add(_nowStatus);
        now.Body.Controls.Add(_now);
        Panel.Controls.Add(now);

        var workload = new Card("Team workload", null, Kit.CardWidth);
        _teamGroups.Margin = new Padding(0, 0, 0, 4);
        workload.Body.Controls.Add(_teamGroups);
        _teamGroups.SelectedIndexChanged += (_, _) =>
        {
            if (_fillingTeamGroups || _teamGroups.SelectedItem is not Option<long> chosen)
                return;
            _teamGroup = chosen.Value;
            SaveTeamChoice();
            RefreshTeam();
        };
        workload.Body.Controls.Add(_teamStatus);
        workload.Body.Controls.Add(_team);
        Panel.Controls.Add(workload);
        // Updated after every check while the window is open.
        WatchStatus.Changed += ShowNow;
        Panel.Disposed += (_, _) => WatchStatus.Changed -= ShowNow;

        var card = new Card("Notification usage · 30 days", null, Kit.CardWidth);
        card.Body.Controls.Add(_summary);
        card.Body.Controls.Add(_table);
        card.Body.Controls.Add(Theme.Label(
            "Acted on: opened, taken or assigned, copied (also just the number) or snoozed.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
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

    // ------------------------------------------------------------------ who's busy

    /// <summary>
    /// Everyone in the chosen group (or all monitored groups) who is in working hours, and how busy they are with tickets in
    /// those groups. Read when Insights is shown, at most every 10 minutes; the last result for these groups is shown
    /// meanwhile (also after a restart: cache\team-workload.json).
    /// </summary>
    private async void RefreshTeam()
    {
        var version = ++_teamVersion;
        var settings = Context.Current();
        var teams = LoadSavedTeams(settings.Subdomain);
        var monitored = settings.WatchedGroups.Where(g => !g.IsNoGroup).ToList();
        ShowTeamChoice(monitored);
        var chosen = monitored.FirstOrDefault(g => g.Id == _teamGroup);
        var groups = (chosen is null ? monitored : [chosen]).Select(g => g.Id).Order().ToList();
        var groupsKey = string.Join(',', groups);
        if (Context.Client is not { } client || Context.Me is not { } me)
        {
            var last = teams.Values.Where(t => t.Key.StartsWith(settings.Subdomain + "|") && t.Key.EndsWith("|" + groupsKey)).MaxBy(t => t.At);
            ShowTeam(last, "Sign in to see the workload.");
            return;
        }
        if (groups.Count == 0)
        {
            ShowTeam(null, "Choose groups to monitor (Groups page) to see the workload.");
            return;
        }
        var key = $"{settings.Subdomain}|{me.Id}|{groupsKey}";
        if (teams.TryGetValue(key, out var cached))
        {
            ShowTeam(cached, null); // right away, also while it's read again
            if (DateTime.UtcNow - cached.At < TeamRefresh)
                return;
        }
        else
            ShowTeam(null, "Loading… (a big group can take a few seconds the first time)");
        try
        {
            var watch = Stopwatch.StartNew();
            // Shared with "Who could take it": a group it read in the last few minutes isn't read again.
            var read_ = await Task.WhenAll(groups.Select(id =>
                GroupWorkloadCache.GetAsync(client, settings.Subdomain, id, SharedDataAge, CancellationToken.None)));
            var members = read_.SelectMany(g => g.Members).DistinctBy(u => u.Id).ToList();
            var tickets = read_.Select(g => g.Tickets).ToList();
            var team = members.ToDictionary(u => u.Id, u => u.Id == me.Id ? "You" : u.Name ?? $"User {u.Id}");
            var statuses = (await client.GetCustomStatusesAsync(CancellationToken.None))
                .Where(s => s.AgentLabel is { Length: > 0 }).ToDictionary(s => s.Id, s => s.AgentLabel!);
            var read = new SavedTeam
            {
                Key = key, At = read_.Min(g => g.At), Rows = TeamWorkload.Count(tickets.SelectMany(t => t), team, me.Id, statuses),
                Zones = members.ToDictionary(u => u.Id, u => TimeRegions.ZoneOf(u))
            };
            Log.Info($"Insights: team workload read in {watch.Elapsed.TotalSeconds:0.0} s ({groups.Count} group(s), " +
                $"{tickets.Sum(t => t.Count)} open tickets, {members.Count} people)");
            teams[key] = read;
            foreach (var old in teams.Values.OrderByDescending(t => t.At).Skip(TeamsKept).ToList())
                teams.Remove(old.Key);
            ListCache.Save(TeamCacheName, settings.Subdomain, read.At, teams);
            if (version == _teamVersion && !Panel.IsDisposed)
                ShowTeam(read, null);
        }
        catch (Exception ex)
        {
            if (ex is not AuthRequiredException)
                Log.Error("Insights: could not read the team workload", ex);
            if (version == _teamVersion && !Panel.IsDisposed)
                ShowTeam(teams.GetValueOrDefault(key), "Couldn't read the group's tickets just now.");
        }
    }

    /// <summary>Once per run: the teams, the chosen group and the order as last saved on this PC (for this Zendesk).</summary>
    private static Dictionary<string, SavedTeam> LoadSavedTeams(string subdomain)
    {
        if (_teamGroup is null)
        {
            // [group, order]: order 1 = least busy first.
            var choice = ListCache.Load<List<long>>(TeamChoiceCacheName, subdomain, anyAge: true)?.Value ?? [];
            _teamGroup = choice.ElementAtOrDefault(0);
            _busiestFirst = choice.ElementAtOrDefault(1) == 0;
        }
        return _teams ??= ListCache.Load<Dictionary<string, SavedTeam>>(TeamCacheName, subdomain, anyAge: true)?.Value ?? [];
    }

    private void SaveTeamChoice() =>
        ListCache.Save(TeamChoiceCacheName, Context.Current().Subdomain, DateTime.UtcNow, new List<long> { _teamGroup ?? 0, _busiestFirst ? 0 : 1 });

    /// <summary>The drop-down: All monitored groups, then each one (hidden with a single group).</summary>
    private void ShowTeamChoice(List<WatchedGroup> monitored)
    {
        Option<long>[] options = [new(0, "All monitored groups"), .. monitored.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new Option<long>(g.Id, g.Name))];
        _teamGroups.Visible = monitored.Count > 1;
        _fillingTeamGroups = true;
        if (!_teamGroups.Items.Cast<Option<long>>().SequenceEqual(options))
        {
            _teamGroups.Items.Clear();
            _teamGroups.Items.AddRange(options);
        }
        Kit.Select(_teamGroups, monitored.Any(g => g.Id == _teamGroup) ? _teamGroup!.Value : 0L);
        _fillingTeamGroups = false;
    }

    /// <summary>For the screenshot tool: a sample group, as if just read.</summary>
    internal static void SetTeamSample(string subdomain, long meId, IEnumerable<long> groups, List<WorkloadRow> rows, Dictionary<long, string?> zones)
    {
        _teamGroup = 0;
        _busiestFirst = true;
        var key = $"{subdomain}|{meId}|{string.Join(',', groups.Order())}";
        _teams = new() { [key] = new SavedTeam { Key = key, At = DateTime.UtcNow, Rows = rows, Zones = zones } };
    }

    private void ShowTeam(SavedTeam? team, string? message)
    {
        var settings = Context.Current();
        var now = DateTimeOffset.UtcNow;
        bool OffWork(long id) => TeamWorkload.OffWork(new User { IanaTimeZone = team?.Zones.GetValueOrDefault(id) }, now,
            settings.WorkdayStartHour, settings.WorkdayEndHour, settings.WorkDays) == true;
        // Off work: not shown (you are, greyed); nobody can be asked then anyway.
        var rows = team is null ? null : TeamWorkload.Order(team.Rows.Where(r => r.IsMe || !OffWork(r.AssigneeId)), OffWork, _busiestFirst);
        var hidden = team is null ? 0 : team.Rows.Count(r => !r.IsMe && OffWork(r.AssigneeId));
        var key = $"{team?.Key}|{team?.At:O}|{message}|{_busiestFirst}|{(rows is null ? "" : string.Join(',', rows.Select(r => r.AssigneeId)))}";
        if (key == _teamShown)
            return;
        _teamShown = key;
        string Updated(DateTime read) => read.ToLocalTime() is var local && local.Date == DateTime.Today ? Clock.Time(local) : Clock.DayTime(local);
        var offNote = hidden == 0 ? "" : $"{hidden} off work not shown (Notifications → Colleagues). ";
        _teamStatus.Text = message ?? (team is null ? ""
            : rows!.Any(r => !r.IsMe)
                ? $"Open tickets in {(_teamGroups.Visible && _teamGroups.SelectedItem is Option<long> { Value: > 0 } g ? g.Label : "your monitored groups")}. " +
                  $"{offNote}Updated {Updated(team.At)}."
                : $"Nobody else is in working hours right now. {offNote}Updated {Updated(team.At)}.");
        _team.Visible = rows is { Count: > 0 };
        if (rows is not { Count: > 0 })
            return;
        var most = Math.Max(1, rows.Max(TeamWorkload.Score));
        var baseUrl = settings.BaseUrl;
        var shortNames = TeamWorkload.ShortNames(rows.Select(r => r.Name));
        // Headings; Workload sorts (▼ busiest first, ▲ least busy first), like a column heading in Zendesk or Outlook.
        var table = new List<GridTable.Cell[]>
        {
            new GridTable.Cell[]
            {
                new("Name", Theme.Small, GridTable.Ink.Muted),
                new(_busiestFirst ? "Workload ▼" : "Workload ▲", Theme.Small, GridTable.Ink.Muted,
                    _busiestFirst ? "Busiest first. Click for least busy first." : "Least busy first. Click for busiest first.",
                    () =>
                    {
                        _busiestFirst = !_busiestFirst;
                        SaveTeamChoice();
                        RefreshTeam();
                    }, Left: true),
                new("", Theme.Small, GridTable.Ink.Muted, Left: true)
            }
        };
        foreach (var row in rows)
        {
            var off = OffWork(row.AssigneeId);
            var ink = off ? GridTable.Ink.Muted : GridTable.Ink.Text;
            var query = $"assignee:{row.AssigneeId} status<solved";
            table.Add(
            [
                new GridTable.Cell(shortNames[row.Name], row.IsMe ? Theme.BodyBold : Theme.Body, off ? GridTable.Ink.Muted : GridTable.Ink.Link,
                    $"{(row.IsMe ? "Your" : $"{row.Name}'s")} unsolved tickets – opens in Zendesk", () => OpenSearch(baseUrl, query)),
                new GridTable.Cell("", Theme.Body, ink, Bar: TeamWorkload.Score(row) / (float)most, Left: true),
                new GridTable.Cell(off ? $"off work · {TeamWorkload.Summary(row)}" : TeamWorkload.Summary(row), Theme.Small, ink, Left: true)
            ]);
        }
        _team.MaxWidth = TableRoom(_team);
        _team.SetRows(table);
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
        var insights = StatsInsights.Analyze(totals, Context.Notifier.Stats.ActiveDays(today), Context.Current(), Context.Notifier.Stats.ClientTotals(today));
        if (Context.Notifier.Stats.HeldBack(today) is > 0 and var heldBack)
            insights.Add(new StatsInsights.Insight(StatsInsights.Tone.Hint,
                $"{heldBack} new ticket{(heldBack == 1 ? " was" : "s were")} for other products and didn't notify: My products (Groups \u2192 Products)."));
        ShowInsights(insights);
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
                // Not notified for another product (My products): not in the count above, but a click shows them.
                if (snapshot.OtherProducts > 0 && Products.Field is { } field)
                {
                    var exclude = string.Concat(settings.MyProducts.Select(p => $" -custom_field_{field.Id}:{p}"));
                    var other = new List<Control>();
                    if (groups.Count > 1)
                        foreach (var g in groups.Where(g => g.OtherProducts > 0))
                        {
                            if (other.Count > 0)
                                other.Add(Muted("\u00b7"));
                            other.Add(Link($"{TicketWatcher.GroupLabel(g.Group)}: {g.OtherProducts}", UnassignedQuery(g.Group) + exclude,
                                $"Unassigned tickets for other products in {TicketWatcher.GroupLabel(g.Group)}"));
                        }
                    other.Add(Muted(groups.Count > 1 ? "(not notified)" : "not notified (My products)"));
                    var count = groups.Count(g => g.OtherProducts > 0) == 1
                        ? Link(snapshot.OtherProducts.ToString(), UnassignedQuery(groups.First(g => g.OtherProducts > 0).Group) + exclude,
                            "Unassigned tickets for other products", bold: true)
                        : Cell(snapshot.OtherProducts.ToString(), Theme.BodyBold, Theme.Text);
                    Row("For other products", count, [.. other]);
                }
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
