using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>The Experimental page's features end to end against the fake Zendesk, with the real (bundled) language model.</summary>
public class ExperimentalFeatureTests
{
    private const long Customer = 50;
    private const long Jane = 70;

    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "test",
        WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
        TrackMyTickets = false
    };

    public ExperimentalFeatureTests()
    {
        File.Delete(AppPaths.StateFile);
        var history = Path.Combine(AppPaths.DataDir, "cache", "ticket-history.json");
        if (File.Exists(history))
            File.Delete(history); // each test reads its own history
        _zendesk.Users[Customer] = new User { Id = Customer, Name = "Jane Customer", Role = "end-user" };
        _zendesk.Users[Jane] = new User { Id = Jane, Name = "Jane Agent", Role = "agent" };
    }

    private async Task<TicketWatcher> StartAsync()
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        await watcher.HistoryLoaded;
        TakeShown();
        return watcher;
    }

    private List<NotificationContent> TakeShown()
    {
        var shown = _notifier.Shown.ToList();
        _notifier.Shown.Clear();
        return shown;
    }

    private Ticket Solved(long id, string subject, long assignee, string priority = "normal")
    {
        var t = _zendesk.AddAssigned(id, priority, assignee, subject);
        t.Status = "solved";
        return t;
    }

    [Fact]
    public async Task Priority_guess_counts_similar_earlier_urgent_tickets()
    {
        _settings.GuessPriority = true;
        Solved(601, "Checkout fails for all customers", Jane, "urgent");
        Solved(602, "Checkout not working, customers cannot pay", Jane, "urgent");
        Solved(603, "Checkout broken in production", Jane, "urgent");
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(610, subject: "Checkout failing").Priority = "normal";
        await watcher.PollAsync(CancellationToken.None);
        var hint = Assert.Single(TakeShown()).PriorityHint;
        Assert.NotNull(hint);
        Assert.Contains(hint!.Reasons, r => r.StartsWith("like ") && r.EndsWith("earlier P1s"));
    }

    [Fact]
    public async Task A_restarted_watcher_keeps_the_ticket_history_instead_of_reading_it_again()
    {
        _settings.GuessPriority = true;
        Solved(601, "Checkout fails for all customers", Jane, "urgent");
        Solved(602, "Checkout not working, customers cannot pay", Jane, "urgent");
        Solved(603, "Checkout broken in production", Jane, "urgent");
        var first = await StartAsync();
        foreach (var id in new long[] { 601, 602, 603 })
            _zendesk.Tickets.Remove(id); // only the history handed over knows them now

        var second = await StartAsync(first);
        _zendesk.AddUnassigned(610, subject: "Checkout failing").Priority = "normal";
        await second.PollAsync(CancellationToken.None);
        var hint = Assert.Single(TakeShown()).PriorityHint;
        Assert.Contains(hint!.Reasons, r => r.StartsWith("like ") && r.EndsWith("earlier P1s"));
    }

    [Fact]
    public async Task After_an_app_restart_the_saved_ticket_history_is_used_instead_of_reading_it_again()
    {
        _settings.GuessPriority = true;
        Solved(601, "Checkout fails for all customers", Jane, "urgent");
        Solved(602, "Checkout not working, customers cannot pay", Jane, "urgent");
        Solved(603, "Checkout broken in production", Jane, "urgent");
        await StartAsync();
        foreach (var id in new long[] { 601, 602, 603 })
            _zendesk.Tickets.Remove(id); // only the saved history knows them now

        var restarted = await StartAsync(); // a new app run: nothing handed over in memory
        _zendesk.AddUnassigned(610, subject: "Checkout failing").Priority = "normal";
        await restarted.PollAsync(CancellationToken.None);
        var hint = Assert.Single(TakeShown()).PriorityHint;
        Assert.Contains(hint!.Reasons, r => r.StartsWith("like ") && r.EndsWith("earlier P1s"));
    }

    private async Task<TicketWatcher> StartAsync(TicketWatcher previous)
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        watcher.TakeHistoryFrom(previous);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        await watcher.HistoryLoaded;
        TakeShown();
        return watcher;
    }

    [Fact]
    public async Task A_thank_you_on_my_ticket_is_quiet()
    {
        _settings.TrackMyTickets = true;
        _settings.WatchedGroups = [];
        _settings.QuietThankYous = true;
        _zendesk.AddAssigned(701, "high");
        _zendesk.AddAssigned(702, "high");
        var watcher = await StartAsync();

        _zendesk.Reply(701, Customer, "Thanks a lot, that worked!");
        _zendesk.Reply(702, Customer, "Thanks, but it still fails on the second site.");
        await watcher.PollAsync(CancellationToken.None);
        var shown = TakeShown();
        Assert.True(Assert.Single(shown, c => c.TicketId == 701).Quiet);
        Assert.False(Assert.Single(shown, c => c.TicketId == 702).Quiet);
        Assert.Equal(ToastSound.Silent, _settings.SoundFor(shown.Single(c => c.TicketId == 701)));
        Assert.Equal(TicketDisplayTimes.For(_settings.TicketDisplayLength, "low"), _settings.DisplaySecondsFor(shown.Single(c => c.TicketId == 701)));
    }

    [Theory]
    [InlineData("Thanks, that fixed it!", true)]
    [InlineData("Thank you so much for the quick help", true)]
    [InlineData("All good now, you can close the ticket.", true)]
    [InlineData("Thanks! One more question: can we also export it?", false)]
    [InlineData("Thanks, but the second site still shows old content", false)]
    [InlineData("The site is down", false)]
    public void Thank_you_detection(string text, bool expected) => Assert.Equal(expected, PriorityGuess.IsThankYou(text));
}
