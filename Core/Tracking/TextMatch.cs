using System.Text.RegularExpressions;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>Words in a text (for the thank-you check).</summary>
public static partial class TextMatch
{
    [GeneratedRegex(@"[a-z][a-z0-9'\-]*")]
    internal static partial Regex Word();
}

/// <summary>A ticket remembered for the Experimental features: only what they need, kept in memory, never saved.</summary>
public sealed record IndexedTicket(long Id, string Subject, long? AssigneeId, bool Solved, DateTimeOffset CreatedAt, string? Priority = null)
{
    public static IndexedTicket From(Ticket t) =>
        new(t.Id, TextCleanup.Plain(t.Subject), t.AssigneeId, t.Status is "solved" or "closed", t.CreatedAt, t.Priority);
}

/// <summary>
/// Recent tickets (90 days of the monitored groups) with their meaning from the language model
/// (<see cref="Embeddings"/>), for similar tickets, the same-issue alert and the priority guess. In memory only.
/// </summary>
public sealed class TicketIndex
{
    /// <summary>Same issue, other words: from about here on (tuned on support subjects; unrelated ones score ≤ 0.15).</summary>
    public const float SameIssue = 0.40f;

    /// <summary>Close enough to name it as a similar ticket.</summary>
    public const float Similar = 0.50f;

    private readonly List<(IndexedTicket Ticket, float[] Meaning)> _entries;
    private readonly Func<string, float[]> _embed;

    public TicketIndex(IEnumerable<IndexedTicket> tickets, Func<string, float[]> embed)
    {
        _embed = embed;
        _entries = tickets.DistinctBy(t => t.Id).Select(t => (t, embed(t.Subject))).ToList();
    }

    public int Count => _entries.Count;

    public float[] Meaning(string text) => _embed(text);

    /// <summary>The indexed tickets most like <paramref name="text"/> (at least <paramref name="min"/>), best first.</summary>
    public List<(IndexedTicket Ticket, float Score)> Like(string? text, Func<IndexedTicket, bool> which, float min, int max = 5)
    {
        var query = _embed(text ?? "");
        return _entries.Where(e => which(e.Ticket))
            .Select(e => (e.Ticket, Score: Embeddings.Similarity(query, e.Meaning)))
            .Where(x => x.Score >= min)
            .OrderByDescending(x => x.Score).Take(max).ToList();
    }
}
