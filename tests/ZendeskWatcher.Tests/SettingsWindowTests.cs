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
        HelpColleagues = true,
        HelpSameAsMonitored = false,
        HelpGroups = [new WatchedGroup { Id = 4, Name = "Onboarding" }],
        HelpExcludedAssignees = [new Colleague { Id = 9, Name = "Alex Example" }],
        WorkdayStartHour = 9,
        WorkdayEndHour = 18,
        HelpPriorities = ["normal", "low"],
        HelpOnlyDuringMyHours = false,
        ShowStartSummary = false,
        SnoozeMinutes = 30,
        SnoozeBehavior = SnoozeBehavior.Always,
        ShowCommentText = false,
        NewTicketStyle = new NotificationStyle { Sound = ToastSound.ChipBlip, Display = ToastDisplay.Long },
        MyTicketStyle = new NotificationStyle { Sound = ToastSound.Silent, Display = ToastDisplay.UntilDismissed },
        HelpStyle = new NotificationStyle { Sound = ToastSound.SongGroove, Display = ToastDisplay.Standard },
        Theme = NotificationTheme.CleanLight,
        Position = PopupPosition.TopRight,
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
