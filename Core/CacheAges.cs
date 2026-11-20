namespace ZendeskWatcher;

/// <summary>
/// How long the app reuses what it read from Zendesk, all in one place (the table in CLAUDE.md lists them too).
/// "Fresh": used as is. "Keep": older than fresh but younger than this, it's used right away while one fresh read runs in
/// the background (only when something needs it: a click, a notification, a page; never a timer). Older: the read is
/// waited for. Nothing here holds ticket text or histories on disk (comments, changes, subjects stay in memory).
/// </summary>
public static class CacheAges
{
    /// <summary>Group workload (ticket number, assignee, status, priority) (cache\group-workload.json). Fresh enough for good
    /// suggestions; older than Keep (e.g. overnight) the click waits for a fresh read instead of ranking by stale counts.</summary>
    public static readonly TimeSpan WorkloadFresh = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan WorkloadKeep = TimeSpan.FromHours(2);

    /// <summary>Who is in a group: rarely changes (cache\group-workload.json, with the workload).</summary>
    public static readonly TimeSpan MembersFresh = TimeSpan.FromDays(1);
    public static readonly TimeSpan MembersKeep = TimeSpan.FromDays(14);

    /// <summary>"Usually handles this client" (90 days of a client's tickets per agent): hardly moves in days (cache\client-handlers.json).</summary>
    public static readonly TimeSpan ClientHandlersFresh = TimeSpan.FromDays(3);
    public static readonly TimeSpan ClientHandlersKeep = TimeSpan.FromDays(14);

    /// <summary>Seen this recently doing something in Zendesk: active, their activity isn't read.</summary>
    public static readonly TimeSpan ActivitySeenRecently = TimeSpan.FromMinutes(10);
    /// <summary>A colleague's activity read on purpose is reused this long (a failed read too: not retried every check).</summary>
    public static readonly TimeSpan ActivityRead = TimeSpan.FromMinutes(15);
    /// <summary>My own activity, for "Working late?": about me now, so read more often.</summary>
    public static readonly TimeSpan MyActivityRead = TimeSpan.FromMinutes(5);
    /// <summary>Sightings and reads kept on disk this long (cache\activity.json); the rules only look 3 hours back.</summary>
    public static readonly TimeSpan ActivityKeep = TimeSpan.FromDays(1);

    /// <summary>Users (name, role, time zone; clients' people by name and email domain only) (cache\users.json).</summary>
    public static readonly TimeSpan Users = TimeSpan.FromDays(7);

    /// <summary>Settings lists: group members (Colleagues) and clients; Reload reads them at once (cache\*.json).</summary>
    public static readonly TimeSpan SettingsLists = TimeSpan.FromHours(24);
    /// <summary>The group list itself (Groups page).</summary>
    public static readonly TimeSpan Groups = TimeSpan.FromDays(7);
    /// <summary>Clients page opened again after this: reads what changed since (cache\clients-digest.json).</summary>
    public static readonly TimeSpan ClientsTopUp = TimeSpan.FromMinutes(15);

    /// <summary>Insights → Team workload is read again when shown and older than this (same as the shared group workload).</summary>
    public static readonly TimeSpan TeamWorkload = WorkloadFresh;

    /// <summary>The ticket field "Product" (cache\ticket-product-field.json).</summary>
    public static readonly TimeSpan ProductField = TimeSpan.FromDays(7);

    /// <summary>Priority guess history (Experimental; cache\ticket-history.json): topped up, read in full weekly.</summary>
    public static readonly TimeSpan GuessHistoryTopUp = TimeSpan.FromHours(6);
    public static readonly TimeSpan GuessHistoryFull = TimeSpan.FromDays(7);
}
