using System.Text.Json;

namespace ZendeskWatcher.Tracking;

/// <summary>What the app has already seen, persisted so a restart does not notify about everything again.</summary>
public sealed class WatchState
{
    public string? Subdomain { get; set; }
    public long? UserId { get; set; }

    /// <summary>Unassigned ticket id -> the last time it was seen in a group's unassigned list.</summary>
    public Dictionary<long, DateTimeOffset> SeenUnassigned { get; set; } = [];

    /// <summary>Groups whose current unassigned tickets were recorded silently on their first poll.</summary>
    public HashSet<long> BaselinedGroups { get; set; } = [];

    /// <summary>Open tickets assigned to the user, keyed by ticket id.</summary>
    public Dictionary<long, TrackedTicket> MyTickets { get; set; } = [];

    public bool MyTicketsBaselined { get; set; }

    /// <summary>The newest updated_at seen in the assigned-tickets list. Newer open tickets are new assignments.</summary>
    public DateTimeOffset MyTicketsWatermark { get; set; }

    /// <summary>Open tickets where the user is CC'd or follows (and isn't the assignee), keyed by ticket id.</summary>
    public Dictionary<long, TrackedTicket> CcTickets { get; set; } = [];

    public bool CcTicketsBaselined { get; set; }

    /// <summary>The newest updated_at seen in the CC'd/followed lists.</summary>
    public DateTimeOffset CcTicketsWatermark { get; set; }

    public static WatchState Load()
    {
        try
        {
            if (File.Exists(AppPaths.StateFile))
                return JsonSerializer.Deserialize<WatchState>(File.ReadAllText(AppPaths.StateFile)) ?? new WatchState();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read state, starting fresh", ex);
        }
        return new WatchState();
    }

    public void Save()
    {
        var temp = AppPaths.StateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, AppPaths.StateFile, overwrite: true);
    }
}

public sealed class TrackedTicket
{
    public string? Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
