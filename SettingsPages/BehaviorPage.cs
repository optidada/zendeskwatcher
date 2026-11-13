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

    private static readonly Option<int>[] RecheckOptions =
        [new(0, "Never"), new(5, "5 minutes"), new(15, "15 minutes"), new(30, "30 minutes"), new(60, "1 hour")];

    private static readonly Option<int>[] SummarizeOptions =
        [new(0, "Never"), new(3, "3 are waiting"), new(5, "5 are waiting"), new(10, "10 are waiting"), new(20, "20 are waiting")];

    private readonly ComboBox _recheck = Kit.Combo(RecheckOptions, width: 160);
    private readonly ComboBox _summarize = Kit.Combo(SummarizeOptions, width: 160);
    private readonly ToggleSwitch _dropAnswered = new("Also drop it once it's been answered");
    private readonly ToggleSwitch _takeTurns = new("Several notifications take turns: one counts down at a time");
    private readonly ToggleSwitch _pauseAway = new("Hold the countdown while I'm away");
    private readonly ComboBox _snoozeMinutes = Kit.Combo(SnoozeMinuteOptions, width: 140);
    private readonly ComboBox _snoozeBehavior = Kit.Combo(SnoozeBehaviorOptions, width: 300);

    public BehaviorPage(SettingsContext context)
        : base(context, "Behavior", "How notifications behave on screen, when you come back, and what Snooze does.")
    {
        var timing = new Card("Timing", "How long notifications stay on screen is set per kind under Look & sound. You're away " +
            "while the screen is locked, or after a minute without mouse or keyboard. Not for the Windows style: it follows Windows' own timing.", Kit.CardWidth);
        timing.Body.Controls.Add(_takeTurns);
        timing.Body.Controls.Add(_pauseAway);
        Panel.Controls.Add(timing);

        var back = new Card("Coming back", "Notifications that waited a while (behind others, or while you were away) are checked " +
            "again first: dropped when a new ticket has been taken, or the ticket is solved or closed. Not for the Windows style.", Kit.CardWidth);
        var backGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        backGrid.Controls.Add(Theme.Label("Check again after waiting"), 0, 0);
        backGrid.Controls.Add(_recheck, 1, 0);
        backGrid.Controls.Add(Theme.Label("One summary instead when more than"), 0, 1);
        backGrid.Controls.Add(_summarize, 1, 1);
        foreach (Control control in backGrid.Controls)
            if (control is Label) control.Anchor = AnchorStyles.Left;
        back.Body.Controls.Add(backGrid);
        back.Body.Controls.Add(_dropAnswered);
        back.Body.Controls.Add(Theme.Label("Answered: an agent replied to the customer on a colleague's ticket, or you acted on " +
            "your own or CC'd ticket. Also used by Snooze, and no \"Customer waiting\" when a colleague already replied.",
            Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        Panel.Controls.Add(back);

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

        TrackChanges(_takeTurns, _pauseAway, _recheck, _summarize, _dropAnswered, _snoozeMinutes, _snoozeBehavior);
    }

    public override bool IsSubPage => true; // under Notifications

    public override void Load(Settings settings)
    {
        _takeTurns.Checked = settings.PopupsTakeTurns;
        _pauseAway.Checked = settings.PausePopupsWhenAway;
        Kit.Select(_recheck, settings.RecheckWaitingAfterMinutes, fallback: 15);
        Kit.Select(_summarize, settings.SummarizeWaitingAbove, fallback: 5);
        _dropAnswered.Checked = settings.DropAnswered;
        Kit.Select(_snoozeMinutes, settings.SnoozeMinutes, fallback: 10);
        Kit.Select(_snoozeBehavior, settings.SnoozeBehavior);
    }

    public override void Apply(Settings s)
    {
        s.PopupsTakeTurns = _takeTurns.Checked;
        s.PausePopupsWhenAway = _pauseAway.Checked;
        s.RecheckWaitingAfterMinutes = Kit.Selected<int>(_recheck);
        s.SummarizeWaitingAbove = Kit.Selected<int>(_summarize);
        s.DropAnswered = _dropAnswered.Checked;
        s.SnoozeMinutes = Kit.Selected<int>(_snoozeMinutes);
        s.SnoozeBehavior = Kit.Selected<SnoozeBehavior>(_snoozeBehavior);
    }
}
