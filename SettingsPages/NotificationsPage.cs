using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Notifications: what to be notified about, Help colleagues, snooze and how often to check.</summary>
internal sealed class NotificationsPage : SettingsPage
{
    private static readonly Option<int>[] SnoozeMinuteOptions =
        [new(5, "5 minutes"), new(10, "10 minutes"), new(15, "15 minutes"), new(30, "30 minutes"), new(60, "1 hour")];

    private static readonly Option<SnoozeBehavior>[] SnoozeBehaviorOptions =
    [
        new(SnoozeBehavior.IfStillNeeded, "Only if the ticket still needs attention"),
        new(SnoozeBehavior.Always, "Always")
    ];

    private readonly ToggleSwitch _notifyUnassigned = new("New unassigned tickets in my monitored groups");
    private readonly ToggleSwitch _trackMine = new("Changes on tickets assigned to me (any group)");
    private readonly ToggleSwitch _trackCc = new("Changes on tickets where I'm CC or a follower (any group)");
    private readonly CheckBox _replies = Kit.Check("Public replies");
    private readonly CheckBox _notes = Kit.Check("Internal notes");
    private readonly CheckBox _status = Kit.Check("Status changes");
    private readonly CheckBox _assignment = Kit.Check("Reassignment and group moves");
    private readonly CheckBox _priority = Kit.Check("Priority changes");
    private readonly ComboBox _snoozeMinutes = Kit.Combo(SnoozeMinuteOptions, width: 140);
    private readonly ComboBox _snoozeBehavior = Kit.Combo(SnoozeBehaviorOptions, width: 300);
    private readonly NumericUpDown _interval = new() { Minimum = 30, Maximum = 3600, Increment = 15, Width = 80, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle };
    private readonly PriorityChecks _myPriorities = new(new Padding(50, 0, 0, 6));
    private readonly PriorityChecks _ccPriorities = new(new Padding(50, 0, 0, 6));
    private readonly HelpColleaguesCard _help;

