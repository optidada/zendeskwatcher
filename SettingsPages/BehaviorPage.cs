using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Behavior (under Notifications): how notifications behave on screen, and snoozing.</summary>
internal sealed class BehaviorPage : SettingsPage
{
    private static readonly Option<int>[] SnoozeMinuteOptions =
        [new(5, "5 minutes"), new(10, "10 minutes"), new(15, "15 minutes"), new(30, "30 minutes"), new(60, "1 hour")];

    private static readonly Option<SnoozeBehavior>[] SnoozeBehaviorOptions =
    [
        new(SnoozeBehavior.IfStillNeeded, "Only if the ticket still needs attention"),
        new(SnoozeBehavior.Always, "Always")
    ];

    private readonly ToggleSwitch _takeTurns = new("Several notifications take turns: one counts down at a time");
    private readonly ToggleSwitch _pauseAway = new("Hold the countdown while I'm away");
    private readonly ComboBox _snoozeMinutes = Kit.Combo(SnoozeMinuteOptions, width: 140);
    private readonly ComboBox _snoozeBehavior = Kit.Combo(SnoozeBehaviorOptions, width: 300);

    public BehaviorPage(SettingsContext context)
        : base(context, "Behavior", "How notifications behave on screen, and what Snooze does.")
    {
        var timing = new Card("Timing", "How long notifications stay on screen is set per kind under Look & sound. You're away " +
            "while the screen is locked, or after a minute without mouse or keyboard. Not for the Windows style: it follows Windows' own timing.", Kit.CardWidth);
        timing.Body.Controls.Add(_takeTurns);
        timing.Body.Controls.Add(_pauseAway);
        Panel.Controls.Add(timing);

        var snooze = new Card("Snooze", "Snooze on a notification hides it and shows it again later. " +
            "A ticket still needs attention when a new ticket is still unassigned, or another ticket isn't solved or closed. " +
            "While notifications are paused, snoozed notifications wait until the pause ends.", Kit.CardWidth);
        var snoozeGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        snoozeGrid.Controls.Add(Theme.Label("Show again after"), 0, 0);
        snoozeGrid.Controls.Add(_snoozeMinutes, 1, 0);
        snoozeGrid.Controls.Add(Theme.Label("Show it"), 0, 1);
        snoozeGrid.Controls.Add(_snoozeBehavior, 1, 1);
        foreach (Control control in snoozeGrid.Controls)
            if (control is Label) control.Anchor = AnchorStyles.Left;
        snooze.Body.Controls.Add(snoozeGrid);
        Panel.Controls.Add(snooze);

        TrackChanges(_takeTurns, _pauseAway, _snoozeMinutes, _snoozeBehavior);
    }

    public override bool IsSubPage => true; // under Notifications

    public override void Load(Settings settings)
    {
        _takeTurns.Checked = settings.PopupsTakeTurns;
        _pauseAway.Checked = settings.PausePopupsWhenAway;
        Kit.Select(_snoozeMinutes, settings.SnoozeMinutes, fallback: 10);
        Kit.Select(_snoozeBehavior, settings.SnoozeBehavior);
    }

    public override void Apply(Settings s)
    {
        s.PopupsTakeTurns = _takeTurns.Checked;
        s.PausePopupsWhenAway = _pauseAway.Checked;
        s.SnoozeMinutes = Kit.Selected<int>(_snoozeMinutes);
        s.SnoozeBehavior = Kit.Selected<SnoozeBehavior>(_snoozeBehavior);
    }
}
