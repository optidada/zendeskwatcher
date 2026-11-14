namespace ZendeskWatcher.UI.Popups;

/// <summary>What a notification says. Each style (custom popup themes or Windows toasts) lays it out its own way.</summary>
/// <param name="Kind">Decides the sound and display settings that apply.</param>
/// <param name="Heading">Short label, e.g. "New ticket" or "Ticket updated".</param>
/// <param name="Title">Main line, e.g. "#123456 Customer cannot log in".</param>
/// <param name="Body">Details; may contain line breaks and **bold** (see <see cref="UI.Markdown"/>; escape text from Zendesk).</param>
/// <param name="TicketId">The ticket to open on click, if any.</param>
/// <param name="Priority">Zendesk priority (low, normal, high, urgent), used for the accent color.</param>
/// <param name="Context">Where it comes from, e.g. the group name.</param>
public sealed record NotificationContent(
    NotificationKind Kind,
    string Heading,
    string Title,
    string Body = "",
    long? TicketId = null,
    string? Priority = null,
    string? Context = null)
{
    /// <summary>What a click does; defaults to opening the ticket, or Zendesk when there is no ticket.</summary>
    public string? Action { get; init; }

    /// <summary>When it happened (e.g. ticket creation). Shown as "· 5 min ago" after the body, kept current in popups.</summary>
    public DateTimeOffset? EventTime { get; init; }

    /// <summary>When the SLA answer is due (Notifications → SLA); shown in red bold above the body, kept current.</summary>
    public DateTimeOffset? SlaDue { get; init; }

    /// <summary>The SLA deadline is for the first reply (a new ticket), not a follow-up.</summary>
    public bool SlaFirstReply { get; init; }

    /// <summary>The body with the time since <see cref="EventTime"/> appended to its first line, under the SLA deadline.</summary>
    public string DisplayBody(DateTimeOffset now)
    {
        var body = Body;
        if (EventTime is { } at)
        {
            var ago = Ago(now - at);
            var newline = body.IndexOf('\n');
            body = newline < 0 ? $"{body} · {ago}" : $"{body[..newline]} · {ago}{body[newline..]}";
        }
        if (SlaDue is { } due)
            body = $"!!{Tracking.Sla.DueText(due, now, SlaFirstReply)}!!" + (body.Length > 0 ? "\n" + body : "");
        return body;
    }

    public static string Ago(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)span.TotalMinutes} min ago",
        < 180 when span.Minutes > 0 => $"{(int)span.TotalHours} h {span.Minutes} min ago",
        < 60 * 24 => $"{(int)span.TotalHours} h ago",
        _ => (int)span.TotalDays == 1 ? "1 day ago" : $"{(int)span.TotalDays} days ago"
    };

    /// <summary>When the newest change it reports happened (not shown): to check later whether someone has answered since.</summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>How many tickets it's about (more than 1 for "5 new unassigned tickets"); for the "while you were away" summary.</summary>
    public int TicketCount { get; init; } = 1;

    /// <summary>How many of those tickets are unassigned and Scheduled (a time is booked, nobody owns it); for the summaries.</summary>
    public int ScheduledCount { get; init; }

    /// <summary>Fixed sound and display time for app messages (updates, welcome, …), instead of the user's ticket styles.</summary>
    public NotificationStyle? Style { get; init; }

    /// <summary>How many body lines a popup shows before it cuts off (more for app summaries).</summary>
    public int MaxBodyLines { get; init; } = 5;

    /// <summary>Show a "Take it" button that assigns the (unassigned) ticket to the user and opens it.</summary>
    public bool CanTake { get; init; }

    /// <summary>Show a "Catch up" button (the start summary), in the place of "Take it".</summary>
    public bool CanCatchUp { get; init; }

    /// <summary>The user asked for it (Catch up): shown even while notifications are paused.</summary>
    public bool RequestedByUser { get; init; }

    /// <summary>About tickets (not an app message or sample): counted in the statistics.</summary>
    public bool IsTicketNotification { get; init; }

    /// <summary>A snoozed notification coming back (not counted as shown again).</summary>
    public bool IsSnoozedRepeat { get; init; }

    /// <summary>The customer's organization, shown prominently as "Client: Contoso". Null when the ticket has none.</summary>
    public string? Client { get; init; }

    /// <summary>The client's organization id (for the per-client statistics on Insights); null when the ticket has none.</summary>
    public long? ClientId { get; init; }

    /// <summary>The client's name alone (<see cref="Client"/> may add a label from Zendesk).</summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// The client the "Mute ▾" menu on the client line acts on (with its current choice), Clients page. Only on new
    /// tickets and customer replies on colleagues' tickets, the ones muting applies to; null otherwise.
    /// </summary>
    public ClientChoice? MuteTarget { get; init; }

    /// <summary>From an important client (Clients page): stays until dismissed, ★ in the heading.</summary>
    public bool Important { get; init; }

    /// <summary>The client's own sound (Clients page), instead of the sound of the notification's type.</summary>
    public ToastSound? Sound { get; init; }

    /// <summary>The priority for display, e.g. "High (P2)".</summary>
    public string? PriorityLabel => LabelForPriority(Priority);

    /// <summary>The priority in the heading: "P1" … "P4"; null for no priority.</summary>
    public string? PriorityShort => Priority?.ToLowerInvariant() switch
    {
        null or "" => null,
        "urgent" => "P1",
        "high" => "P2",
        "normal" => "P3",
        "low" => "P4",
        _ => LabelForPriority(Priority)
    };

    /// <summary>"New ticket · P2 · Support Tier 2": kind, priority and where it's from.</summary>
    public string FullHeading => string.Join(" · ", new[] { Heading, PriorityShort, Context }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>"urgent" → "Urgent (P1)", … ; null for no priority.</summary>
    public static string? LabelForPriority(string? priority) => priority?.ToLowerInvariant() switch
    {
        null or "" => null,
        "urgent" => "Urgent (P1)",
        "high" => "High (P2)",
        "normal" => "Normal (P3)",
        "low" => "Low (P4)",
        var other => char.ToUpperInvariant(other[0]) + other[1..]
    };

    public string ResolvedAction => Action ?? (TicketId is null ? NotificationActions.OpenZendesk : NotificationActions.OpenTicket);
}