    public NotificationsPage(SettingsContext context, GroupsPage groups)
        : base(context, "Notifications", "Choose what you want to hear about and how often to check.")
    {
        var unassigned = new Card("New tickets", "A notification for each new unassigned ticket in the groups you monitor.", Kit.CardWidth);
        unassigned.Body.Controls.Add(_notifyUnassigned);
        Panel.Controls.Add(unassigned);

        var mine = new Card("Ticket changes", "Get notified when someone else changes these tickets, for the priorities you choose. " +
            "It's the ticket's current priority that counts; tickets without a priority count as Normal.", Kit.CardWidth);
        mine.Body.Controls.Add(_trackMine);
        mine.Body.Controls.Add(_myPriorities.Row);
        mine.Body.Controls.Add(_trackCc);
        mine.Body.Controls.Add(_ccPriorities.Row);
        Panel.Controls.Add(mine);

        // Its own card: the change types apply to both kinds of tickets above, not to one of the switches.
        var which = new Card("Which changes", "Applies to your tickets and to tickets you're CC'd on or follow. Changes you make yourself never trigger a notification.", Kit.CardWidth);
        var options = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 0) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (Kit.CardWidth - 44) / 2));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        CheckBox[] changeTypes = [_replies, _notes, _status, _priority, _assignment];
        for (var i = 0; i < changeTypes.Length; i++)
            options.Controls.Add(changeTypes[i], i % 2, i / 2);
        which.Body.Controls.Add(options);
        Panel.Controls.Add(which);

        _help = new HelpColleaguesCard(context, groups);
        Panel.Controls.Add(_help.Card);

        var snooze = new Card("Snooze", "Snooze on a popup hides it and shows it again later. " +
            "A ticket still needs attention when a new ticket is still unassigned, or another ticket isn't solved or closed. " +
            "While notifications are paused, snoozed popups wait until the pause ends.", Kit.CardWidth);
        var snoozeGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        snoozeGrid.Controls.Add(Theme.Label("Show again after"), 0, 0);
        snoozeGrid.Controls.Add(_snoozeMinutes, 1, 0);
        snoozeGrid.Controls.Add(Theme.Label("Show it"), 0, 1);
        snoozeGrid.Controls.Add(_snoozeBehavior, 1, 1);
        foreach (Control control in snoozeGrid.Controls)
            if (control is Label) control.Anchor = AnchorStyles.Left;
        snooze.Body.Controls.Add(snoozeGrid);
        Panel.Controls.Add(snooze);

        var checking = new Card("Checking", "Zendesk search can lag 1–3 minutes behind, so very short intervals don't help much.", Kit.CardWidth);
        checking.Body.Controls.Add(Kit.Row(Theme.Label("Check Zendesk every"), _interval, Theme.Label("seconds")));
        Theme.WheelScrollsPage(_interval);
        Panel.Controls.Add(checking);

        _trackMine.CheckedChanged += (_, _) => UpdateChangeTypes();
        _trackCc.CheckedChanged += (_, _) => UpdateChangeTypes();
        _myPriorities.Changed += Changed;
        _ccPriorities.Changed += Changed;
        TrackChanges(_notifyUnassigned, _trackMine, _trackCc, _replies, _notes, _status, _assignment, _priority,
            _snoozeMinutes, _snoozeBehavior, _interval);
    }

    public override void Load(Settings settings)
    {
        _notifyUnassigned.Checked = settings.NotifyUnassigned;
        _interval.Value = Math.Clamp(settings.PollIntervalSeconds, 30, 3600);
        _trackMine.Checked = settings.TrackMyTickets;
        _trackCc.Checked = settings.TrackCcTickets;
        _replies.Checked = settings.NotifyPublicReplies;
        _notes.Checked = settings.NotifyInternalNotes;
        _status.Checked = settings.NotifyStatusChanges;
        _assignment.Checked = settings.NotifyAssignmentChanges;
        _priority.Checked = settings.NotifyPriorityChanges;
        _myPriorities.Selected = settings.MyTicketPriorities;
        _ccPriorities.Selected = settings.CcTicketPriorities;
        _help.Load(settings);
        Kit.Select(_snoozeMinutes, settings.SnoozeMinutes, fallback: 10);
        Kit.Select(_snoozeBehavior, settings.SnoozeBehavior);
        UpdateChangeTypes();
    }

    public override void Apply(Settings s)
    {
        s.NotifyUnassigned = _notifyUnassigned.Checked;
        s.PollIntervalSeconds = (int)_interval.Value;
        s.TrackMyTickets = _trackMine.Checked;
        s.TrackCcTickets = _trackCc.Checked;
        s.NotifyPublicReplies = _replies.Checked;
        s.NotifyInternalNotes = _notes.Checked;
        s.NotifyStatusChanges = _status.Checked;
        s.NotifyAssignmentChanges = _assignment.Checked;
        s.NotifyPriorityChanges = _priority.Checked;
        s.MyTicketPriorities = _myPriorities.Selected;
        s.CcTicketPriorities = _ccPriorities.Selected;
        s.SnoozeMinutes = Kit.Selected<int>(_snoozeMinutes);
        s.SnoozeBehavior = Kit.Selected<SnoozeBehavior>(_snoozeBehavior);
        _help.Apply(s);
    }

    /// <summary>Monitored groups may have changed on the Groups page.</summary>
    public override void OnShowing() => _help.ShowGroups();

    /// <summary>For the screenshot tool.</summary>
    internal HelpColleaguesCard Help => _help;

    private void UpdateChangeTypes()
    {
        foreach (var box in new[] { _replies, _notes, _status, _assignment, _priority })
            box.Enabled = _trackMine.Checked || _trackCc.Checked;
        _myPriorities.Enabled = _trackMine.Checked;
        _ccPriorities.Enabled = _trackCc.Checked;
    }
}
