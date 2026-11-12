namespace ZendeskWatcher.UI.Popups;

/// <summary>What a notification says. Each style (custom popup themes or Windows toasts) lays it out its own way.</summary>
/// <param name="Kind">Decides the sound and display settings that apply.</param>
/// <param name="Heading">Short label, e.g. "New ticket" or "Ticket updated".</param>
/// <param name="Title">Main line, e.g. "#123456 Customer cannot log in".</param>
/// <param name="Body">Details; may contain line breaks.</param>
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

    public string ResolvedAction => Action ?? (TicketId is null ? Notifier.ActionOpenZendesk : Notifier.ActionOpenTicket);
}
