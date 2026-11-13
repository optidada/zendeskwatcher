namespace ZendeskWatcher.Tests;

public class NotificationStatsTests
{
    private static readonly DateOnly Today = new(2026, 11, 12);

    [Fact]
    public void Counts_per_kind_and_outcome()
    {
        var stats = new NotificationStats();
        stats.Record(NotificationKind.NewTicket, StatOutcome.Shown, Today);
        stats.Record(NotificationKind.NewTicket, StatOutcome.Shown, Today);
        stats.Record(NotificationKind.NewTicket, StatOutcome.Took, Today);
        stats.Record(NotificationKind.MyTicket, StatOutcome.Shown, Today.AddDays(-3));

        var totals = stats.Totals(Today);

        Assert.Equal(2, totals[(NotificationKind.NewTicket, StatOutcome.Shown)]);
        Assert.Equal(1, totals[(NotificationKind.NewTicket, StatOutcome.Took)]);
        Assert.Equal(1, totals[(NotificationKind.MyTicket, StatOutcome.Shown)]);
        Assert.False(totals.ContainsKey((NotificationKind.CcTicket, StatOutcome.Shown)));
    }

    [Fact]
    public void Only_the_last_30_days_count_and_older_days_are_dropped()
    {
        var stats = new NotificationStats();
        stats.Record(NotificationKind.NewTicket, StatOutcome.Shown, Today.AddDays(-30)); // 31 days ago counting today
        stats.Record(NotificationKind.NewTicket, StatOutcome.Shown, Today.AddDays(-29)); // the oldest day that counts
        Assert.Equal(1, stats.Totals(Today)[(NotificationKind.NewTicket, StatOutcome.Shown)]);

        stats.Record(NotificationKind.NewTicket, StatOutcome.Shown, Today);
        Assert.Equal(2, stats.Totals(Today, days: 365)[(NotificationKind.NewTicket, StatOutcome.Shown)]); // the old day is gone
    }

    [Fact]
    public void Kept_in_a_file_across_restarts_and_clearable()
    {
        var file = Path.Combine(AppPaths.DataDir, "stats-test.json");
        File.Delete(file);

        new NotificationStats(file).Record(NotificationKind.HelpColleague, StatOutcome.Snoozed, Today);
        var reloaded = new NotificationStats(file);
        Assert.Equal(1, reloaded.Totals(Today)[(NotificationKind.HelpColleague, StatOutcome.Snoozed)]);

        reloaded.Clear();
        Assert.Empty(new NotificationStats(file).Totals(Today));
    }

    [Fact]
    public void A_damaged_file_starts_fresh()
    {
        var file = Path.Combine(AppPaths.DataDir, "stats-damaged.json");
        File.WriteAllText(file, "{ not json");

        var stats = new NotificationStats(file);

        Assert.Empty(stats.Totals(Today));
    }

    [Theory]
    [InlineData(PopupForm.Result.Open, Notifier.ActionOpenTicket, StatOutcome.OpenedTicket)]
    [InlineData(PopupForm.Result.Open, Notifier.ActionOpenZendesk, StatOutcome.OpenedZendesk)]
    [InlineData(PopupForm.Result.Take, Notifier.ActionOpenTicket, StatOutcome.Took)]
    [InlineData(PopupForm.Result.Snooze, Notifier.ActionOpenTicket, StatOutcome.Snoozed)]
    [InlineData(PopupForm.Result.Dismiss, Notifier.ActionOpenTicket, StatOutcome.Closed)]
    [InlineData(PopupForm.Result.Timeout, Notifier.ActionOpenTicket, StatOutcome.TimedOut)]
    public void Popup_outcomes(PopupForm.Result result, string action, StatOutcome expected)
        => Assert.Equal(expected, Notifier.StatForPopup(result, action));

    [Fact]
    public void Popups_closed_without_an_outcome_are_not_counted()
        => Assert.Null(Notifier.StatForPopup(PopupForm.Result.None, Notifier.ActionOpenTicket));

    [Theory]
    [InlineData(Notifier.ActionOpenTicket, StatOutcome.OpenedTicket)]
    [InlineData(Notifier.ActionOpenZendesk, StatOutcome.OpenedZendesk)]
    [InlineData(Notifier.ActionTake, StatOutcome.Took)]
    public void Windows_notification_clicks(string action, StatOutcome expected)
        => Assert.Equal(expected, NotificationStats.ForToastAction(action));

    [Theory]
    [InlineData(NotificationKind.NewTicket)]
    [InlineData(NotificationKind.MyTicket)]
    [InlineData(NotificationKind.HelpColleague)]
    [InlineData(null)] // "Several"
    public void Try_it_samples_never_count(NotificationKind? kind)
    {
        var samples = Notifier.TestSamples(kind).ToList();
        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.False(s.IsTicketNotification));
    }
}
