using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Notifications: what to be notified about, and how often to check.</summary>
internal sealed class NotificationsPage : SettingsPage
{
    /// <summary>Under New tickets: whether My products filters right now, with a link to the Products page.</summary>
    private readonly Control _productsHint = Theme.Label(ProductsHint(0), Theme.Small, Theme.TextMuted);

    private readonly ToggleSwitch _notifyUnassigned = new("New unassigned tickets in my monitored groups");
    private readonly ToggleSwitch _trackMine = new("Changes on tickets assigned to me (any group)");
    private readonly ToggleSwitch _trackCc = new("Changes on tickets where I'm CC or a follower (any group)");
    private readonly CheckBox _replies = Kit.Check("Public replies");
    private readonly CheckBox _notes = Kit.Check("Internal notes");
    private readonly CheckBox _status = Kit.Check("Status changes");
    private readonly CheckBox _assignment = Kit.Check("Reassignment and group moves");
    private readonly CheckBox _priority = Kit.Check("Priority changes");
    private readonly CheckBox _automatic = Kit.Check("Automatic updates (bots)");
    private readonly ToggleSwitch _onlyMyHours = new("Only notify me during my working hours");
    private readonly CheckBox _workingLate = Kit.Check("Also outside them, while I'm active in Zendesk");
    private readonly ToggleSwitch _catchUpAtStart = new("Catch up when my working day starts");
    private readonly DayChecks _myDays = new("", new Padding(0));
    private readonly HourRange _myHours = new() { Margin = new Padding(0, 4, 0, 6) };
    private readonly PriorityChoice _myPriorities = new(new Padding(50, 0, 0, 6));
    private readonly PriorityChoice _ccPriorities = new(new Padding(50, 0, 0, 6));

    /// <summary>Under "New unassigned tickets": each monitored group with its priorities (rebuilt when the page shows,
    /// as groups may have been ticked on the Groups page since).</summary>
    private readonly TableLayoutPanel _groupPriorities = new()
    {
        ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(50, 0, 0, 6)
    };
    private readonly GroupsPage _groups;

    /// <summary>The priorities that notify even when someone took the ticket before the app saw it unassigned.</summary>
    private static readonly (string Label, string[] Priorities)[] TakenQuicklyChoices =
        [("Off", []), ("P1", ["urgent"]), ("P1, P2", ["urgent", "high"]), ("All", Settings.AllPriorities)];
    private readonly ChoicePills _takenQuickly = new([.. TakenQuicklyChoices.Select(c => c.Label)]) { AccessibleName = "Also when someone took it before I saw it" };

    /// <summary>Groups with fewer priorities than all, as chosen here (also for groups not shown right now).</summary>
    private Dictionary<long, List<string>> _newPriorities = [];

