namespace ZendeskWatcher;

/// <summary>
/// Suggestions from the notification statistics: is a kind of notification ignored, closed right away, snoozed a
/// lot, or just too frequent? Plain rules with thresholds, so the advice is predictable (and tested).
/// </summary>
public static class StatsInsights
{
    /// <summary>A kind needs this many notifications in the period before it's judged.</summary>
    public const int MinPerKind = 10;

    /// <summary>More than this many per working day (with notifications) is "a lot".</summary>
    public const int BusyPerDay = 40;

    public enum Tone { Good, Hint, Warning }

    public sealed record Insight(Tone Tone, string Text);

    private static readonly (NotificationKind Kind, string Name)[] Kinds =
    [
        (NotificationKind.NewTicket, "new ticket"), (NotificationKind.MyTicket, "my ticket"),
        (NotificationKind.CcTicket, "CC / following"), (NotificationKind.HelpColleague, "colleague")
    ];

    /// <param name="totals">The last 30 days (see <see cref="NotificationStats.Totals"/>).</param>
    /// <param name="activeDays">Days with at least one notification in that period.</param>
    /// <param name="settings">The current settings, to suggest the right change.</param>
    public static List<Insight> Analyze(IReadOnlyDictionary<(NotificationKind Kind, StatOutcome Outcome), int> totals, int activeDays, Settings settings)
    {
        int Count(NotificationKind kind, StatOutcome outcome) => outcome == StatOutcome.OpenedTicket
            ? totals.GetValueOrDefault((kind, StatOutcome.OpenedTicket)) + totals.GetValueOrDefault((kind, StatOutcome.OpenedZendesk))
            : totals.GetValueOrDefault((kind, outcome));

        var insights = new List<Insight>();
        var total = Kinds.Sum(k => Count(k.Kind, StatOutcome.Shown));
        if (Kinds.All(k => Count(k.Kind, StatOutcome.Shown) < MinPerKind))
        {
            insights.Add(new Insight(Tone.Hint, total == 0
                ? "No suggestions yet: there haven't been any ticket notifications."
                : $"Not enough notifications yet for suggestions ({total} so far; about {MinPerKind} of one kind are needed)."));
            return insights;
        }

        // Too many overall?
        if (activeDays > 0 && total / (double)activeDays > BusyPerDay)
        {
            var busiest = Kinds.MaxBy(k => Count(k.Kind, StatOutcome.Shown));
            insights.Add(new Insight(Tone.Warning,
                $"About {Math.Round(total / (double)activeDays)} notifications per working day, mostly {busiest.Name} notifications. " +
                Fewer(busiest.Kind, settings)));
        }

        foreach (var (kind, name) in Kinds)
        {
            var shown = Count(kind, StatOutcome.Shown);
            if (shown < MinPerKind)
                continue;
            double Share(StatOutcome outcome) => Count(kind, outcome) / (double)shown;
            var acted = Share(StatOutcome.OpenedTicket) + Share(StatOutcome.Took);
            var timedOut = Share(StatOutcome.TimedOut);
            var closed = Share(StatOutcome.Closed);
            var snoozed = Share(StatOutcome.Snoozed);
            var capitalized = char.ToUpperInvariant(name[0]) + name[1..];

            if (timedOut >= 0.6)
            {
                var shortDisplay = settings.StyleFor(kind).Display is ToastDisplay.Standard or ToastDisplay.Medium;
                insights.Add(new Insight(Tone.Warning, $"{Percent(timedOut)} of {name} notifications disappeared unread. " +
                    (shortDisplay ? "Show them longer (Look & sound → Sound and display). " : "") + Fewer(kind, settings)));
            }
            else if (closed >= 0.5)
                insights.Add(new Insight(Tone.Warning, $"You closed {Percent(closed)} of {name} notifications without opening them: " +
                    $"they may not be useful. {Fewer(kind, settings)}"));
            else if (snoozed >= 0.3)
                insights.Add(new Insight(Tone.Hint, $"You snoozed {Percent(snoozed)} of {name} notifications. " +
                    "A longer snooze time (Notifications → Snooze) may suit you better."));
            else if (acted >= 0.5)
                insights.Add(new Insight(Tone.Good, $"{capitalized} notifications work well: you opened or took {Percent(acted)} of them."));
        }

        return insights;
    }

    /// <summary>How to get fewer notifications of a kind, with where to change it.</summary>
    private static string Fewer(NotificationKind kind, Settings settings) => kind switch
    {
        NotificationKind.NewTicket => "Monitoring fewer groups (Groups) gives fewer.",
        NotificationKind.MyTicket => settings.MyTicketPriorities.Count == Settings.AllPriorities.Length
            ? "Fewer priorities or change types (Notifications → Ticket changes) give fewer."
            : "Fewer change types (Notifications → Which changes) give fewer, e.g. without status changes.",
        NotificationKind.CcTicket => settings.CcTicketPriorities.Count > 1
            ? "Only Urgent for CC / following tickets (Notifications → Ticket changes) gives fewer."
            : "Turning off CC / following tickets (Notifications → Ticket changes) stops them.",
        _ => "Fewer priorities or groups (Help colleagues page) give fewer."
    };

    private static string Percent(double share) => $"{Math.Round(share * 100)}%";
}
