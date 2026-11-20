using System.Text.Json;

namespace ZendeskWatcher.Tracking;

/// <summary>What the app has already seen, persisted so a restart does not notify about everything again.</summary>
public sealed class WatchState
{
    public string? Subdomain { get; set; }
    public long? UserId { get; set; }

    /// <summary>Unassigned ticket id -> the last time it was seen in a group's unassigned list.</summary>
    public Dictionary<long, DateTimeOffset> SeenUnassigned { get; set; } = [];

    /// <summary>Unassigned tickets kept quiet for a priority not chosen -> that priority, to notify once one is raised.</summary>
    public Dictionary<long, string> QuietPriority { get; set; } = [];

    /// <summary>Unassigned tickets kept quiet for another product (My products) -> that product, to notify once it's mine.</summary>
    public Dictionary<long, string> QuietProduct { get; set; } = [];

    /// <summary>Tickets someone took before the app saw them unassigned, already looked at -> when (Notifications → New tickets).</summary>
    public Dictionary<long, DateTimeOffset> TakenQuickly { get; set; } = [];

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

    /// <summary>When Catch up last ran (local time), so it runs by itself once per working day.</summary>
    public DateTime LastCatchUp { get; set; }

    /// <summary>The local date "Working late?" was last shown: once a day, also across restarts and updates.</summary>
    public DateTime WorkingLateNoticeShown { get; set; }

    /// <summary>The day colleagues in my region were last looked at for "Cover today?" (once a day).</summary>
    public DateTime MissingColleaguesChecked { get; set; }

    /// <summary>Who that check found without activity today (<see cref="AwayToday"/>).</summary>
    public List<long> AwayTodayIds { get; set; } = [];

    /// <summary>The baseline hit the size of a CC'd/followed list (30): older open ones may be missing from the count.</summary>
    public bool CcBaselineFull { get; set; }

    /// <summary>The newest updated_at seen in the CC'd/followed lists.</summary>
    public DateTimeOffset CcTicketsWatermark { get; set; }

    /// <summary>"Help colleagues": ticket id -> the updated_at already looked at.</summary>
    public Dictionary<long, DateTimeOffset> HelpSeen { get; set; } = [];


    /// <summary>Groups whose current tickets were recorded silently on their first check.</summary>
    public HashSet<long> HelpBaselinedGroups { get; set; } = [];

    /// <summary>When the last "Help colleagues" check ran.</summary>
    public DateTimeOffset HelpLastCheck { get; set; }

    /// <summary>What Zendesk's SLA timers taught about the SLA types and their targets (Notifications → SLA).</summary>
    public SlaLearning Sla { get; set; } = new();

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

    /// <summary>
    /// Writes the state (via a temp file, so a crash never leaves half a file). Another program, e.g. a virus scanner, can
    /// hold the file for a moment just after it was written: then it's tried again a few times, briefly.
    /// </summary>
    public void Save()
    {
        var temp = AppPaths.StateFile + ".tmp";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.StateFile, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(50 * attempt); // 50, 100, 150, 200 ms
            }
        }
    }
}

public sealed class TrackedTicket
{
    public string? Status { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
