using System.Text.RegularExpressions;

namespace ZendeskWatcher.Tracking;

/// <summary>A guessed priority higher than the ticket's, with what made it look so ("site down", "frustrated tone").</summary>
public sealed record PriorityHint(string Priority, IReadOnlyList<string> Reasons)
{
    /// <summary>"P1" … "P3".</summary>
    public string Short => Priority switch { "urgent" => "P1", "high" => "P2", _ => "P3" };

    /// <summary>The line on the notification: "Looks like P1: site down · frustrated tone".</summary>
    public string Text => $"Looks like {Short}: {string.Join(" · ", Reasons)}";
}

/// <summary>
/// Experimental "true priority" guess (Experimental page): reads what the customer wrote and suggests a
/// higher priority when it sounds more urgent than the ticket says. English only. Runs on the PC: plain rules, no
/// service. Never changes anything in Zendesk; it only adds a line to the notification.
/// </summary>
public static partial class PriorityGuess
{
    private const double UrgentScore = 4;
    private const double HighScore = 2.5;

    /// <summary>A signal: its name on the notification, how much it counts, and the words that show it.</summary>
    private sealed record Signal(string Label, double Weight, Regex Pattern);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static Signal S(string label, double weight, string pattern) => new(label, weight, new Regex(pattern, Options));

    private static readonly Signal[] Signals =
    [
        S("site down", 4,
            @"\b(site|website|web ?site|production|prod|server|service|platform|everything|cms|checkout|portal|shop|store|application|app|api)\s+" +
            @"(is\s+|are\s+|went\s+|has\s+gone\s+|seems\s+(to\s+be\s+)?)?(completely\s+|totally\s+)?(down|offline|unavailable|unreachable)\b" +
            @"|\boutage\b|\bdown\s+for\s+(all|everyone|everybody)\b" +
            @"|\b(internal server error|service unavailable|bad gateway|gateway time-?out)\b|\b50[0234]\s+errors?\b"),
        S("security", 3,
            @"\bsecurity\s+(issue|incident|breach|problem|hole)\b|\bvulnerab\w*|\bbreach(ed)?\b|\bhacked\b|\bcompromised\b|\bmalware\b" +
            @"|\bdata\s+leak|\bleaked\b|\bexposed\s+(personal|customer|user)\s+data\b"),
        S("data loss", 3,
            @"\bdata\s+loss\b|\blost\s+(all\s+)?(our\s+)?(data|content)\b" +
            @"|\b(content|data|pages|orders)\s+(was|were|has\s+been|have\s+been|got)\s+(deleted|wiped|lost|removed)\b"),
        S("losing sales", 2.5,
            @"\b(losing|lost)\s+(money|revenue|sales|orders|business)\b|\b(revenue|sales)\s+(impact|loss)\b" +
            @"|\bcheckout\s+(is\s+)?(broken|failing|fails|not\s+working|doesn'?t\s+work)\b" +
            @"|\b(orders?|payments?)\s+(are\s+|is\s+)?(failing|not\s+going\s+through|not\s+working)\b|\bcan'?t\s+(take|place|accept)\s+orders\b"),
        S("everyone affected", 2,
            @"\b(all|every|none\s+of\s+(our|the))\s+(of\s+our\s+)?(users|customers|editors|visitors|orders|sites|pages|markets)\b" +
            @"|\b(no\s?one|nobody)\s+(can|is\s+able)\b|\bentire\s+(site|team|company)\b"),
        S("production", 1.5, @"\b(in|on)\s+(production|prod)\b|\blive\s+(site|environment)\b"),
        S("users blocked", 1.5,
            @"\b(cannot|can'?t|can\s+not|unable\s+to|not\s+able\s+to)\s+(log\s?in|sign\s?in|access|publish|deploy|check\s?out|place\s+orders?)\b"),
        S("deadline soon", 1.5,
            @"\b(go[- ]?live|launch(ing)?|release|campaign|deadline)\b.{0,60}\b(today|tonight|tomorrow|in\s+\d+\s+hours?)\b" +
            @"|\b(today|tonight|tomorrow)\b.{0,60}\b(go[- ]?live|launch(ing)?|deadline)\b"),
        S("says urgent", 1.5,
            @"\b(urgent(ly)?|asap|critical|emergency|immediately|blocker|blocking|showstopper|show-stopper|sev\s?1|severity\s+1)\b"),
        S("frustrated tone", 1.5,
            @"\b(unacceptable|ridiculous|frustrat\w+|disappointed|escalat\w+|third\s+time|again\s+and\s+again|any\s+update" +
            @"|still\s+(not|no|waiting|broken|happening|haven'?t)|no\s+(response|reply|answer|update)\s+(yet|since|for)" +
            @"|how\s+long|waiting\s+(for|since)\s+(days|hours|\d+))\b|!{2,}"),
    ];

