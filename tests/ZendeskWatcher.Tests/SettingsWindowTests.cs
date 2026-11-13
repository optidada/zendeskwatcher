namespace ZendeskWatcher.Tests;

/// <summary>The Settings window: every setting is shown by a page and written back unchanged.</summary>
public class SettingsWindowTests
{
    /// <summary>Every setting differs from its default, so a page that forgets to load or save one is caught.</summary>
    private static Settings NonDefaultSettings() => new()
    {
        Subdomain = "acme",
        WatchedGroups = [new WatchedGroup { Id = 1, Name = "Support Tier 2" }],
        PollIntervalSeconds = 120,
        TrackMyTickets = false,
        TrackCcTickets = true,
        NotifyUnassigned = false,
        NotifyPublicReplies = false,
        NotifyInternalNotes = false,
        NotifyStatusChanges = false,
        NotifyAssignmentChanges = false,
        NotifyPriorityChanges = false,
        HelpColleagues = false,
        HelpRegionDefaultDone = true,
        HelpSameAsMonitored = false,
        HelpGroups = [new WatchedGroup { Id = 4, Name = "Onboarding" }],
        HelpExcludedAssignees = [new Colleague { Id = 9, Name = "Alex Example" }],
        WorkdayStartHour = 9,
        WorkdayEndHour = 18,
        HelpPriorities = ["normal", "low"],
        InProgressGraceHours = 8,
        HelpOnlyDuringMyHours = false,
        ShowStartSummary = false,
        SnoozeMinutes = 30,
        SnoozeBehavior = SnoozeBehavior.Always,
        ShowCommentText = false,
        MyTicketPriorities = ["urgent"],
        CcTicketPriorities = ["high", "low"],
        NewTicketStyle = new NotificationStyle { Sound = ToastSound.ChipBlip },
        MyTicketStyle = new NotificationStyle { Sound = ToastSound.Mail },
        HelpStyle = new NotificationStyle { Sound = ToastSound.SongGroove },
        CcTicketStyle = new NotificationStyle { Sound = ToastSound.ChipAlert },
        TicketDisplay = new TicketDisplayTimes { Urgent = 60, High = 30, Normal = 15, Low = 0 },
        SummaryStyle = new NotificationStyle { Sound = ToastSound.ChipFanfare, Display = ToastDisplay.UntilDismissed },
        AppMessageStyle = new NotificationStyle { Sound = ToastSound.Sms, Display = ToastDisplay.Medium },
        Theme = NotificationTheme.CleanLight,
        AppTheme = NotificationTheme.Terminal,
        PopupsTakeTurns = false,
        PausePopupsWhenAway = false,
        RecheckWaitingAfterMinutes = 60,
        SummarizeWaitingAbove = 0,
        DropAnswered = false,
        OnlyNotifyDuringMyHours = true,
        CatchUpAtDayStart = false,
        MyWorkdayStartHour = 7,
        MyWorkdayEndHour = 15,
        MyWorkDays = [DayOfWeek.Tuesday, DayOfWeek.Saturday],
        WorkDays = [DayOfWeek.Monday, DayOfWeek.Sunday],
        NotificationsLikeApp = false,
        Position = PopupPosition.TopRight,
        NotificationSize = 130,
        ShowAppLogo = false,
        GroupNotificationsByType = false
    };

    [Fact]
    public void Every_setting_survives_the_pages_unchanged()
    {
        var expected = NonDefaultSettings();
        Assert.NotEqual(new Settings().ToJson(), expected.ToJson());

        var shown = OnUiThread(() =>
        {
            using var form = new SettingsForm(expected, _ => new FakeZendesk(), new Notifier((_, _) => { }));
            return form.PagesAsSettings();
        });

        Assert.Equal(expected.ToJson(), shown.ToJson());
    }

    [Fact]
    public void Defaults_survive_the_pages_unchanged()
    {
        var expected = new Settings { Subdomain = "acme" };

        var shown = OnUiThread(() =>
        {
            using var form = new SettingsForm(expected, _ => new FakeZendesk(), new Notifier((_, _) => { }));
            return form.PagesAsSettings();
        });

        Assert.Equal(expected.ToJson(), shown.ToJson());
    }

    [Theory]
    [InlineData(75, 60)]     // from an older version: the nearest step
    [InlineData(100, 120)]
    [InlineData(600, 300)]   // longer than the longest choice: 5 min
    [InlineData(10, 30)]
    public void Check_interval_snaps_to_the_nearest_step(int saved, int shown)
    {
        var result = OnUiThread(() =>
        {
            using var form = new SettingsForm(new Settings { Subdomain = "acme", PollIntervalSeconds = saved }, _ => new FakeZendesk(), new Notifier((_, _) => { }));
            return form.PagesAsSettings().PollIntervalSeconds;
        });
        Assert.Equal(shown, result);
    }

    [Fact]
    public void Reset_shows_the_defaults_but_keeps_account_and_groups()
    {
        var saved = NonDefaultSettings();

        var shown = OnUiThread(() =>
        {
            using var form = new SettingsForm(saved, _ => new FakeZendesk(), new Notifier((_, _) => { }));
            form.ResetToDefaults();
            return form.PagesAsSettings();
        });

        var expected = new Settings { Subdomain = saved.Subdomain, WatchedGroups = saved.WatchedGroups };
        Assert.Equal(expected.ToJson(), shown.ToJson());
    }

    [Fact]
    public void Ticket_notifications_stay_longer_the_higher_the_priority_by_default()
    {
        var times = new Settings().TicketDisplay;
        Assert.Equal((0, 60, 30, 15), (times.Urgent, times.High, times.Normal, times.Low)); // 0: until dismissed
        Assert.Equal(30, times.For(null)); // no priority counts as normal
    }

    [Fact]
    public void Builds_without_a_default_subdomain_start_empty()
        => Assert.Equal("", Settings.DefaultSubdomain); // the release build sets it from a GitHub secret

    /// <summary>Windows Forms controls want a single-threaded apartment, like the app's UI thread.</summary>
    private static T OnUiThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException("Failed on the UI thread", error);
        return result;
    }
}