    public NotificationsPage(SettingsContext context, GroupsPage groups)
        : base(context, "Notifications", "Choose what you want to hear about.")
    {
        _groups = groups;
        var unassigned = new Card("New tickets", "A notification for each new unassigned ticket in the groups you monitor, for the " +
            "priorities you choose per group.", Kit.CardWidth);
        unassigned.Body.Controls.Add(_notifyUnassigned);
        unassigned.Body.Controls.Add(_groupPriorities);
        // Tickets someone took between two checks (never seen unassigned): off for most, P1 for managers.
        var takenLabel = Theme.Label("Also when someone took it before I saw it");
        takenLabel.Margin = new Padding(50, 8, 0, 0);
        _takenQuickly.Margin = new Padding(50, 4, 0, 6);
        unassigned.Body.Controls.Add(takenLabel);
        unassigned.Body.Controls.Add(_takenQuickly);
        _productsHint.Margin = new Padding(50, 4, 0, 6);
        unassigned.Body.Controls.Add(_productsHint);
        Panel.Controls.Add(unassigned);

        var mine = new Card("Ticket changes", "When someone else changes these tickets, for the priorities you choose.", Kit.CardWidth);
        mine.Body.Controls.Add(_trackMine);
        mine.Body.Controls.Add(_myPriorities.Row);
        mine.Body.Controls.Add(_trackCc);
        mine.Body.Controls.Add(_ccPriorities.Row);
        Panel.Controls.Add(mine);

        // Its own card: the change types apply to both kinds of tickets above, not to one of the switches.
        var which = new Card("Which changes", "For your own and CC'd tickets. Your own changes never notify.", Kit.CardWidth);
        var options = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (Kit.CardWidth - 44) / 2));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        CheckBox[] changeTypes = [_replies, _notes, _status, _priority, _assignment, _automatic];
        for (var i = 0; i < changeTypes.Length; i++)
            options.Controls.Add(changeTypes[i], i % 2, i / 2);
        which.Body.Controls.Add(options);
        Panel.Controls.Add(which);

        // The hours are used by two switches: this one, and "only while I'm working myself" on the Colleagues page.
        var working = new Card("Working hours", "When you work (this PC's time). Outside these hours notifications can stay quiet, and Catch up shows what you " +
            "missed when your day starts.", Kit.CardWidth);
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        grid.Controls.Add(Theme.Label("My hours"), 0, 0);
        grid.Controls.Add(_myHours, 1, 0);
        grid.Controls.Add(Theme.Label("My days"), 0, 1);
        grid.Controls.Add(_myDays.Row, 1, 1);
        foreach (Control control in grid.Controls)
            if (control is Label) control.Anchor = AnchorStyles.Left;
        working.Body.Controls.Add(grid);
        _onlyMyHours.Margin = new Padding(0, 6, 0, 0);
        working.Body.Controls.Add(_onlyMyHours);
        _workingLate.Margin = new Padding(50, 0, 0, 6); // under the switch, like the priorities under theirs
        working.Body.Controls.Add(_workingLate);
        working.Body.Controls.Add(_catchUpAtStart);
        Panel.Controls.Add(working);


        _trackMine.CheckedChanged += (_, _) => UpdateChangeTypes();
        _trackCc.CheckedChanged += (_, _) => UpdateChangeTypes();
        _notifyUnassigned.CheckedChanged += (_, _) => UpdateChangeTypes();
        _takenQuickly.SelectedIndexChanged += (_, _) => Changed();
        _myPriorities.Changed += Changed;
        _ccPriorities.Changed += Changed;
        _myDays.Changed += Changed;
        TrackChanges(_notifyUnassigned, _trackMine, _trackCc, _replies, _notes, _status, _assignment, _priority, _automatic, _onlyMyHours, _workingLate, _catchUpAtStart);
        _onlyMyHours.CheckedChanged += (_, _) => _workingLate.Enabled = _onlyMyHours.Checked;
        _myHours.RangeChanged += (_, _) => Changed();
    }

    /// <summary>"All products notify" until some are picked (Products page), so it never sounds like a filter that isn't on.</summary>
    internal static string ProductsHint(int picked) => picked == 0
        ? "All products notify. Pick yours on the Products page."
        : $"Only your products notify ({picked} picked). Change it on the Products page.";

    public override void Load(Settings settings)
    {
        _productsHint.Text = ProductsHint(settings.MyProducts.Count);
        _notifyUnassigned.Checked = settings.NotifyUnassigned;
        // A value from an older version or an edited file (e.g. 75 s) gets the nearest step.
        _trackMine.Checked = settings.TrackMyTickets;
        _trackCc.Checked = settings.TrackCcTickets;
        _replies.Checked = settings.NotifyPublicReplies;
        _notes.Checked = settings.NotifyInternalNotes;
        _status.Checked = settings.NotifyStatusChanges;
        _assignment.Checked = settings.NotifyAssignmentChanges;
        _priority.Checked = settings.NotifyPriorityChanges;
        _automatic.Checked = settings.NotifyAutomaticChanges;
        _newPriorities = settings.NewTicketPriorities.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        // By the lowest priority in the list, like the other priority choices (none: Off).
        var taken = settings.TakenQuicklyPriorities;
        _takenQuickly.SelectedIndex = taken.Count == 0 ? 0 : taken.Contains("low") || taken.Contains("normal") ? 3 : taken.Contains("high") ? 2 : 1;
        ShowGroupPriorities();
        _myPriorities.Selected = settings.MyTicketPriorities;
        _ccPriorities.Selected = settings.CcTicketPriorities;
        _onlyMyHours.Checked = settings.OnlyNotifyDuringMyHours;
        _workingLate.Checked = settings.NotifyWhenWorkingLate;
        _workingLate.Enabled = _onlyMyHours.Checked;
        _catchUpAtStart.Checked = settings.CatchUpAtDayStart;
        _myHours.SetRange(settings.MyWorkdayStartHour, settings.MyWorkdayEndHour);
        _myDays.Selected = settings.MyWorkDays;
        UpdateChangeTypes();
    }

    public override void Apply(Settings s)
    {
        s.NotifyUnassigned = _notifyUnassigned.Checked;
        s.TrackMyTickets = _trackMine.Checked;
        s.TrackCcTickets = _trackCc.Checked;
        s.NotifyPublicReplies = _replies.Checked;
        s.NotifyInternalNotes = _notes.Checked;
        s.NotifyStatusChanges = _status.Checked;
        s.NotifyAssignmentChanges = _assignment.Checked;
        s.NotifyPriorityChanges = _priority.Checked;
        s.NotifyAutomaticChanges = _automatic.Checked;
        // Only the monitored groups' choices are kept (one unticked and ticked again starts at all priorities).
        var monitored = _groups.MonitoredGroups().Select(g => g.Id).ToHashSet();
        s.NewTicketPriorities = _newPriorities.Where(kv => monitored.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        s.TakenQuicklyPriorities = [.. TakenQuicklyChoices[_takenQuickly.SelectedIndex].Priorities];
        s.MyTicketPriorities = _myPriorities.Selected;
        s.CcTicketPriorities = _ccPriorities.Selected;
        s.OnlyNotifyDuringMyHours = _onlyMyHours.Checked;
        s.NotifyWhenWorkingLate = _workingLate.Checked;
        s.CatchUpAtDayStart = _catchUpAtStart.Checked;
        s.MyWorkdayStartHour = _myHours.Start;
        s.MyWorkdayEndHour = _myHours.End;
        s.MyWorkDays = _myDays.Selected;
    }


    public override void OnShowing()
    {
        ShowGroupPriorities();
        _productsHint.Text = ProductsHint(Context.Current().MyProducts.Count); // the Products page may have changed (not saved yet)
    }

    /// <summary>The rows under "New unassigned tickets": a group and its priorities.</summary>
    private readonly List<(long GroupId, PriorityChoice Choice)> _groupChoices = [];

    /// <summary>One row per monitored group: its name and All, P1–P3, P1 and P2, or P1 only.</summary>
    private void ShowGroupPriorities()
    {
        var groups = _groups.MonitoredGroups();
        if (_groupChoices.Count > 0 && _groupChoices.Select(c => c.GroupId).SequenceEqual(groups.Select(g => g.Id)))
        {
            foreach (var (id, choice) in _groupChoices)
                choice.Selected = [.. _newPriorities.GetValueOrDefault(id) ?? [.. Settings.AllPriorities]]; // Discard: the saved choices again
            return;
        }

        _groupPriorities.SuspendLayout();
        foreach (Control old in _groupPriorities.Controls.Cast<Control>().ToList())
            old.Dispose();
        _groupPriorities.Controls.Clear();
        _groupChoices.Clear();
        if (groups.Count == 0)
            _groupPriorities.Controls.Add(Theme.Label("Tick groups on the Groups page first.", Theme.Small, Theme.TextMuted), 0, 0);
        for (var row = 0; row < groups.Count; row++)
        {
            var group = groups[row];
            var name = Theme.Label(group.Name);
            name.Anchor = AnchorStyles.Left;
            name.Margin = new Padding(0, 0, 16, 0);
            var choice = new PriorityChoice(Padding.Empty, label: false)
            {
                Selected = [.. _newPriorities.GetValueOrDefault(group.Id) ?? [.. Settings.AllPriorities]],
                Enabled = _notifyUnassigned.Checked
            };
            choice.Changed += () =>
            {
                var chosen = choice.Selected;
                if (chosen.Count == Settings.AllPriorities.Length) _newPriorities.Remove(group.Id); else _newPriorities[group.Id] = chosen;
                Changed();
            };
            _groupPriorities.Controls.Add(name, 0, row);
            _groupPriorities.Controls.Add(choice.Row, 1, row);
            _groupChoices.Add((group.Id, choice));
        }
        _groupPriorities.ResumeLayout();
    }

    private void UpdateChangeTypes()
    {
        _takenQuickly.Enabled = _notifyUnassigned.Checked;
        foreach (var (_, choice) in _groupChoices)
            choice.Enabled = _notifyUnassigned.Checked;
        foreach (var box in new[] { _replies, _notes, _status, _assignment, _priority, _automatic })
            box.Enabled = _trackMine.Checked || _trackCc.Checked;
        _myPriorities.Enabled = _trackMine.Checked;
        _ccPriorities.Enabled = _trackCc.Checked;
    }
}