    /// <summary>Calm words: no rush, a question. Removed before the signals are looked for ("not urgent" isn't urgent).</summary>
    [GeneratedRegex(@"\b(no\s+rush|not\s+(urgent|critical|a\s+blocker|blocking)|no\s+hurry|low\s+priority|when\s+you\s+(have|get)\s+(a\s+chance|time)" +
                    @"|whenever\s+(you|it'?s)\s+convenient)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Calm();

    /// <summary>The customer says it's sorted: no hint at all.</summary>
    [GeneratedRegex(@"\b(that\s+(worked|fixed\s+it|solved\s+it|did\s+the\s+trick)|works\s+now|(is\s+)?working\s+(again|now)|issue\s+(is\s+)?resolved" +
                    @"|(you\s+)?can\s+close|please\s+close|all\s+good\s+now|problem\s+solved)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Resolved();

    /// <summary>Where the quoted earlier mails start in a reply: only the new text counts.</summary>
    [GeneratedRegex(@"\bOn\s.{4,80}\swrote:|-----\s*Original Message\s*-----|\bFrom:\s.{3,80}\sSent:|^>|\n>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuoteStart();

    /// <summary>Two or more SHOUTED words (5+ capitals): counts as a frustrated tone.</summary>
    [GeneratedRegex(@"\b[A-Z]{5,}\b")]
    private static partial Regex Shouting();

    /// <summary>SLA types with tight targets: when the text already sounds urgent, these tip it over sooner.</summary>
    private static readonly HashSet<string> DemandingSla = ["sla15min", "slapremium", "slahighattention", "sla24x7"];

    /// <summary>
    /// The guess, or null when the ticket's own priority looks right (only higher guesses are shown).
    /// </summary>
    /// <param name="priority">The ticket's priority (none counts as normal).</param>
    /// <param name="subject">The ticket's subject.</param>
    /// <param name="customerTexts">What the customer wrote, oldest first: the first message and the latest replies.</param>
    /// <param name="slaType">The client's SLA type (<see cref="Sla.Parse"/>).</param>
    /// <param name="waited">How long the customer has been waiting for an answer (zero for a new ticket).</param>
    /// <param name="likeEarlier">The language model found this many similar earlier tickets that had this priority
    /// (urgent or high, see <see cref="LikeEarlier"/>); counts like a signal from the text.</param>
    public static PriorityHint? Guess(string? priority, string? subject, IReadOnlyList<string> customerTexts, string slaType, TimeSpan waited,
        (string Priority, int Count)? likeEarlier = null)
    {
        var current = Rank(priority);
        if (current >= Rank("urgent"))
            return null;
        var latest = customerTexts.Count > 0 ? NewText(customerTexts[^1]) : "";
        if (Resolved().IsMatch(latest))
            return null; // thanks, it works now

        var text = Calm().Replace(string.Join("\n", new[] { subject ?? "" }.Concat(customerTexts.Select(NewText))), " ");
        var found = Signals.Where(s => s.Pattern.IsMatch(text)).ToList();
        if (Shouting().Matches(text).Count >= 2 && !found.Any(s => s.Label == "frustrated tone"))
            found.Add(Signals.First(s => s.Label == "frustrated tone"));
        if (likeEarlier is var (earlier, count) && count >= 2)
            found.Add(new Signal($"like {count} earlier {(earlier == "urgent" ? "P1s" : "P2s")}", earlier == "urgent" ? 2 : 1.5, Never()));
        if (found.Count == 0)
            return null;

        var reasons = found.OrderByDescending(s => s.Weight).Select(s => s.Label).Take(3).ToList();
        var score = found.Sum(s => s.Weight);
        // Only on top of what the text says: a long wait or a demanding SLA alone isn't urgency.
        if (waited >= TimeSpan.FromHours(24))
        {
            score += 1;
            reasons.Add($"waited {(int)waited.TotalHours} h");
        }
        else if (waited >= TimeSpan.FromHours(4))
            score += 0.5;
        if (DemandingSla.Contains(slaType))
            score += 0.5;

        var guess = score >= UrgentScore ? "urgent" : score >= HighScore ? "high" : null;
        return guess is not null && Rank(guess) > current ? new PriorityHint(guess, reasons) : null;
    }

    /// <summary>
    /// From the most similar earlier tickets (the language model's neighbours, best first): when most of them were
    /// urgent, or urgent and high together, that priority and how many. Null when they don't agree.
    /// </summary>
    public static (string Priority, int Count)? LikeEarlier(IReadOnlyList<string?> neighbourPriorities)
    {
        if (neighbourPriorities.Count < 2)
            return null;
        var urgent = neighbourPriorities.Count(p => p == "urgent");
        var high = neighbourPriorities.Count(p => p is "urgent" or "high");
        if (urgent >= 2 && urgent * 2 > neighbourPriorities.Count)
            return ("urgent", urgent);
        if (high >= 2 && high * 2 > neighbourPriorities.Count)
            return ("high", high);
        return null;
    }

    /// <summary>
    /// Just a thank-you or "it works now": short, thanks the agent or says it's solved, asks nothing and has no "but".
    /// Such replies notify quietly (Experimental page) and Catch up leaves them out.
    /// </summary>
    public static bool IsThankYou(string? text)
    {
        var t = NewText(text ?? "");
        if (Resolved().IsMatch(t))
            return !Doubt().IsMatch(t) && !t.Contains('?');
        return Thanks().IsMatch(t) && !t.Contains('?') && !Doubt().IsMatch(t) && TextMatch.Word().Matches(t.ToLowerInvariant()).Count <= 40;
    }

    [GeneratedRegex(@"\b(thanks|thank\s+you|thx|ty|cheers|much\s+appreciated|appreciate\s+(it|your|the))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Thanks();

    [GeneratedRegex(@"\b(but|however|still|although|though|unfortunately|not|doesn'?t|isn'?t|can'?t|won'?t|error|issue|problem|again|another|also)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Doubt();

    [GeneratedRegex(@"(?!)")]
    private static partial Regex Never();

    /// <summary>low 0, normal (or none) 1, high 2, urgent 3.</summary>
    private static int Rank(string? priority) => priority?.ToLowerInvariant() switch
    {
        "urgent" => 3,
        "high" => 2,
        "low" => 0,
        _ => 1
    };

    /// <summary>The reply without the quoted earlier mails.</summary>
    private static string NewText(string text) =>
        QuoteStart().Match(text) is { Success: true, Index: > 0 } m ? text[..m.Index] : text;
}
