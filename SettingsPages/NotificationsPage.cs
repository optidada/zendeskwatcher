using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Notifications: what to be notified about, and how often to check.</summary>
internal sealed class NotificationsPage : SettingsPage
{
    private readonly ToggleSwitch _notifyUnassigned = new("New unassigned tickets in my monitored groups");
    private readonly ToggleSwitch _trackMine = new("Changes on tickets assigned to me (any group)");
    private readonly ToggleSwitch _trackCc = new("Changes on tickets where I'm CC or a follower (any group)");
    private readonly CheckBox _replies = Kit.Check("Public replies");
    private readonly CheckBox _notes = Kit.Check("Internal notes");
    private readonly CheckBox _status = Kit.Check("Status changes");
    private readonly CheckBox _assignment = Kit.Check("Reassignment and group moves");
    private readonly CheckBox _priority = Kit.Check("Priority changes");
    private readonly ToggleSwitch _onlyMyHours = new("Only notify me while I'm working");
    private readonly ToggleSwitch _catchUpAtStart = new("Catch up when my working day starts");
    private readonly DayChecks _myDays = new("", new Padding(0));
    private readonly ComboBox _myStart = Kit.HourCombo(0, 23);
    private readonly ComboBox _myEnd = Kit.HourCombo(1, 24);
    private static readonly Option<int>[] IntervalOptions =
        [new(30, "30 s"), new(60, "1 min"), new(120, "2 min"), new(180, "3 min"), new(300, "5 min")];

    private readonly ChoicePills _interval = new(IntervalOptions.Select(o => o.Label).ToArray());
    private readonly PriorityChecks _myPriorities = new(new Padding(50, 0, 0, 6));
    private readonly PriorityChecks _ccPriorities = new(new Padding(50, 0, 0, 6));

