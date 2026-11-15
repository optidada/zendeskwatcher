using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tracking;

/// <summary>Why a notification glows at the screen edge (Look & sound → "Glow at the screen edge").</summary>
public enum GlowReason
{
    None,
    /// <summary>An Urgent (P1) ticket: the priority color.</summary>
    Urgent,
    /// <summary>The SLA is breached or breaches soon: the priority color.</summary>
    Sla,
    /// <summary>From an important client (Clients page): the notification's accent.</summary>
    ImportantClient
}

/// <summary>Which notifications get the screen-edge glow: only the ones that matter most.</summary>
public static class EdgeGlowRule
{
    /// <summary>An SLA deadline this close (or already past) counts as a breach.</summary>
    public static readonly TimeSpan SlaSoon = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The reason to glow for <paramref name="content"/>, or None: only ticket notifications (and samples from Try it),
    /// never app messages or quiet thank-yous. Urgent and SLA win over an important client (red says more).
    /// </summary>
    public static GlowReason For(NotificationContent content, DateTimeOffset now, bool isSample = false)
    {
        if (!content.IsTicketNotification && !isSample || content.Quiet)
            return GlowReason.None;
        if (string.Equals(content.Priority, "urgent", StringComparison.OrdinalIgnoreCase))
            return GlowReason.Urgent;
        if (content.SlaDue is { } due && due - now <= SlaSoon)
            return GlowReason.Sla;
        return content.Important ? GlowReason.ImportantClient : GlowReason.None;
    }
}
