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
    public async Task Three_new_tickets_that_read_alike_get_one_same_issue_notification()
    {
        _settings.SameIssueAlert = true;
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(201, subject: "Website down");
        _zendesk.AddUnassigned(202, subject: "Invoice for October");
        await watcher.PollAsync(CancellationToken.None);
        Assert.DoesNotContain(TakeShown(), c => c.Heading == "Same issue?");

        _zendesk.AddUnassigned(203, subject: "Our website is down");
        _zendesk.AddUnassigned(204, subject: "Site down for everyone");
        await watcher.PollAsync(CancellationToken.None);
        var alert = Assert.Single(TakeShown(), c => c.Heading == "Same issue?");
        Assert.Equal("3 new tickets read alike", alert.Title);
        Assert.Contains("#201", alert.Body);
        Assert.DoesNotContain("#202", alert.Body);
        Assert.Equal(3, alert.TicketCount);

        // A fourth one joins the same group: no second alert.
        _zendesk.AddUnassigned(205, subject: "Website down again");
        await watcher.PollAsync(CancellationToken.None);
        Assert.DoesNotContain(TakeShown(), c => c.Heading == "Same issue?");
    }

    [Fact]
    public async Task Without_the_switch_there_is_no_same_issue_notification()
    {
        var watcher = await StartAsync();
        foreach (var (id, subject) in new[] { (301L, "Website down"), (302L, "Our website is down"), (303L, "Site down for everyone") })
            _zendesk.AddUnassigned(id, subject: subject);
        await watcher.PollAsync(CancellationToken.None);
        Assert.DoesNotContain(TakeShown(), c => c.Heading == "Same issue?");
    }

    [Fact]
    public async Task New_ticket_shows_a_similar_solved_ticket_and_fits_you()
    {
        _settings.ShowSimilarTickets = true;
        _settings.MarkFitsYou = true;
        Solved(401, "Media library images broken", Jane);
        Solved(402, "Images missing in the media library", _zendesk.Me.Id);
        Solved(403, "Media library does not show images", _zendesk.Me.Id);
        Solved(404, "Invoice question", Jane);
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(410, subject: "Images not showing in media library");
        await watcher.PollAsync(CancellationToken.None);
        var shown = Assert.Single(TakeShown());
        var lines = shown.Body.Split('\n');
        Assert.StartsWith("Like #40", lines[1]);
        Assert.Contains("solved by", lines[1]);
        Assert.Equal("**Fits you:** like 2 tickets you solved", lines[2]);
    }

    [Fact]
    public async Task Unrelated_new_ticket_gets_no_similar_line()
    {
        _settings.ShowSimilarTickets = true;
        Solved(501, "Media library images broken", Jane);
        var watcher = await StartAsync();

        _zendesk.AddUnassigned(510, subject: "Change the invoice address");
        await watcher.PollAsync(CancellationToken.None);
        Assert.Single(Assert.Single(TakeShown()).Body.Split('\n'));
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
        Assert.Equal(_settings.TicketDisplay.For("low"), _settings.DisplaySecondsFor(shown.Single(c => c.TicketId == 701)));
    }

    [Theory]
    [InlineData("Thanks, that fixed it!", true)]
    [InlineData("Thank you so much for the quick help", true)]
    [InlineData("All good now, you can close the ticket.", true)]
    [InlineData("Thanks! One more question: can we also export it?", false)]
    [InlineData("Thanks, but the second site still shows old content", false)]
    [InlineData("The site is down", false)]
    public void Thank_you_detection(string text, bool expected) => Assert.Equal(expected, PriorityGuess.IsThankYou(text));

    [Fact]
    public void Rising_topics_compare_this_week_with_the_weeks_before()
    {
        var now = DateTimeOffset.UtcNow;
        var tickets = new List<IndexedTicket>();
        var id = 1;
        foreach (var subject in new[] { "Search not returning pages", "Search index stale", "Search results empty", "Search broken", "Invoice" })
            tickets.Add(new IndexedTicket(id++, subject, null, false, now.AddDays(-2)));
        for (var week = 2; week < 12; week++)
            tickets.Add(new IndexedTicket(id++, week % 5 == 0 ? "Search question" : "Invoice copy", null, true, now.AddDays(-7 * week)));

        var topics = new TicketIndex(tickets, _ => [0f]).RisingTopics(now);

        var search = Assert.Single(topics);
        Assert.Equal(("search", 4), (search.Word, search.ThisWeek));
        Assert.StartsWith("search (4 this week, usually none)", TopicsStatus.Text(topics));
    }
}
