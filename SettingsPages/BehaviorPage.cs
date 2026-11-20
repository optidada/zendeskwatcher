using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Behavior (under Notifications): how long notifications stay, what happens after they waited, and snoozing.</summary>
internal sealed class BehaviorPage : SettingsPage
{
    private static readonly Option<int>[] SnoozeMinuteOptions =
        [new(5, "5 minutes"), new(10, "10 minutes"), new(15, "15 minutes"), new(30, "30 minutes"), new(60, "1 hour")];

    /// <summary>The pills, in this order (Normal in the middle).</summary>
    private static readonly DisplayLength[] Lengths = [DisplayLength.Short, DisplayLength.Normal, DisplayLength.Long];

    private readonly ChoicePills _ticketLength = new("Short", "Normal", "Long");
    private readonly Label _ticketTimes = Theme.Label("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
    private readonly Label _otherTimes = Theme.Label("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
    private readonly ToggleSwitch _dropTaken = new("Assigned to a colleague");
    private readonly ToggleSwitch _dropAssignedToMe = new("Assigned to you");
    private readonly ToggleSwitch _dropAnswered = new("Replied to by a colleague");
    private readonly ToggleSwitch _dropSolvedByMe = new("Solved by you");
    private readonly ToggleSwitch _dropSolvedByColleague = new("Solved by a colleague");
    private readonly ToggleSwitch _dropSolvedByClient = new("Solved by the client");
    private readonly ToggleSwitch _pauseAway = new("Hold the countdown while I'm away");
    private readonly ToggleSwitch _holdBusy = new("Don't interrupt me while I'm busy typing");
    private readonly ChoiceBox _snoozeMinutes = Kit.Combo(SnoozeMinuteOptions, width: 140);

    public BehaviorPage(SettingsContext context)
        : base(context, "Behavior", "How long notifications stay, what happens to ones that waited, and Snooze.")
    {
        var screen = new Card("Display time", "How long a notification stays on screen. Pointing at it pauses the countdown.", Kit.CardWidth);
        _ticketLength.Margin = new Padding(3, 2, 0, 4);
        screen.Body.Controls.Add(_ticketLength);
        _ticketTimes.Margin = new Padding(3, 0, 0, 0);
        screen.Body.Controls.Add(_ticketTimes);
        _otherTimes.Margin = new Padding(3, 0, 0, 12);
        screen.Body.Controls.Add(_otherTimes);
        _ticketLength.SelectedIndexChanged += (_, _) => ShowTicketTimes();
        screen.Body.Controls.Add(_pauseAway);
        screen.Body.Controls.Add(Theme.Label("Away means the screen is locked, or no mouse or keyboard for a minute.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        _holdBusy.Margin = new Padding(_holdBusy.Margin.Left, 10, _holdBusy.Margin.Right, _holdBusy.Margin.Bottom);
        screen.Body.Controls.Add(_holdBusy);
        screen.Body.Controls.Add(Theme.Label("Ticket notifications wait until you pause, at most 10 minutes. P1s, important clients and SLAs due soon still come right away.",
            Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        Panel.Controls.Add(screen);

        var back = new Card("Drop notifications", "Drop a notification (one that waited, or is on screen) when meanwhile the ticket was:",
            Kit.CardWidth);
        // Two columns: assigned and replied on the left; solved on the right.
        var drops = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 4) };
        drops.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (Kit.CardWidth - 44) / 2));
        drops.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ToggleSwitch[] left = [_dropTaken, _dropAssignedToMe, _dropAnswered], right = [_dropSolvedByMe, _dropSolvedByColleague, _dropSolvedByClient];
        for (var i = 0; i < left.Length; i++)
            drops.Controls.Add(left[i], 0, i);
        for (var i = 0; i < right.Length; i++)
            drops.Controls.Add(right[i], 1, i);
        back.Body.Controls.Add(drops);
        back.Body.Controls.Add(Theme.Label("Off: it shows anyway and says what happened (“Taken by Anna meanwhile”). Deleted or moved out of your groups: always dropped. " +
            "Leads: turn off “Assigned to a colleague” and “Replied to by a colleague” to see who took what and who answered. " +
            "Taken before you saw it: (Notifications → New tickets).",
            Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        Panel.Controls.Add(back);

        var snooze = new Card("Snooze", "Snooze hides a notification and brings it back later, if the ticket still needs you.", Kit.CardWidth);
        var snoozeGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        snoozeGrid.Controls.Add(Theme.Label("Show again after"), 0, 0);
        snoozeGrid.Controls.Add(_snoozeMinutes, 1, 0);
        foreach (Control control in snoozeGrid.Controls)
            if (control is Label) control.Anchor = AnchorStyles.Left;
        snooze.Body.Controls.Add(snoozeGrid);
        Panel.Controls.Add(snooze);

        TrackChanges(_pauseAway, _holdBusy, _dropTaken, _dropAssignedToMe, _dropAnswered, _dropSolvedByMe, _dropSolvedByColleague, _dropSolvedByClient, _snoozeMinutes);
        _ticketLength.SelectedIndexChanged += (_, _) => Changed();
    }

    public override bool IsSubPage => true; // under Notifications

    public override void Load(Settings settings)
    {
        _ticketLength.SelectedIndex = Math.Max(0, Array.IndexOf(Lengths, settings.TicketDisplayLength));
        ShowTicketTimes();
        _pauseAway.Checked = settings.PausePopupsWhenAway;
        _holdBusy.Checked = settings.HoldWhileBusy;
        _dropTaken.Checked = settings.DropTaken;
        _dropAssignedToMe.Checked = settings.DropAssignedToMe;
        _dropAnswered.Checked = settings.DropAnswered;
        _dropSolvedByMe.Checked = settings.DropSolvedByMe;
        _dropSolvedByColleague.Checked = settings.DropSolvedByColleague;
        _dropSolvedByClient.Checked = settings.DropSolvedByClient;
        Kit.Select(_snoozeMinutes, settings.SnoozeMinutes, fallback: 10);
    }

    public override void Apply(Settings s)
    {
        s.TicketDisplayLength = Lengths[_ticketLength.SelectedIndex];
        s.ApplyDisplayLength(); // Catch up and app messages follow the same choice
        // Only the duration: the sound is set on Look & sound.
        s.PausePopupsWhenAway = _pauseAway.Checked;
        s.HoldWhileBusy = _holdBusy.Checked;
        s.DropTaken = _dropTaken.Checked;
        s.DropAssignedToMe = _dropAssignedToMe.Checked;
        s.DropAnswered = _dropAnswered.Checked;
        s.DropSolvedByMe = _dropSolvedByMe.Checked;
        s.DropSolvedByColleague = _dropSolvedByColleague.Checked;
        s.DropSolvedByClient = _dropSolvedByClient.Checked;
        s.SnoozeMinutes = Kit.Selected<int>(_snoozeMinutes);
    }

    /// <summary>"P1 until closed · P2 1 min · P3 30 s · P4 15 s" for the chosen length.</summary>
    private void ShowTicketTimes()
    {
        // Short enough for one line in every choice (a second line made the page jump).
        var length = Lengths[_ticketLength.SelectedIndex];
        static string Time(int seconds) => seconds == 0 ? "until closed" : seconds >= 60 ? $"{seconds / 60} min" : $"{seconds} s";
        var (high, normal, low) = (TicketDisplayTimes.For(length, "high"), TicketDisplayTimes.For(length, "normal"), TicketDisplayTimes.For(length, "low"));
        _ticketTimes.Text = high == 0
            ? $"Tickets: P1 and P2 until closed · P3 {Time(normal)} · P4 {Time(low)}"
            : $"Tickets: P1 until closed · P2 {Time(high)} · P3 {Time(normal)} · P4 {Time(low)}";
        var (summary, app) = TicketDisplayTimes.Others(length);
        _otherTimes.Text = $"Catch up {Time(summary.Seconds())} · App messages {Time(app.Seconds())} · Sign-in required until closed";
    }
}
