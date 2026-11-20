namespace ZendeskWatcher.UI.Popups;

/// <summary>What a notification says. Each style (popup theme) lays it out its own way.</summary>
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
    /// <summary>
    /// How it's named in the log: kind and ticket number, never the subject or text (the log goes along with feedback);
    /// app messages and lists by their title ("7 unassigned tickets already waiting").
    /// </summary>
    public string LogName => TicketId is { } id ? $"{Kind} #{id}" : $"{Kind} \"{Title}\"";

    /// <summary>What a click does; defaults to opening the ticket, or Zendesk when there is no ticket.</summary>
    public string? Action { get; init; }

    /// <summary>When it happened (e.g. ticket creation). Shown as "· 5 min ago" after the body, kept current in popups.</summary>
    public DateTimeOffset? EventTime { get; init; }

    /// <summary>When the SLA answer is due (Notifications → SLA); shown in red bold above the body, kept current.</summary>
    public DateTimeOffset? SlaDue { get; init; }

    /// <summary>The SLA deadline is for the first reply (a new ticket), not a follow-up.</summary>
    public bool SlaFirstReply { get; init; }

    /// <summary>The experimental priority guess ("Set as [P3] · looks like [P1]: site down"), shown under the SLA deadline. Text only.</summary>
    public Tracking.PriorityHint? PriorityHint { get; init; }

    /// <summary>Just a thank-you from the customer (Experimental page): no sound, and only as long as Low (P4) ones stay.</summary>
    public bool Quiet { get; init; }

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
        // The SLA deadline at the right end of the first line ("Jane Doe · 2 min ago      Reply due in 3 h"; a tab: the
        // notification puts the rest of the line at the right edge, or on its own line when it doesn't fit), then the
        // priority guess, then the rest.
        var newlineAt = body.IndexOf('\n');
        var (first, rest) = newlineAt < 0 ? (body, "") : (body[..newlineAt], body[newlineAt..]);
        if (PriorityHint is { } hint)
            rest = "\n" + hint.Text + rest;
        if (SlaDue is { } due)
        {
            var sla = $"!!{Tracking.Sla.DueText(due, now, SlaFirstReply)}!!";
            first = first.Length > 0 ? $"{first}\t{sla}" : sla;
        }
        body = first + rest;
        return body.TrimStart('\n');
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

    /// <summary>
    /// The lines actually shown: a ticket notification is a preview (click to read the rest in Zendesk), so it keeps
    /// <see cref="MaxBodyLines"/>; anything else (Catch up, What's new, app messages) has nowhere else to read it, so it
    /// shows all of its text, up to 20 lines.
    /// </summary>
    public int BodyLines => IsTicketNotification ? MaxBodyLines : Math.Max(MaxBodyLines, 20);

    /// <summary>A list of tickets: each line stays one row (a long subject ends in "…"), so they don't run into each other.</summary>
    public bool OneRowPerLine { get; init; }

    /// <summary>Show a "Take it" button that assigns the (unassigned) ticket to the user and opens it.</summary>
    public bool CanTake { get; init; }

    /// <summary>A second choice next to the main button (e.g. "Always" next to "Cover today"): its label and action. Null: none.</summary>
    public (string Label, string Action)? SecondChoice { get; init; }


    /// <summary>The user asked for it (Catch up): shown even while notifications are paused.</summary>
    public bool RequestedByUser { get; init; }

    /// <summary>About tickets (not an app message or sample): counted in the statistics.</summary>
    public bool IsTicketNotification { get; init; }

    /// <summary>A sample from Try it (or --test-popups): may glow at the screen edge to show what that looks like.</summary>
    public bool IsSample { get; init; }

    /// <summary>A snoozed notification coming back (not counted as shown again).</summary>
    public bool IsSnoozedRepeat { get; init; }

    /// <summary>The same for a notification and its snoozed repeats (copies keep it): "acted on" counts once per notification.</summary>
    public Guid StatId { get; init; } = Guid.NewGuid();

    /// <summary>The customer's organization, shown prominently as "Client: Contoso". Null when the ticket has none.</summary>
    public string? Client { get; init; }

    /// <summary>The client's organization id (for the per-client statistics on Insights); null when the ticket has none.</summary>
    public long? ClientId { get; init; }

    /// <summary>The client's name alone (<see cref="Client"/> may add a label from Zendesk).</summary>
    public string? ClientName { get; init; }

    /// <summary>
    /// The client the "Options ▾" menu on the client line acts on (with its current choice), Clients page. Only on new
    /// tickets and customer replies on colleagues' tickets, the ones muting applies to; null otherwise.
    /// </summary>
    public ClientChoice? MuteTarget { get; init; }

    /// <summary>From an important client (Clients page): stays until dismissed, ★ in the heading.</summary>
    public bool Important { get; init; }

    /// <summary>The client's own sound (Clients page), instead of the sound of the notification's type.</summary>
    public ToastSound? Sound { get; init; }

    /// <summary>The priority for display, e.g. "P2".</summary>
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

    /// <summary>"urgent" → "P1", … (the team says P1–P4, never Urgent…); null for no priority.</summary>
    public static string? LabelForPriority(string? priority) => priority?.ToLowerInvariant() switch
    {
        null or "" => null,
        "urgent" => "P1",
        "high" => "P2",
        "normal" => "P3",
        "low" => "P4",
        var other => char.ToUpperInvariant(other[0]) + other[1..]
    };

    public string ResolvedAction => Action ?? (TicketId is null ? NotificationActions.OpenZendesk : NotificationActions.OpenTicket);
}
