using ZendeskWatcher.Tracking;

namespace ZendeskWatcher.Tests;

/// <summary>
/// "Only notify me during my working hours" + "Also when I'm working late": outside my hours, new tickets notify while
/// I'm active in Zendesk (a change or comment in the last 30 minutes), and stay quiet otherwise.
/// </summary>
public class WorkingLateTests
{
    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "test",
        WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
        TrackMyTickets = false,
        OnlyNotifyDuringMyHours = true,
        NotifyWhenWorkingLate = true,
        MyWorkDays = [] // no working days: always outside my hours
    };

    public WorkingLateTests() => File.Delete(AppPaths.StateFile);

    /// <summary>Notices ("Working late?") from every check, also the first one.</summary>
    private readonly List<NotificationContent> _notices = [];

    private async Task<TicketWatcher> StartAsync()
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        _notices.AddRange(_notifier.Shown.Where(c => c.Title == "Working late?"));
        _notifier.Shown.Clear();
        return watcher;
    }

    /// <summary>I commented on one of my tickets <paramref name="minutesAgo"/> minutes ago.</summary>
    private void IWorked(int minutesAgo)
    {
        _zendesk.AddAssigned(700, "normal", assignee: _zendesk.Me.Id);
        _zendesk.Reply(700, _zendesk.Me.Id, "On it");
        _zendesk.Audits[700][^1].CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
    }

    [Fact]
    public async Task Active_in_Zendesk_outside_my_hours_new_tickets_notify_with_a_notice_once()
    {
        IWorked(minutesAgo: 5);
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(701, subject: "Site down");
        await watcher.PollAsync(CancellationToken.None);
        _zendesk.AddUnassigned(702, subject: "Login broken");
        await watcher.PollAsync(CancellationToken.None);

        _notices.AddRange(_notifier.Shown.Where(c => c.Title == "Working late?"));
        Assert.Single(_notices);                                                       // once, not every check
        Assert.Contains(_notifier.Shown, c => c.Title.Contains("Site down"));
        Assert.Contains(_notifier.Shown, c => c.Title.Contains("Login broken"));
    }

    [Fact]
    public async Task The_notice_shows_once_a_day_also_after_the_app_restarts()
    {
        IWorked(minutesAgo: 5);
        await StartAsync();
        await StartAsync(); // restarted (an update, a new build): reads the saved state

        Assert.Single(_notices);
    }

    [Fact]
    public async Task Inactive_for_30_minutes_outside_my_hours_stays_quiet()
    {
        IWorked(minutesAgo: 45);
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(703, subject: "Site down");
        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task Without_the_checkbox_working_late_doesnt_count()
    {
        _settings.NotifyWhenWorkingLate = false;
        IWorked(minutesAgo: 5);
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(704, subject: "Site down");
        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(_notifier.Shown);
    }
}
