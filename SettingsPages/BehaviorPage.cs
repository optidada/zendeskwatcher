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
    private readonly ToggleSwitch _dropAnswered = new("Also drop it when someone from your side already replied");
    private readonly ToggleSwitch _pauseAway = new("Hold the countdown while I'm away");
    private readonly ToggleSwitch _holdBusy = new("Hold ticket notifications while I'm typing a lot");
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
        screen.Body.Controls.Add(Theme.Label("They come when you slow down, at most 10 minutes later. P1, important clients and SLAs due soon never wait.",
            Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        Panel.Controls.Add(screen);

        var back = new Card("Already handled", "A notification that had to wait is checked again first, and dropped if the ticket was " +
            "taken, solved or closed meanwhile.", Kit.CardWidth);
        back.Body.Controls.Add(_dropAnswered);
        Panel.Controls.Add(back);

        var snooze = new Card("Snooze", "Snooze hides a notification and brings it back later, if the ticket still needs you.", Kit.CardWidth);
        var snoozeGrid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface };
        snoozeGrid.Controls.Add(Theme.Label("Show again after"), 0, 0);
        snoozeGrid.Controls.Add(_snoozeMinutes, 1, 0);
        foreach (Control control in snoozeGrid.Controls)
            if (control is Label) control.Anchor = AnchorStyles.Left;
        snooze.Body.Controls.Add(snoozeGrid);
        Panel.Controls.Add(snooze);

        TrackChanges(_pauseAway, _holdBusy, _dropAnswered, _snoozeMinutes);
        _ticketLength.SelectedIndexChanged += (_, _) => Changed();
    }

    public override bool IsSubPage => true; // under Notifications

    public override void Load(Settings settings)
    {
        _ticketLength.SelectedIndex = Math.Max(0, Array.IndexOf(Lengths, settings.TicketDisplayLength));
        ShowTicketTimes();
        _pauseAway.Checked = settings.PausePopupsWhenAway;
        _holdBusy.Checked = settings.HoldWhileBusy;
        _dropAnswered.Checked = settings.DropAnswered;
        Kit.Select(_snoozeMinutes, settings.SnoozeMinutes, fallback: 10);
    }

    public override void Apply(Settings s)
    {
        s.TicketDisplayLength = Lengths[_ticketLength.SelectedIndex];
        s.ApplyDisplayLength(); // Catch up and app messages follow the same choice
        // Only the duration: the sound is set on Look & sound.
        s.PausePopupsWhenAway = _pauseAway.Checked;
        s.HoldWhileBusy = _holdBusy.Checked;
        s.DropAnswered = _dropAnswered.Checked;
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
