using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// One request instead of several: the parts of a check (new tickets, my tickets, CC'd tickets) each re-read some
/// tickets as they are now (show_many). Each part asks through its own <see cref="Part"/>; the tickets are read once
/// every part has asked or finished (a part that ends without asking, e.g. on an error, never holds the others up).
/// </summary>
public sealed class TicketBatch
{
    private readonly Func<List<long>, Task<List<Ticket>>> _read;
    private readonly HashSet<long> _ids = [];
    private readonly TaskCompletionSource<Dictionary<long, Ticket>> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private int _open;

    /// <param name="read">Reads tickets by id (only called with at least one id).</param>
    public TicketBatch(Func<List<long>, Task<List<Ticket>>> read) => _read = read;

    /// <summary>A part that may ask for tickets: call <see cref="Part.ReadAsync"/> at most once, and dispose it when the part is done.</summary>
    public Part Join()
    {
        lock (_lock)
            _open++;
        return new Part(this);
    }

    private void Close(IEnumerable<long>? ids)
    {
        bool fire;
        lock (_lock)
        {
            if (ids is not null)
                _ids.UnionWith(ids);
            fire = --_open == 0;
        }
        if (fire)
            _ = FireAsync();
    }

    private async Task FireAsync()
    {
        try
        {
            var tickets = _ids.Count == 0 ? [] : await _read(_ids.ToList());
            _result.TrySetResult(tickets.DistinctBy(t => t.Id).ToDictionary(t => t.Id));
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
        }
    }

    public sealed class Part : IDisposable
    {
        private readonly TicketBatch _batch;
        private bool _closed;

        internal Part(TicketBatch batch) => _batch = batch;

        /// <summary>The tickets as they are now (missing: deleted, or no longer visible to you).</summary>
        public async Task<List<Ticket>> ReadAsync(IReadOnlyCollection<long> ids)
        {
            if (_closed)
                throw new InvalidOperationException("A part reads its tickets once per check.");
            _closed = true;
            _batch.Close(ids);
            var all = await _batch._result.Task;
            return ids.Distinct().Where(all.ContainsKey).Select(id => all[id]).ToList();
        }

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            _batch.Close(null);
        }
    }
}
