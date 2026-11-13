using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Behavior (under Notifications): how long notifications stay, what happens after they waited, and snoozing.</summary>
internal sealed class BehaviorPage : SettingsPage
{
    private static readonly Option<int>[] SnoozeMinuteOptions =
        [new(5, "5 minutes"), new(10, "10 minutes"), new(15, "15 minutes"), new(30, "30 minutes"), new(60, "1 hour")];

    private static readonly Option<SnoozeBehavior>[] SnoozeBehaviorOptions =
    [
        new(SnoozeBehavior.IfStillNeeded, "Only if the ticket still needs attention"),
        new(SnoozeBehavior.Always, "Always")
    ];

    private static readonly Option<int>[] TicketTimeOptions =
        [new(10, "10 seconds"), new(15, "15 seconds"), new(30, "30 seconds"), new(60, "1 minute"), new(0, "Until dismissed")];

    private static readonly Option<ToastDisplay>[] DisplayOptions =
    [
        new(ToastDisplay.Standard, "5 seconds"), new(ToastDisplay.Medium, "10 seconds"), new(ToastDisplay.Long, "25 seconds"),
        new(ToastDisplay.UntilDismissed, "Until dismissed")
    ];

    private static readonly Option<int>[] RecheckOptions =
        [new(0, "Never"), new(5, "5 minutes"), new(15, "15 minutes"), new(30, "30 minutes"), new(60, "1 hour")];

    private static readonly Option<int>[] SummarizeOptions =
        [new(0, "Never"), new(3, "3 are waiting"), new(5, "5 are waiting"), new(10, "10 are waiting"), new(20, "20 are waiting")];

    private readonly ComboBox _urgentTime = Kit.Combo(TicketTimeOptions, width: 160);
    private readonly ComboBox _highTime = Kit.Combo(TicketTimeOptions, width: 160);
    private readonly ComboBox _normalTime = Kit.Combo(TicketTimeOptions, width: 160);
    private readonly ComboBox _lowTime = Kit.Combo(TicketTimeOptions, width: 160);
    private readonly ComboBox _summaryTime = Kit.Combo(DisplayOptions, width: 160);
    private readonly ComboBox _appTime = Kit.Combo(DisplayOptions, width: 160);
    private readonly ComboBox _recheck = Kit.Combo(RecheckOptions, width: 160);
    private readonly ComboBox _summarize = Kit.Combo(SummarizeOptions, width: 160);
    private readonly ToggleSwitch _dropAnswered = new("Also drop it once it's been answered");
    private readonly ToggleSwitch _takeTurns = new("Several notifications take turns: one counts down at a time");
    private readonly ToggleSwitch _pauseAway = new("Hold the countdown while I'm away");
    private readonly ComboBox _snoozeMinutes = Kit.Combo(SnoozeMinuteOptions, width: 140);
    private readonly ComboBox _snoozeBehavior = Kit.Combo(SnoozeBehaviorOptions, width: 300);

    public BehaviorPage(SettingsContext context)
        : base(context, "Behavior", "How long notifications stay on screen, what happens to ones that waited, and what Snooze does.")
    {
        var screen = new Card("On screen", "How long a notification stays before it closes on its own; hovering it pauses the countdown. " +
            "Windows notifications can only stay about 5 s (for up to 15 s here), about 25 s (for longer) or until dismissed.", Kit.CardWidth);
        AddTimeSection(screen, "Ticket notifications, by the ticket's priority (no priority counts as Normal (P3))",
            ("Urgent (P1)", _urgentTime), ("High (P2)", _highTime), ("Normal (P3)", _normalTime), ("Low (P4)", _lowTime));
        AddTimeSection(screen, "Other notifications (\"Sign-in required\" always stays until dismissed)",
            ("Start summary", _summaryTime), ("App messages", _appTime));
        screen.Body.Controls.Add(_takeTurns);
        screen.Body.Controls.Add(_pauseAway);
        screen.Body.Controls.Add(Theme.Label("You're away while the screen is locked, or after a minute without mouse or keyboard. " +
            "Taking turns and holding: not for the Windows style.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        Panel.Controls.Add(screen);

        var back = new Card("After waiting", "Notifications that waited a while (behind others, or while you were away) are checked " +
            "again first: dropped when a new ticket has been taken, or the ticket is solved or closed. Not for the Windows style.", Kit.CardWidth);
        var backGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        backGrid.Controls.Add(Theme.Label("Check again after"), 0, 0);
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

        TrackChanges(_urgentTime, _highTime, _normalTime, _lowTime, _summaryTime, _appTime, _takeTurns, _pauseAway, _recheck, _summarize, _dropAnswered, _snoozeMinutes, _snoozeBehavior);
    }

    public override bool IsSubPage => true; // under Notifications

    public override void Load(Settings settings)
    {
        Kit.Select(_urgentTime, settings.TicketDisplay.Urgent, fallback: 0);
        Kit.Select(_highTime, settings.TicketDisplay.High, fallback: 60);
        Kit.Select(_normalTime, settings.TicketDisplay.Normal, fallback: 30);
        Kit.Select(_lowTime, settings.TicketDisplay.Low, fallback: 15);
        Kit.Select(_summaryTime, settings.SummaryStyle.Display);
        Kit.Select(_appTime, settings.AppMessageStyle.Display);
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
        s.TicketDisplay = new TicketDisplayTimes
        {
            Urgent = Kit.Selected<int>(_urgentTime), High = Kit.Selected<int>(_highTime),
            Normal = Kit.Selected<int>(_normalTime), Low = Kit.Selected<int>(_lowTime)
        };
        // Only the duration: the sound is set on Look & sound.
        s.SummaryStyle.Display = Kit.Selected<ToastDisplay>(_summaryTime);
        s.AppMessageStyle.Display = Kit.Selected<ToastDisplay>(_appTime);
        s.PopupsTakeTurns = _takeTurns.Checked;
        s.PausePopupsWhenAway = _pauseAway.Checked;
        s.RecheckWaitingAfterMinutes = Kit.Selected<int>(_recheck);
        s.SummarizeWaitingAbove = Kit.Selected<int>(_summarize);
        s.DropAnswered = _dropAnswered.Checked;
        s.SnoozeMinutes = Kit.Selected<int>(_snoozeMinutes);
        s.SnoozeBehavior = Kit.Selected<SnoozeBehavior>(_snoozeBehavior);
    }

    /// <summary>A muted heading, then one row per label with its duration dropdown.</summary>
    private static void AddTimeSection(Card card, string heading, params (string Label, ComboBox Combo)[] rows)
    {
        var title = Theme.Label(heading, Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
        title.Margin = new Padding(0, 4, 0, 2); // level with the card text above
        card.Body.Controls.Add(title);
        // The table keeps its default left margin, like the other cards' tables: the labels line up.
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(3, 0, 3, 10) };
        for (var i = 0; i < rows.Length; i++)
        {
            var text = Theme.Label(rows[i].Label);
            text.Anchor = AnchorStyles.Left;
            text.MinimumSize = new Size(140, 0);
            table.Controls.Add(text, 0, i);
            table.Controls.Add(rows[i].Combo, 1, i);
        }
        card.Body.Controls.Add(table);
    }
}
