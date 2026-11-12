using System.Text.Json;

namespace ZendeskWatcher;

/// <summary>User settings, edited in the Settings window and stored in %LOCALAPPDATA%\ZendeskWatcher\settings.json.</summary>
public sealed class Settings
{
    // Connection
    public string Subdomain { get; set; } = "";

    // What to watch
    public List<WatchedGroup> WatchedGroups { get; set; } = [];
    public int PollIntervalSeconds { get; set; } = 60;
    public bool TrackMyTickets { get; set; } = true;

    /// <summary>Also notify about changes on tickets where I'm CC'd or a follower (same change types as my tickets).</summary>
    public bool TrackCcTickets { get; set; }

    /// <summary>Notify about new unassigned tickets in the monitored groups (they are counted either way).</summary>
    public bool NotifyUnassigned { get; set; } = true;

    // Which changes on my tickets raise a notification
    public bool NotifyPublicReplies { get; set; } = true;
    public bool NotifyInternalNotes { get; set; } = true;
    public bool NotifyStatusChanges { get; set; } = true;
    public bool NotifyAssignmentChanges { get; set; } = true;
    public bool NotifyPriorityChanges { get; set; } = true;

    /// <summary>
    /// "Help colleagues": notify when a customer replies on a colleague's open ticket while the colleague is outside
    /// working hours (in their own time zone).
    /// </summary>
    public bool HelpColleagues { get; set; }

    /// <summary>Use the monitored groups for "Help colleagues" (default), or <see cref="HelpGroups"/>.</summary>
    public bool HelpSameAsMonitored { get; set; } = true;

    /// <summary>Custom groups for "Help colleagues" when <see cref="HelpSameAsMonitored"/> is off.</summary>
    public List<WatchedGroup> HelpGroups { get; set; } = [];

    /// <summary>The groups "Help colleagues" actually covers (none when it's off).</summary>
    public IReadOnlyList<WatchedGroup> EffectiveHelpGroups =>
        !HelpColleagues ? [] : (HelpSameAsMonitored ? WatchedGroups : HelpGroups).Where(g => !g.IsNoGroup).ToList();

    /// <summary>Working hours, Monday to Friday, applied in each colleague's own time zone.</summary>
    public int WorkdayStartHour { get; set; } = 8;
    public int WorkdayEndHour { get; set; } = 17;

    /// <summary>
    /// "Help colleagues" only for these Zendesk priorities (urgent, high, normal, low). Tickets without a priority
    /// count as normal. Default: urgent (P1) and high (P2).
    /// </summary>
    public List<string> HelpPriorities { get; set; } = ["urgent", "high"];

    /// <summary>"Help colleagues" only notifies while it's within the same working hours on this PC (your own time).</summary>
    public bool HelpOnlyDuringMyHours { get; set; } = true;

    /// <summary>Show the start of the comment text in notifications, or only who replied / added a note.</summary>
    public bool ShowCommentText { get; set; } = true;

    // Appearance of the notifications
    public NotificationStyle NewTicketStyle { get; set; } = new() { Sound = ToastSound.Default, Display = ToastDisplay.Standard };
    public NotificationStyle MyTicketStyle { get; set; } = new() { Sound = ToastSound.Mail, Display = ToastDisplay.Standard };
    public NotificationStyle HelpStyle { get; set; } = new() { Sound = ToastSound.Reminder, Display = ToastDisplay.Long };

    public NotificationStyle StyleFor(NotificationKind kind) => kind switch
    {
        NotificationKind.NewTicket => NewTicketStyle,
        NotificationKind.HelpColleague => HelpStyle,
        _ => MyTicketStyle
    };
    public NotificationTheme Theme { get; set; } = NotificationTheme.Optimizely;
    public PopupPosition Position { get; set; } = PopupPosition.BottomRight;
    public bool ShowAppLogo { get; set; } = true;
    public bool GroupNotificationsByType { get; set; } = true;

    public string BaseUrl => $"https://{Subdomain}.zendesk.com";
    public string TicketUrl(long id) => $"{BaseUrl}/agent/tickets/{id}";

