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
    private readonly CheckBox _automatic = Kit.Check("Automatic updates (bots)");
    private readonly ToggleSwitch _onlyMyHours = new("Only notify me during my working hours");
    private readonly CheckBox _workingLate = Kit.Check("Also outside them, while I'm active in Zendesk");
    private readonly ToggleSwitch _catchUpAtStart = new("Catch up when my working day starts");
    private readonly DayChecks _myDays = new("", new Padding(0));
    private readonly HourRange _myHours = new() { Margin = new Padding(0, 4, 0, 6) };
    private readonly PriorityChecks _myPriorities = new(new Padding(50, 0, 0, 6));
    private readonly PriorityChecks _ccPriorities = new(new Padding(50, 0, 0, 6));

    public NotificationsPage(SettingsContext context)
        : base(context, "Notifications", "Choose what you want to hear about.")
    {
        var unassigned = new Card("New tickets", "A notification for each new unassigned ticket in the groups you monitor.", Kit.CardWidth);
        unassigned.Body.Controls.Add(_notifyUnassigned);
        Panel.Controls.Add(unassigned);

        var mine = new Card("Ticket changes", "When someone else changes these tickets, for the priorities you tick.", Kit.CardWidth);
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
        _myPriorities.Changed += Changed;
        _ccPriorities.Changed += Changed;
        _myDays.Changed += Changed;
        TrackChanges(_notifyUnassigned, _trackMine, _trackCc, _replies, _notes, _status, _assignment, _priority, _automatic, _onlyMyHours, _workingLate, _catchUpAtStart);
        _onlyMyHours.CheckedChanged += (_, _) => _workingLate.Enabled = _onlyMyHours.Checked;
        _myHours.RangeChanged += (_, _) => Changed();
    }

    public override void Load(Settings settings)
    {
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
        s.MyTicketPriorities = _myPriorities.Selected;
        s.CcTicketPriorities = _ccPriorities.Selected;
        s.OnlyNotifyDuringMyHours = _onlyMyHours.Checked;
        s.NotifyWhenWorkingLate = _workingLate.Checked;
        s.CatchUpAtDayStart = _catchUpAtStart.Checked;
        s.MyWorkdayStartHour = _myHours.Start;
        s.MyWorkdayEndHour = _myHours.End;
        s.MyWorkDays = _myDays.Selected;
    }


    private void UpdateChangeTypes()
    {
        foreach (var box in new[] { _replies, _notes, _status, _assignment, _priority, _automatic })
            box.Enabled = _trackMine.Checked || _trackCc.Checked;
        _myPriorities.Enabled = _trackMine.Checked;
        _ccPriorities.Enabled = _trackCc.Checked;
    }
}
