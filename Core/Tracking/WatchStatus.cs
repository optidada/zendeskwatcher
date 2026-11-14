namespace ZendeskWatcher.Tracking;

/// <summary>One monitored group's unassigned tickets as of the last check.</summary>
public sealed record GroupCount(WatchedGroup Group, int Count, int Scheduled = 0);

/// <summary>
/// What the last check found: the "Right now" card on Insights shows it. <see cref="Mine"/> is null when your own
/// tickets aren't tracked, <see cref="Cc"/> when CC / following isn't.
/// </summary>
public sealed record WatchSnapshot(
    DateTime CheckedAt,
    IReadOnlyList<GroupCount> Groups,
    DateTimeOffset? OldestUnassigned,
    (int Open, int Pending, int Hold)? Mine,
    int? Cc,
    bool CcAtLeast)
{
    public int Unassigned => Groups.Sum(g => g.Count);

    /// <summary>How many of the unassigned tickets are Scheduled (a time is booked, nobody owns it).</summary>
    public int Scheduled => Groups.Sum(g => g.Scheduled);

    /// <summary>Sample numbers for the Settings screenshots (--render-settings).</summary>
    public static WatchSnapshot Sample() => new(DateTime.Now.AddSeconds(-20),
        [new(new WatchedGroup { Id = 1, Name = "Support Tier 2" }, 3), new(new WatchedGroup { Id = 2, Name = "Premium Support" }, 2)],
        DateTimeOffset.UtcNow.AddMinutes(-130), (3, 4, 1), 2, false);
}

/// <summary>
/// The latest check, shared between the tray loop (which updates it) and the Settings window (which shows it).
/// Both run on the UI thread.
/// </summary>
public static class WatchStatus
{
    public static WatchSnapshot? Latest { get; private set; }

    /// <summary>Why the numbers aren't current (signed out, an error, …); null while checking works.</summary>
    public static string? Problem { get; private set; }

    public static event Action? Changed;

    public static void Update(WatchSnapshot snapshot)
    {
        Latest = snapshot;
        Problem = null;
        Changed?.Invoke();
    }

    /// <summary>Checking stopped (e.g. sign-in required): the old numbers would be misleading.</summary>
    public static void Stopped(string reason)
    {
        Latest = null;
        Problem = reason;
        Changed?.Invoke();
    }

    /// <summary>A check failed; it's retried, and the last numbers are kept with the reason.</summary>
    public static void Failed(string reason)
    {
        Problem = reason;
        Changed?.Invoke();
    }
}
