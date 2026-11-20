using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// "Very busy": keyboard or mouse in at least <see cref="ActiveSecondsNeeded"/> of the last 60 seconds, kept up for
/// <see cref="BusyAfter"/>; it ends after <see cref="CalmAfter"/> below <see cref="ActiveSecondsToStay"/> (lower, so a
/// short pause to read doesn't end it; stopping completely ends it after about 45 seconds). Fed once a second with
/// only whether there was any input in that second (never which keys or where). While busy, ticket notifications wait
/// (not Urgent ones, nor Important clients'), at most <see cref="MaxHold"/>.
/// </summary>
public sealed class BusyRule
{
    public const int WindowSeconds = 60;
    public const int ActiveSecondsNeeded = 50;
    public const int ActiveSecondsToStay = 35;
    public static readonly TimeSpan BusyAfter = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan CalmAfter = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(10);

    private readonly bool[] _window = new bool[WindowSeconds];
    private int _next;
    private int _filled;
    private DateTime? _denseSince;
    private DateTime? _calmSince;

    public bool Busy { get; private set; }

    /// <summary>Seconds with input in the last 60.</summary>
    public int ActiveSeconds { get; private set; }

    /// <summary>One second's sample. True when Busy changed.</summary>
    public bool Add(bool active, DateTime now)
    {
        if (_filled == WindowSeconds && _window[_next])
            ActiveSeconds--;
        _window[_next] = active;
        if (active)
            ActiveSeconds++;
        _next = (_next + 1) % WindowSeconds;
        _filled = Math.Min(_filled + 1, WindowSeconds);

        var was = Busy;
        if (ActiveSeconds >= ActiveSecondsNeeded)
        {
            _denseSince ??= now;
            if (!Busy && now - _denseSince >= BusyAfter)
                Busy = true;
        }
        else
            _denseSince = null;

        if (!Busy || ActiveSeconds >= ActiveSecondsToStay)
            _calmSince = null;
        else
        {
            _calmSince ??= now;
            if (now - _calmSince >= CalmAfter)
            {
                Busy = false;
                _calmSince = null;
            }
        }
        return Busy != was;
    }

    /// <summary>
    /// Whether this notification waits while busy: ticket notifications, not P1, not from an important client, not with
    /// an SLA breached or due within 15 minutes, not samples.
    /// </summary>
    public static bool Holds(NotificationContent content) => Holds(content, DateTimeOffset.UtcNow);

    public static bool Holds(NotificationContent content, DateTimeOffset now) =>
        content.IsTicketNotification && !content.IsSample && !content.Important &&
        !(content.SlaDue is { } due && due - now <= EdgeGlowRule.SlaSoon) &&
        !string.Equals(content.Priority, "urgent", StringComparison.OrdinalIgnoreCase);
}
