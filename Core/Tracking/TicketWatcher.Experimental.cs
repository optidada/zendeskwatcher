using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The Experimental page's features: quiet thank-yous, and the priority guess (rules, and the language model comparing a
/// new ticket with 90 days of earlier ones). All on the PC; the tickets stay in memory.
/// </summary>
public sealed partial class TicketWatcher
{
    /// <summary>How far back the ticket history goes, and how often it's read again.</summary>
    private const int HistoryDays = 90;
    private static readonly TimeSpan HistoryRefresh = TimeSpan.FromHours(6);

    private TicketIndex? _history;
    private DateTimeOffset _historyAt;
    private Task? _historyLoad;

    /// <summary>The language model, when a feature that needs it is on.</summary>
    private Embeddings? Model => settings.NeedsTicketHistory ? Embeddings.Shared : null;

    /// <summary>
    /// Starts reading the ticket history when a feature needs it and it's missing or 6 hours old (in the background: the
    /// check doesn't wait for it). With all features off, the history and the model are let go.
    /// </summary>
    private void KeepHistoryFresh(CancellationToken ct)
    {
        if (!settings.NeedsTicketHistory)
        {
            if (_history is not null)
            {
                _history = null;
                Embeddings.Unload();
                Log.Info("Experimental features off: ticket history and language model let go");
            }
            return;
        }
        if (_historyLoad is { IsCompleted: false } || (_history is not null && DateTimeOffset.UtcNow - _historyAt < HistoryRefresh))
            return;
        _historyAt = DateTimeOffset.UtcNow; // also after a failure: try again in 6 hours, not every check
        _historyLoad = LoadHistoryAsync(ct);
    }

    /// <summary>For tests: until the ticket history read in the background is done.</summary>
    internal Task HistoryLoaded => _historyLoad ?? Task.CompletedTask;

    private async Task LoadHistoryAsync(CancellationToken ct)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var tickets = new List<Ticket>();
            foreach (var group in settings.WatchedGroups)
                tickets.AddRange(await zendesk.GetRecentGroupTicketsAsync(group.Id, HistoryDays, ct));
            if (Model is not { } model)
                return;
            _history = new TicketIndex(tickets.Select(IndexedTicket.From), model.Embed);
            Log.Info($"Ticket history for the Experimental features: {_history.Count} tickets of {HistoryDays} days in {clock.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Could not read the ticket history for the Experimental features", ex);
        }
    }

    /// <summary>The priorities of the most similar earlier tickets agree on urgent or high (for the priority guess).</summary>
    private (string Priority, int Count)? LikeEarlier(Ticket ticket)
    {
        if (_history is not { } history || string.IsNullOrWhiteSpace(ticket.Subject))
            return null;
        var neighbours = history.Like(TextCleanup.Plain(ticket.Subject), t => t.Id != ticket.Id, TicketIndex.Similar, max: 5);
        return PriorityGuess.LikeEarlier(neighbours.Select(n => n.Ticket.Priority).ToList());
    }

    /// <summary>The customer's reply is just a thank-you, and quiet thank-yous are on.</summary>
    private bool IsQuietThankYou(AuditEvent comment) =>
        settings.QuietThankYous && PriorityGuess.IsThankYou(TextCleanup.Plain(comment.PlainBody ?? comment.Body));
}
