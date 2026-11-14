using System.Text.RegularExpressions;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>Words for the topics feature: lower case, 3+ letters, no common words, simple endings removed.</summary>
public static partial class TextMatch
{
    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "with", "not", "are", "was", "were", "has", "have", "had", "our", "your", "you", "this", "that",
        "from", "but", "can", "cannot", "can't", "after", "when", "what", "how", "why", "all", "any", "some", "into", "out", "its",
        "it's", "there", "their", "they", "them", "been", "being", "will", "would", "could", "should", "does", "did", "doesn't",
        "don't", "isn't", "aren't", "wasn't", "won't", "now", "also", "just", "only", "more", "very", "too", "via", "per",
        "issue", "issues", "problem", "problems", "question", "help", "please", "hello", "thanks", "thank", "regards", "need",
        "request", "ticket", "re", "fw", "fwd", "able", "get", "getting", "see", "seeing", "use", "using", "one", "new", "still",
        "again", "since", "urgent", "asap", "error", "errors", "working", "work", "works", "not", "unable", "about", "regarding"
    ];

    public static List<string> Words(string? text)
    {
        var words = new List<string>();
        foreach (Match m in Word().Matches((text ?? "").ToLowerInvariant()))
        {
            var w = m.Value.Trim('\'', '-');
            if (w.Length < 3 || StopWords.Contains(w) || w.All(char.IsDigit))
                continue;
            words.Add(Stem(w));
        }
        return words;
    }

    /// <summary>"publishing", "published", "publishes" → "publish"; "searches" → "search".</summary>
    internal static string Stem(string w)
    {
        foreach (var ending in new[] { "ing", "ed", "es", "s" })
            if (w.Length > ending.Length + 3 && w.EndsWith(ending, StringComparison.Ordinal) && !w.EndsWith("ss", StringComparison.Ordinal))
                return w[..^ending.Length];
        return w;
    }

    [GeneratedRegex(@"[a-z][a-z0-9'\-]*")]
    internal static partial Regex Word();
}

/// <summary>A ticket remembered for the Experimental features: only what they need, kept in memory, never saved.</summary>
public sealed record IndexedTicket(long Id, string Subject, long? AssigneeId, bool Solved, DateTimeOffset CreatedAt, string? Priority = null)
{
    public static IndexedTicket From(Ticket t) =>
        new(t.Id, TextCleanup.Plain(t.Subject), t.AssigneeId, t.Status is "solved" or "closed", t.CreatedAt, t.Priority);
}

/// <summary>A word that came up more than usual this week: "search", 8 tickets, usually 2.</summary>
public sealed record Topic(string Word, int ThisWeek, double UsualPerWeek);

/// <summary>
/// Recent tickets (90 days of the monitored groups, and the ones I solved) with their meaning from the language model
/// (<see cref="Embeddings"/>), for similar tickets, "fits you", the priority guess and topics. In memory only.
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

    /// <summary>
    /// Words in this week's subjects that come up at least 3 times and at least twice as often as in the weeks before.
    /// </summary>
    public List<Topic> RisingTopics(DateTimeOffset now, int max = 5)
    {
        var weekStart = now.AddDays(-7);
        var words = _entries.Select(e => (e.Ticket.CreatedAt, Words: TextMatch.Words(e.Ticket.Subject).ToHashSet())).ToList();
        var recent = words.Where(e => e.CreatedAt >= weekStart).ToList();
        var before = words.Where(e => e.CreatedAt < weekStart).ToList();
        if (recent.Count == 0)
            return [];
        var weeksBefore = before.Count == 0 ? 1 : Math.Max(1, (weekStart - before.Min(e => e.CreatedAt)).TotalDays / 7);

        var surface = new Dictionary<string, Dictionary<string, int>>(); // stem → the words as written, to show a real one
        foreach (var e in _entries.Where(e => e.Ticket.CreatedAt >= weekStart))
            foreach (Match m in TextMatch.Word().Matches(e.Ticket.Subject.ToLowerInvariant()))
            {
                if (TextMatch.Words(m.Value).FirstOrDefault() is not { } stem)
                    continue;
                var forms = surface.TryGetValue(stem, out var f) ? f : surface[stem] = [];
                forms[m.Value] = forms.GetValueOrDefault(m.Value) + 1;
            }

        return surface.Keys
            .Select(word => (Word: word, Week: recent.Count(e => e.Words.Contains(word)), Usual: before.Count(e => e.Words.Contains(word)) / weeksBefore))
            .Where(x => x.Week >= 3 && x.Week >= 2 * Math.Max(x.Usual, 1))
            .OrderByDescending(x => x.Week - x.Usual).ThenBy(x => x.Word)
            .Take(max)
            .Select(x => new Topic(surface[x.Word].OrderByDescending(kv => kv.Value).First().Key, x.Week, Math.Round(x.Usual, 1)))
            .ToList();
    }
}

/// <summary>What the topics feature last found, for the Experimental page (shared like <see cref="WatchStatus"/>).</summary>
public static class TopicsStatus
{
    public static IReadOnlyList<Topic> Current { get; private set; } = [];
    public static DateTime? UpdatedAt { get; private set; }
    public static int TicketsLookedAt { get; private set; }

    public static void Update(IReadOnlyList<Topic> topics, int tickets)
    {
        Current = topics;
        TicketsLookedAt = tickets;
        UpdatedAt = DateTime.Now;
    }

    /// <summary>"search (8 this week, usually 2) · login (5, usually 1)"; empty when nothing stands out.</summary>
    public static string Text(IReadOnlyList<Topic> topics) => string.Join(" · ", topics.Select((t, i) =>
        i == 0 ? $"{t.Word} ({t.ThisWeek} this week, usually {Usual(t.UsualPerWeek)})" : $"{t.Word} ({t.ThisWeek}, usually {Usual(t.UsualPerWeek)})"));

    private static string Usual(double perWeek) => perWeek < 0.5 ? "none" : $"{perWeek:0.#}";
}
