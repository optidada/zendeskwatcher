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
}
