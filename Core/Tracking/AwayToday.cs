namespace ZendeskWatcher.Tracking;

/// <summary>
/// Colleagues the daily "Cover today?" check found without any Zendesk activity today (in their working hours): the
/// Colleagues page shows them with an amber dot. Set by the check, kept in the state (so a restart keeps it for the
/// day), and only valid on the day it was found.
/// </summary>
public static class AwayToday
{
    private static readonly object Gate = new();
    private static DateTime _day;
    private static HashSet<long> _ids = [];

    /// <summary>Found away today.</summary>
    public static bool Contains(long id)
    {
        lock (Gate)
            return _day == DateTime.Today && _ids.Contains(id);
    }

    /// <summary>Today's findings (replacing earlier ones).</summary>
    public static void Set(DateTime day, IEnumerable<long> ids)
    {
        lock (Gate)
        {
            _day = day.Date;
            _ids = [.. ids];
        }
    }
}