    public NotificationsPage(SettingsContext context)
        : base(context, "Notifications", "Choose what you want to hear about and how often to check.")
    {
        var unassigned = new Card("New tickets", "A notification for each new unassigned ticket in the groups you monitor.", Kit.CardWidth);
        unassigned.Body.Controls.Add(_notifyUnassigned);
        Panel.Controls.Add(unassigned);

        var mine = new Card("Ticket changes", "Get notified when someone else changes these tickets, for the priorities you choose. " +
            "It's the ticket's current priority that counts; tickets without a priority count as Normal (P3).", Kit.CardWidth);
        mine.Body.Controls.Add(_trackMine);
        mine.Body.Controls.Add(_myPriorities.Row);
        mine.Body.Controls.Add(_trackCc);
        mine.Body.Controls.Add(_ccPriorities.Row);
        Panel.Controls.Add(mine);

        // Its own card: the change types apply to both kinds of tickets above, not to one of the switches.
        var which = new Card("Which changes", "Applies to your tickets and to tickets you're CC'd on or follow. Changes you make yourself never trigger a notification. " +
            "Status changes include your own statuses (e.g. In progress → Escalated), but not New → Open or Solved → Closed.", Kit.CardWidth);
        var options = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (Kit.CardWidth - 44) / 2));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        CheckBox[] changeTypes = [_replies, _notes, _status, _priority, _assignment];
        for (var i = 0; i < changeTypes.Length; i++)
            options.Controls.Add(changeTypes[i], i % 2, i / 2);
        which.Body.Controls.Add(options);
        Panel.Controls.Add(which);

        // The hours are used by two switches: this one, and "only while I'm working myself" on the Colleagues page.
        var working = new Card("Working hours", "When you're working, in this PC's time. Only notify: new tickets and changes on " +
            "your tickets are skipped outside your hours (colleagues' tickets have their own switch on the Colleagues page). " +
            "Catch up: when your day starts, you get what came in while you were off and still needs you: tickets still " +
            "unassigned, customers still waiting (also from the tray menu).", Kit.CardWidth);
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        grid.Controls.Add(Theme.Label("My hours"), 0, 0);
        grid.Controls.Add(Kit.Row(_myStart, Theme.Label("to"), _myEnd), 1, 0);
        grid.Controls.Add(Theme.Label("My days"), 0, 1);
        grid.Controls.Add(_myDays.Row, 1, 1);
        foreach (Control control in grid.Controls)
            if (control is Label) control.Anchor = AnchorStyles.Left;
        working.Body.Controls.Add(grid);
        _onlyMyHours.Margin = new Padding(0, 6, 0, 0);
        working.Body.Controls.Add(_onlyMyHours);
        working.Body.Controls.Add(_catchUpAtStart);
        Panel.Controls.Add(working);

        var checking = new Card("How often to check", "Zendesk search can lag 1–3 minutes behind, so very short intervals don't help much.", Kit.CardWidth);
        checking.Body.Controls.Add(Theme.Label("Check Zendesk every"));
        _interval.Margin = new Padding(0, 4, 0, 2);
        checking.Body.Controls.Add(_interval);
        Panel.Controls.Add(checking);

        _trackMine.CheckedChanged += (_, _) => UpdateChangeTypes();
        _trackCc.CheckedChanged += (_, _) => UpdateChangeTypes();
        _myPriorities.Changed += Changed;
        _ccPriorities.Changed += Changed;
        _myDays.Changed += Changed;
        TrackChanges(_notifyUnassigned, _trackMine, _trackCc, _replies, _notes, _status, _assignment, _priority, _onlyMyHours, _catchUpAtStart);
        _interval.SelectedIndexChanged += (_, _) => Changed();
        foreach (var combo in new[] { _myStart, _myEnd })
            combo.SelectedIndexChanged += (_, _) => Changed();
    }

    public override void Load(Settings settings)
    {
        _notifyUnassigned.Checked = settings.NotifyUnassigned;
        // A value from an older version or an edited file (e.g. 75 s) gets the nearest step.
        _interval.SelectedIndex = Array.IndexOf(IntervalOptions, IntervalOptions.MinBy(o => Math.Abs(o.Value - settings.PollIntervalSeconds)));
        _trackMine.Checked = settings.TrackMyTickets;
        _trackCc.Checked = settings.TrackCcTickets;
        _replies.Checked = settings.NotifyPublicReplies;
        _notes.Checked = settings.NotifyInternalNotes;
        _status.Checked = settings.NotifyStatusChanges;
        _assignment.Checked = settings.NotifyAssignmentChanges;
        _priority.Checked = settings.NotifyPriorityChanges;
        _myPriorities.Selected = settings.MyTicketPriorities;
        _ccPriorities.Selected = settings.CcTicketPriorities;
        _onlyMyHours.Checked = settings.OnlyNotifyDuringMyHours;
        _catchUpAtStart.Checked = settings.CatchUpAtDayStart;
        _myStart.SelectedIndex = Math.Clamp(settings.MyWorkdayStartHour, 0, 23);
        _myEnd.SelectedIndex = Math.Clamp(settings.MyWorkdayEndHour, 1, 24) - 1;
        _myDays.Selected = settings.MyWorkDays;
        UpdateChangeTypes();
    }

    public override void Apply(Settings s)
    {
        s.NotifyUnassigned = _notifyUnassigned.Checked;
        s.PollIntervalSeconds = IntervalOptions[_interval.SelectedIndex].Value;
        s.TrackMyTickets = _trackMine.Checked;
        s.TrackCcTickets = _trackCc.Checked;
        s.NotifyPublicReplies = _replies.Checked;
        s.NotifyInternalNotes = _notes.Checked;
        s.NotifyStatusChanges = _status.Checked;
        s.NotifyAssignmentChanges = _assignment.Checked;
        s.NotifyPriorityChanges = _priority.Checked;
        s.MyTicketPriorities = _myPriorities.Selected;
        s.CcTicketPriorities = _ccPriorities.Selected;
        s.OnlyNotifyDuringMyHours = _onlyMyHours.Checked;
        s.CatchUpAtDayStart = _catchUpAtStart.Checked;
        s.MyWorkdayStartHour = _myStart.SelectedIndex;
        s.MyWorkdayEndHour = Math.Max(_myEnd.SelectedIndex + 1, s.MyWorkdayStartHour + 1);
        s.MyWorkDays = _myDays.Selected;
    }


    private void UpdateChangeTypes()
    {
        foreach (var box in new[] { _replies, _notes, _status, _assignment, _priority })
            box.Enabled = _trackMine.Checked || _trackCc.Checked;
        _myPriorities.Enabled = _trackMine.Checked;
        _ccPriorities.Enabled = _trackCc.Checked;
    }
}
