using System.Diagnostics;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>
/// Zendesk's "too many requests" (429): the whole app pauses for the time Zendesk asks, not only the request that got
/// it, so the app never adds to a busy account.
/// </summary>
public class RateLimitTests
{
    [Fact]
    public async Task A_rate_limit_pauses_every_request_until_Zendesk_says()
    {
        var zendesk = new FakeZendesk();
        zendesk.NextAnswers.Enqueue(new ApiResponse(429, "", TimeSpan.FromSeconds(1)));
        var settings = new Settings { Subdomain = "test" };
        var clock = Stopwatch.StartNew();

        var groups = new ZendeskClient(settings, zendesk).GetGroupsAsync(CancellationToken.None); // gets the 429
        var me = await new ZendeskClient(settings, zendesk).GetMeAsync(CancellationToken.None);   // another client: waits too

        Assert.True(clock.ElapsedMilliseconds >= 900, $"sent after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(zendesk.Me.Id, me.Id);
        Assert.Single(await groups);                                // tried again after the pause
        Assert.Equal(3, zendesk.Requested.Count);                   // the limited one, its retry, and the other
    }

    [Fact]
    public void The_apps_own_limit_lets_60_requests_a_minute_through_and_makes_the_next_wait()
    {
        var recent = new Queue<DateTimeOffset>();
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < ZendeskClient.LocalLimitPerMinute; i++)
            Assert.Null(ZendeskClient.TakeLocalSlot(recent, start.AddSeconds(i * 0.5), ZendeskClient.LocalLimitPerMinute)); // all 60 go

        var now = start.AddSeconds(31);
        var wait = ZendeskClient.TakeLocalSlot(recent, now, ZendeskClient.LocalLimitPerMinute);
        Assert.NotNull(wait);                                         // the 61st waits...
        Assert.InRange(wait!.Value.TotalSeconds, 29, 30);             // ...until the first is a minute old
        Assert.Equal(ZendeskClient.LocalLimitPerMinute, recent.Count); // and took no place

        Assert.Null(ZendeskClient.TakeLocalSlot(recent, start.AddSeconds(60.1), ZendeskClient.LocalLimitPerMinute)); // then it goes
    }

    [Fact]
    public void A_click_doesnt_wait_behind_the_background_checks()
    {
        var recent = new Queue<DateTimeOffset>();
        var now = DateTimeOffset.UtcNow;
        const int plain = ZendeskClient.LocalLimitPerMinute, click = ZendeskClient.LocalLimitPerMinute + ZendeskClient.ClickHeadroom;
        for (var i = 0; i < plain; i++)
            Assert.Null(ZendeskClient.TakeLocalSlot(recent, now, plain)); // the checks used the whole minute

        Assert.NotNull(ZendeskClient.TakeLocalSlot(recent, now, plain));  // another check waits
        for (var i = 0; i < ZendeskClient.ClickHeadroom; i++)
            Assert.Null(ZendeskClient.TakeLocalSlot(recent, now, click)); // Take it ▾ goes at once
        Assert.NotNull(ZendeskClient.TakeLocalSlot(recent, now, click));  // only a runaway of clicks waits
    }

    [Fact]
    public async Task A_Zendesk_hiccup_on_a_read_is_tried_again_instead_of_failing()
    {
        var zendesk = new FakeZendesk();
        zendesk.NextAnswers.Enqueue(new ApiResponse(503, """{"error":{"title":"DatabaseError","message":"Database timeout error"}}""", null));
        var settings = new Settings { Subdomain = "test" };

        var me = await new ZendeskClient(settings, zendesk).GetMeAsync(CancellationToken.None);

        Assert.Equal(zendesk.Me.Id, me.Id);
        Assert.Equal(2, zendesk.Requested.Count); // the 503 and the retry 3 s later
    }

    [Fact]
    public async Task A_read_that_keeps_failing_gives_up_after_two_retries()
    {
        var zendesk = new FakeZendesk();
        for (var i = 0; i < 3; i++)
            zendesk.NextAnswers.Enqueue(new ApiResponse(503, "{}", null));
        var settings = new Settings { Subdomain = "test" };

        await Assert.ThrowsAsync<HttpRequestException>(() => new ZendeskClient(settings, zendesk).GetMeAsync(CancellationToken.None));
        Assert.Equal(3, zendesk.Requested.Count);
    }
}
