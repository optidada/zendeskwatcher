using ZendeskWatcher.Tracking;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>The parts of a check share one ticket read; a part that never asks doesn't hold the others up.</summary>
public class TicketBatchTests
{
    [Fact]
    public async Task Parts_get_their_own_tickets_from_one_read()
    {
        var reads = new List<List<long>>();
        var batch = new TicketBatch(ids => { reads.Add(ids); return Task.FromResult(ids.Where(id => id != 3).Select(id => new Ticket { Id = id }).ToList()); });
        using var a = batch.Join();
        using var b = batch.Join();

        var first = a.ReadAsync([1, 3]);
        Assert.False(first.IsCompleted); // waits for the other part
        var second = await b.ReadAsync([2, 1]);

        Assert.Equal([1L], (await first).Select(t => t.Id));   // 3 is gone (deleted or no access)
        Assert.Equal([2L, 1L], second.Select(t => t.Id));
        Assert.Single(reads);
    }

    [Fact]
    public async Task A_part_that_ends_without_asking_lets_the_others_go_on()
    {
        var batch = new TicketBatch(ids => Task.FromResult(ids.Select(id => new Ticket { Id = id }).ToList()));
        var asking = batch.Join();
        var failing = batch.Join();

        var tickets = asking.ReadAsync([7]);
        failing.Dispose(); // e.g. an error before it read anything

        Assert.Equal([7L], (await tickets).Select(t => t.Id));
    }
}
