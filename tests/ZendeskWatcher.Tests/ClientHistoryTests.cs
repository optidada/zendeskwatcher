namespace ZendeskWatcher.Tests;

/// <summary>The saved ticket records behind Clients and Products (cache\clients-digest.json).</summary>
public class ClientHistoryTests
{
    [Fact]
    public void Only_monitored_groups_are_kept_and_others_for_a_day()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        List<TicketDigest> One(long id) => [new TicketDigest(id, 4711, 2, old)];
        var digests = new Dictionary<long, GroupDigest>
        {
            [10] = new(old, One(1)),                      // monitored, read two days ago: kept
            [20] = new(old, One(2)),                      // no longer monitored, read two days ago: dropped
            [30] = new(DateTimeOffset.UtcNow.AddHours(-2), One(3)) // unticked today: kept for now
        };

        ClientHistory.Save("history-test", digests, new HashSet<long> { 10 });

        Assert.Equal([10L, 30L], digests.Keys.Order());
        Assert.Equal([10L, 30L], ClientHistory.Load("history-test").Keys.Order());
        ClientHistory.Save("history-test", digests); // without the monitored groups: nothing dropped
        Assert.Equal(2, ClientHistory.Load("history-test").Count);
    }
}
