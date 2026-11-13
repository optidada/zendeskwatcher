namespace ZendeskWatcher.Tests;

public class StatsInsightsTests
{
    private readonly Dictionary<(NotificationKind, StatOutcome), int> _totals = [];

    private StatsInsightsTests Add(NotificationKind kind, StatOutcome outcome, int count)
    {
        _totals[(kind, outcome)] = count;
        return this;
    }

    private List<StatsInsights.Insight> Analyze(int activeDays = 20, Settings? settings = null) =>
        StatsInsights.Analyze(_totals, activeDays, settings ?? new Settings());

    [Fact]
    public void Too_little_data_says_so()
    {
        Add(NotificationKind.NewTicket, StatOutcome.Shown, 4);

        var insight = Assert.Single(Analyze());
        Assert.Equal(StatsInsights.Tone.Hint, insight.Tone);
        Assert.Contains("Not enough notifications yet", insight.Text);
    }

    [Fact]
    public void Nothing_at_all_says_so()
        => Assert.Contains("haven't been any", Assert.Single(Analyze()).Text);

    [Fact]
    public void Mostly_unread_new_tickets_suggest_showing_longer_and_fewer_groups()
    {
        Add(NotificationKind.NewTicket, StatOutcome.Shown, 40).Add(NotificationKind.NewTicket, StatOutcome.TimedOut, 30);

        var insight = Assert.Single(Analyze());
        Assert.Equal(StatsInsights.Tone.Warning, insight.Tone);
        Assert.Contains("75% of new ticket notifications disappeared unread", insight.Text);
        Assert.Contains("Show them longer", insight.Text); // normal tickets show 30 s by default
        Assert.Contains("fewer groups", insight.Text);
    }

    [Fact]
    public void Already_long_display_only_suggests_fewer()
    {
        var settings = new Settings { TicketDisplay = new TicketDisplayTimes { Normal = 60 } };
        Add(NotificationKind.NewTicket, StatOutcome.Shown, 40).Add(NotificationKind.NewTicket, StatOutcome.TimedOut, 30);

        Assert.DoesNotContain("Show them longer", Assert.Single(Analyze(settings: settings)).Text);
    }

    [Fact]
    public void Closed_right_away_suggests_narrowing_cc_to_urgent()
    {
        Add(NotificationKind.CcTicket, StatOutcome.Shown, 20).Add(NotificationKind.CcTicket, StatOutcome.Closed, 12);

        var text = Assert.Single(Analyze()).Text;
        Assert.Contains("closed 60% of CC / following notifications", text);
        Assert.Contains("Only Urgent (P1)", text); // default is Urgent and High
    }

    [Fact]
    public void Cc_already_urgent_only_suggests_turning_it_off()
    {
        Add(NotificationKind.CcTicket, StatOutcome.Shown, 20).Add(NotificationKind.CcTicket, StatOutcome.Closed, 12);

        Assert.Contains("Turning off", Assert.Single(Analyze(settings: new Settings { CcTicketPriorities = ["urgent"] })).Text);
    }

    [Fact]
    public void Lots_of_snoozing_suggests_a_longer_snooze()
    {
        Add(NotificationKind.MyTicket, StatOutcome.Shown, 20).Add(NotificationKind.MyTicket, StatOutcome.Snoozed, 8);

        Assert.Contains("longer snooze time", Assert.Single(Analyze()).Text);
    }

    [Fact]
    public void Useful_notifications_get_a_thumbs_up()
    {
        Add(NotificationKind.MyTicket, StatOutcome.Shown, 20).Add(NotificationKind.MyTicket, StatOutcome.OpenedTicket, 8)
            .Add(NotificationKind.MyTicket, StatOutcome.OpenedZendesk, 1).Add(NotificationKind.MyTicket, StatOutcome.Took, 2);

        var insight = Assert.Single(Analyze());
        Assert.Equal(StatsInsights.Tone.Good, insight.Tone);
        Assert.Contains("you opened or took 55%", insight.Text);
    }

    [Fact]
    public void Many_per_day_names_the_busiest_kind()
    {
        Add(NotificationKind.NewTicket, StatOutcome.Shown, 900).Add(NotificationKind.NewTicket, StatOutcome.OpenedTicket, 500)
            .Add(NotificationKind.MyTicket, StatOutcome.Shown, 100).Add(NotificationKind.MyTicket, StatOutcome.OpenedTicket, 60);

        var busy = Analyze(activeDays: 20).First();
        Assert.Equal(StatsInsights.Tone.Warning, busy.Tone);
        Assert.Contains("About 50 notifications per working day, mostly new ticket notifications", busy.Text);
    }

    [Fact]
    public void Kinds_with_few_notifications_are_not_judged()
    {
        Add(NotificationKind.MyTicket, StatOutcome.Shown, 20).Add(NotificationKind.MyTicket, StatOutcome.OpenedTicket, 15)
            .Add(NotificationKind.HelpColleague, StatOutcome.Shown, 3).Add(NotificationKind.HelpColleague, StatOutcome.TimedOut, 3);

        Assert.DoesNotContain(Analyze(), i => i.Text.Contains("colleague"));
    }

    [Fact]
    public void Active_days_count_days_with_notifications()
    {
        var stats = new NotificationStats();
        var today = new DateOnly(2026, 11, 12);
        stats.Record(NotificationKind.NewTicket, StatOutcome.Shown, today);
        stats.Record(NotificationKind.NewTicket, StatOutcome.Shown, today.AddDays(-2));
        stats.Record(NotificationKind.NewTicket, StatOutcome.Copied, today.AddDays(-3)); // no notification shown that day

        Assert.Equal(2, stats.ActiveDays(today));
    }
}
