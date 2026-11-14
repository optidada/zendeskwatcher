using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The Experimental page's features: same-issue alert, similar solved tickets, "fits you", quiet thank-yous, topics,
/// and the language model's part of the priority guess. All on the PC; the 90 days of tickets stay in memory.
/// </summary>
public sealed partial class TicketWatcher
{
    /// <summary>How far back the ticket history goes, and how often it's read again.</summary>
    private const int HistoryDays = 90;
    private static readonly TimeSpan HistoryRefresh = TimeSpan.FromHours(6);

    /// <summary>New tickets within this long of each other can be "the same issue"; it takes this many.</summary>
    private static readonly TimeSpan SameIssueWindow = TimeSpan.FromHours(1);
    private const int SameIssueCount = 3;

    private TicketIndex? _history;
    private DateTimeOffset _historyAt;
    private Task? _historyLoad;

    /// <summary>New tickets of the last hour or so, with their meaning, for the same-issue alert.</summary>
    private readonly List<(Ticket Ticket, WatchedGroup Group, float[] Meaning)> _recentNew = [];
    private readonly HashSet<long> _sameIssueShown = [];

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
            if (_history is not null || _recentNew.Count > 0)
            {
                _history = null;
                _recentNew.Clear();
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
            if (settings.MarkFitsYou)
                tickets.AddRange(await zendesk.GetMySolvedTicketsAsync(HistoryDays, ct));
            if (Model is not { } model)
                return;
            _history = new TicketIndex(tickets.Select(IndexedTicket.From), model.Embed);
            UpdateTopics();
            Log.Info($"Ticket history for the Experimental features: {_history.Count} tickets of {HistoryDays} days in {clock.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Could not read the ticket history for the Experimental features", ex);
        }
    }

    private void UpdateTopics()
    {
        if (settings.ShowTopics && _history is not null)
            TopicsStatus.Update(_history.RisingTopics(DateTimeOffset.UtcNow), _history.Count);
    }

    /// <summary>
    /// "Like #123 … · solved by Jane" and "Fits you: like 3 tickets you solved", as extra body lines (escaped); empty
    /// when off, when the history isn't read yet, or nothing is close enough.
    /// </summary>
    private async Task<List<string>> SimilarLinesAsync(Ticket ticket, CancellationToken ct)
    {
        var lines = new List<string>();
        if (_history is not { } history || string.IsNullOrWhiteSpace(ticket.Subject))
            return lines;
        var subject = TextCleanup.Plain(ticket.Subject);
        if (settings.ShowSimilarTickets &&
            history.Like(subject, t => t.Solved && t.Id != ticket.Id, TicketIndex.Similar, max: 1) is [var (best, _)])
        {
            var by = best.AssigneeId == Me?.Id ? "you" : best.AssigneeId is { } id ? await zendesk.GetUserNameAsync(id, ct) : null;
            lines.Add($"Like #{best.Id} {Markdown.Escape(Shorten(best.Subject, 50))}{(by is null ? "" : $" · solved by {Markdown.Escape(by)}")}");
        }
        if (settings.MarkFitsYou && Me is { } me)
        {
            var mine = history.Like(subject, t => t.Solved && t.AssigneeId == me.Id && t.Id != ticket.Id, TicketIndex.SameIssue, max: 20).Count;
            if (mine >= 2)
                lines.Add($"**Fits you:** like {mine} tickets you solved");
        }
        return lines;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>The priorities of the most similar earlier tickets agree on urgent or high (for the priority guess).</summary>
    private (string Priority, int Count)? LikeEarlier(Ticket ticket)
    {
        if (_history is not { } history || string.IsNullOrWhiteSpace(ticket.Subject))
            return null;
        var neighbours = history.Like(TextCleanup.Plain(ticket.Subject), t => t.Id != ticket.Id, TicketIndex.Similar, max: 5);
        return PriorityGuess.LikeEarlier(neighbours.Select(n => n.Ticket.Priority).ToList());
    }

    /// <summary>
    /// New tickets just found: when one makes 3 or more within an hour that read alike, one "Same issue?"
    /// notification lists them (once per group of tickets; a fourth that joins later doesn't repeat it).
    /// </summary>
    private void CheckSameIssue(IReadOnlyList<(Ticket Ticket, WatchedGroup Group)> fresh)
    {
        if (!settings.SameIssueAlert || Model is not { } model)
            return;
        var now = DateTimeOffset.UtcNow;
        _recentNew.RemoveAll(r => now - r.Ticket.CreatedAt > SameIssueWindow * 3);
        foreach (var (ticket, group) in fresh)
            if (_recentNew.All(r => r.Ticket.Id != ticket.Id))
                _recentNew.Add((ticket, group, model.Embed(TextCleanup.Plain(ticket.Subject))));

        foreach (var (ticket, _) in fresh)
        {
            var me = _recentNew.FirstOrDefault(r => r.Ticket.Id == ticket.Id);
            if (me.Meaning is null || _sameIssueShown.Contains(ticket.Id))
                continue;
            var alike = _recentNew
                .Where(r => Math.Abs((r.Ticket.CreatedAt - ticket.CreatedAt).TotalMinutes) <= SameIssueWindow.TotalMinutes
                            && (r.Ticket.Id == ticket.Id || Embeddings.Similarity(r.Meaning, me.Meaning) >= TicketIndex.SameIssue))
                .OrderBy(r => r.Ticket.CreatedAt).ToList();
            if (alike.Count < SameIssueCount)
                continue;
            if (alike.Any(r => _sameIssueShown.Contains(r.Ticket.Id)))
            {
                _sameIssueShown.Add(ticket.Id); // joins a group already reported: no second alert
                continue;
            }
            foreach (var r in alike)
                _sameIssueShown.Add(r.Ticket.Id);
            var minutes = Math.Max(1, (int)(alike[^1].Ticket.CreatedAt - alike[0].Ticket.CreatedAt).TotalMinutes);
            var tickets = alike.Select(r => r.Ticket).ToList();
            var groups = alike.Select(r => GroupLabel(r.Group)).Distinct().ToList();
            Log.Info($"Same issue? {tickets.Count} new tickets within {minutes} min read alike: {string.Join(",", tickets.Select(t => t.Id))}");
            Show(new NotificationContent(NotificationKind.NewTicket, "Same issue?", $"{tickets.Count} new tickets read alike",
                $"Within {minutes} min · maybe an incident\n{TicketList(tickets)}",
                Priority: tickets.OrderByDescending(t => PriorityRank(t.Priority)).First().Priority,
                Context: groups.Count == 1 ? groups[0] : null)
            { IsTicketNotification = true, TicketCount = tickets.Count, MaxBodyLines = ListLines + 1, EventTime = null },
                tickets.Select(t => t.Id));
        }
        _sameIssueShown.RemoveWhere(id => _recentNew.All(r => r.Ticket.Id != id));
    }

    private static int PriorityRank(string? priority) => priority switch { "urgent" => 3, "high" => 2, "low" => 0, _ => 1 };

    /// <summary>The customer's reply is just a thank-you, and quiet thank-yous are on.</summary>
    private bool IsQuietThankYou(AuditEvent comment) =>
        settings.QuietThankYous && PriorityGuess.IsThankYou(TextCleanup.Plain(comment.PlainBody ?? comment.Body));
}