    public bool IsConnectionConfigured => !string.IsNullOrWhiteSpace(Subdomain);

    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>True when no settings file existed at start: the very first start (shows the welcome).</summary>
    public static bool IsFirstRun { get; private set; }

    public static Settings Load()
    {
        IsFirstRun = !File.Exists(FilePath);
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
                settings.Normalize();
                return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read settings, using defaults", ex);
        }
        return new Settings();
    }

    /// <summary>
    /// Replaces values this version doesn't know (e.g. a theme added in a newer version, saved before going back
    /// to an older one) with defaults, so nothing breaks on them.
    /// </summary>
    private void Normalize()
    {
        if (!Enum.IsDefined(Theme)) Theme = NotificationTheme.NeonDark;
        WorkdayStartHour = Math.Clamp(WorkdayStartHour, 0, 23);
        WorkdayEndHour = Math.Clamp(WorkdayEndHour, WorkdayStartHour + 1, 24);
        if (!Enum.IsDefined(Position)) Position = PopupPosition.BottomRight;
        if (!Enum.IsDefined(NewTicketStyle.Sound)) NewTicketStyle.Sound = ToastSound.Default;
        if (!Enum.IsDefined(NewTicketStyle.Display)) NewTicketStyle.Display = ToastDisplay.Standard;
        if (!Enum.IsDefined(MyTicketStyle.Sound)) MyTicketStyle.Sound = ToastSound.Mail;
        if (!Enum.IsDefined(MyTicketStyle.Display)) MyTicketStyle.Display = ToastDisplay.Standard;
        if (!Enum.IsDefined(HelpStyle.Sound)) HelpStyle.Sound = ToastSound.Reminder;
        if (!Enum.IsDefined(HelpStyle.Display)) HelpStyle.Display = ToastDisplay.Long;
    }

    public void Save()
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 30, 3600);
        File.WriteAllText(FilePath, ToJson());
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(ToJson(), JsonOptions)!;

    /// <summary>Normalizes "acme", "acme.zendesk.com" or "https://acme.zendesk.com/" to "acme".</summary>
    public static string NormalizeSubdomain(string input)
    {
        var value = input.Trim().ToLowerInvariant();
        value = value.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        var dot = value.IndexOf('.');
        return dot > 0 ? value[..dot] : value;
    }
}

public sealed class WatchedGroup
{
    /// <summary>Pseudo group for tickets that have neither a group nor an assignee (Zendesk ids are always positive).</summary>
    public const long NoGroupId = 0;
    public const string NoGroupName = "(No group)";

    public long Id { get; set; }
    public string Name { get; set; } = "";

    public bool IsNoGroup => Id == NoGroupId;

    /// <summary>"in Support Tier 2", or "with no group" for the pseudo group.</summary>
    public string Describe() => IsNoGroup ? "with no group" : $"in {Name}";
}

public sealed class NotificationStyle
{
    public ToastSound Sound { get; set; } = ToastSound.Default;
    public ToastDisplay Display { get; set; } = ToastDisplay.Standard;
}

public enum ToastSound { Default, Mail, InstantMessage, Reminder, Sms, Silent }

public enum ToastDisplay
{
    /// <summary>About 5 seconds, then it moves to the notification center.</summary>
    Standard,

    /// <summary>About 25 seconds.</summary>
    Long,

    /// <summary>Stays on screen until clicked or dismissed.</summary>
    UntilDismissed
}

public enum NotificationTheme
{
    NeonDark,
    Terminal,
    CleanLight,

    /// <summary>The built-in Windows toasts (work with Focus / Do Not Disturb and the notification center).</summary>
    WindowsNative,

    /// <summary>Dusky purple with warm cream text and soft pastel accents. (Added last: settings store the number.)</summary>
    CozyPurple,

    /// <summary>The optimizely.com look: dark fir green with lime accents. (Added last: settings store the number.)</summary>
    Optimizely
}

public enum PopupPosition { BottomRight, TopRight }
