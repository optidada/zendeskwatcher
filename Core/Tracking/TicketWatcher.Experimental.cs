using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The Experimental page's features: quiet thank-yous, and the priority guess (rules, and the language model comparing a
/// new ticket with 90 days of earlier ones). All on the PC: the history (number, subject, date, priority; no ticket text)
/// is kept in the data folder for a week, topped up with the newest tickets every 6 hours.
/// </summary>
public sealed partial class TicketWatcher
{
    /// <summary>How far back the ticket history goes, and how often it's read again.</summary>
    private const int HistoryDays = 90;
    private static readonly TimeSpan HistoryRefresh = CacheAges.GuessHistoryTopUp;

    /// <summary>How long a history read in full is only topped up (new tickets) before it's read in full again, so
    /// priorities changed later on older tickets come in too.</summary>
    private static readonly TimeSpan HistoryFullRefresh = CacheAges.GuessHistoryFull;

    /// <summary>The history as kept on disk (cache\ticket-history.json).</summary>
    private sealed class SavedHistory
    {
        public string Key { get; set; } = "";
        public DateTimeOffset FullAt { get; set; }
        public DateTimeOffset ToppedUpAt { get; set; }
        public List<IndexedTicket> Tickets { get; set; } = [];
    }

    /// <summary>After the app starts, reading the history from Zendesk waits this long: the first check and the Catch up
    /// greeting go first, and Zendesk gets them one after the other rather than all at once.</summary>
    private static readonly TimeSpan HistoryStartDelay = TimeSpan.FromMinutes(1);

    /// <summary>When this app run started (a Settings save keeps the history, so it never waits again).</summary>
    private static readonly DateTime AppStartedAt = DateTime.UtcNow;

    /// <summary>Waits until <see cref="HistoryStartDelay"/> after the app started, before a read from Zendesk.</summary>
    private static async Task WaitForStartAsync(CancellationToken ct)
    {
        var wait = AppStartedAt + HistoryStartDelay - DateTime.UtcNow;
        if (wait > TimeSpan.Zero && !TestMode)
            await Task.Delay(wait, ct);
    }

    /// <summary>Tests don't wait for the start delay.</summary>
    internal static bool TestMode { get; set; }

    private static string HistoryFile => Path.Combine(AppPaths.DataDir, "cache", "ticket-history.json");

    private SavedHistory? LoadSavedHistory()
    {
        try
        {
            return File.Exists(HistoryFile) && System.Text.Json.JsonSerializer.Deserialize<SavedHistory>(File.ReadAllText(HistoryFile)) is { } saved
                   && saved.Key == HistoryKey ? saved : null;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the saved ticket history", ex);
            return null;
        }
    }

    private static void SaveHistory(SavedHistory history)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            File.WriteAllText(HistoryFile, System.Text.Json.JsonSerializer.Serialize(history));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the ticket history", ex);
        }
    }

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

    /// <summary>Which account and groups the history is of.</summary>
    private string HistoryKey => settings.Subdomain + ":" + string.Join(",", settings.WatchedGroups.Select(g => g.Id).Order());

    /// <summary>
    /// Keeps the ticket history of the watcher this one replaces (checking restarts after every save in Settings), when
    /// it's of the same account and groups: otherwise each save read 90 days of tickets again (about ten requests).
    /// </summary>
    public void TakeHistoryFrom(TicketWatcher? previous)
    {
        if (previous?._history is not { } history || previous.HistoryKey != HistoryKey || !settings.NeedsTicketHistory)
            return;
        _history = history;
        _historyAt = previous._historyAt;
    }

    /// <summary>For tests: until the ticket history read in the background is done.</summary>
    internal Task HistoryLoaded => _historyLoad ?? Task.CompletedTask;

    private async Task LoadHistoryAsync(CancellationToken ct)
    {
        ZendeskClient.Reason.Value = "ticket history";
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var now = DateTimeOffset.UtcNow;
            var saved = LoadSavedHistory();
            string how;
            if (saved is not null && now - saved.FullAt < HistoryFullRefresh)
            {
                if (now - saved.ToppedUpAt >= HistoryRefresh)
                {
                    // Only what was created since the last read (search goes by date: a day extra), newest wins.
                    await WaitForStartAsync(ct);
                    var days = (int)Math.Ceiling((now - saved.ToppedUpAt).TotalDays) + 1;
                    var fresh = new List<Ticket>();
                    foreach (var group in settings.WatchedGroups)
                        fresh.AddRange(await zendesk.GetRecentGroupTicketsAsync(group.Id, days, ct));
                    var oldest = now.AddDays(-HistoryDays);
                    saved.Tickets = [.. fresh.Select(IndexedTicket.From).Concat(saved.Tickets).DistinctBy(t => t.Id).Where(t => t.CreatedAt >= oldest)];
                    saved.ToppedUpAt = now;
                    SaveHistory(saved);
                    how = $"topped up with {fresh.Count} of the last {days} days";
                }
                else
                    how = "as saved";
            }
            else
            {
                await WaitForStartAsync(ct);
                var tickets = new List<Ticket>();
                foreach (var group in settings.WatchedGroups)
                    tickets.AddRange(await zendesk.GetRecentGroupTicketsAsync(group.Id, HistoryDays, ct));
                saved = new SavedHistory { Key = HistoryKey, FullAt = now, ToppedUpAt = now, Tickets = [.. tickets.Select(IndexedTicket.From)] };
                SaveHistory(saved);
                how = "read in full";
            }
            _historyAt = saved.ToppedUpAt; // the next top-up 6 hours after the last one, also after a restart
            if (Model is not { } model)
                return;
            _history = new TicketIndex(saved.Tickets, model.Embed);
            Log.Info($"Ticket history for the Experimental features: {_history.Count} tickets of {HistoryDays} days, {how}, in {clock.Elapsed.TotalSeconds:0.0}s");
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
