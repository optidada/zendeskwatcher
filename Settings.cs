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

    /// <summary>
    /// Colleagues "Help colleagues" ignores (unticked in Settings). Only exclusions are stored, so new group members
    /// are covered automatically. The name is kept for display.
    /// </summary>
    public List<Colleague> HelpExcludedAssignees { get; set; } = [];

    /// <summary>The groups "Help colleagues" actually covers (none when it's off).</summary>
    public IReadOnlyList<WatchedGroup> EffectiveHelpGroups =>
        !HelpColleagues ? [] : (HelpSameAsMonitored ? WatchedGroups : HelpGroups).Where(g => !g.IsNoGroup).ToList();

    /// <summary>Colleagues' working hours, applied in each colleague's own time zone.</summary>
    public int WorkdayStartHour { get; set; } = 8;
    public int WorkdayEndHour { get; set; } = 17;

    /// <summary>Colleagues' working days (one set for everyone; Zendesk doesn't know each person's schedule).</summary>
    public List<DayOfWeek> WorkDays { get; set; } = [.. Tracking.TicketRules.Weekdays];

    /// <summary>My own working days.</summary>
    public List<DayOfWeek> MyWorkDays { get; set; } = [.. Tracking.TicketRules.Weekdays];

    /// <summary>
    /// "Help colleagues" only for these Zendesk priorities (urgent, high, normal, low). Tickets without a priority
    /// count as normal. Default: urgent (P1) and high (P2).
    /// </summary>
    public List<string> HelpPriorities { get; set; } = ["urgent", "high"];

    /// <summary>"Help colleagues" only notifies while it's within the same working hours on this PC (your own time).</summary>
    public bool HelpOnlyDuringMyHours { get; set; } = true;

    /// <summary>New tickets and changes on my own and CC'd tickets only notify within my working hours; outside them they're skipped (colleagues' tickets: <see cref="HelpOnlyDuringMyHours"/>).</summary>
    public bool OnlyNotifyDuringMyHours { get; set; }

    private int? _myWorkdayStartHour, _myWorkdayEndHour;

    /// <summary>My own working hours (on <see cref="MyWorkDays"/>, this PC's time). Until chosen, the colleagues' hours (they were one setting before 1.3.13).</summary>
    public int MyWorkdayStartHour
    {
        get => _myWorkdayStartHour ??= WorkdayStartHour;
        set => _myWorkdayStartHour = value;
    }

    public int MyWorkdayEndHour
    {
        get => _myWorkdayEndHour ??= WorkdayEndHour;
        set => _myWorkdayEndHour = value;
    }

    /// <summary>After the first check of each start: a short summary of what's being watched and what's waiting.</summary>
    public bool ShowStartSummary { get; set; } = true;

    /// <summary>How long the Snooze button on a popup hides it before it shows again.</summary>
    public int SnoozeMinutes { get; set; } = 10;

    /// <summary>Whether a snoozed popup shows again only if its ticket still needs attention.</summary>
    public SnoozeBehavior SnoozeBehavior { get; set; } = SnoozeBehavior.IfStillNeeded;

    /// <summary>The ticket priorities, in Zendesk's words. Tickets without a priority count as "normal".</summary>
    public static readonly string[] AllPriorities = ["urgent", "high", "normal", "low"];

    /// <summary>Only changes on my tickets with these (current) priorities notify.</summary>
    public List<string> MyTicketPriorities { get; set; } = [.. AllPriorities];

    /// <summary>Only changes on CC'd / followed tickets with these (current) priorities notify. Urgent and high by default:
    /// you're only watching these, so the important ones are enough.</summary>
    public List<string> CcTicketPriorities { get; set; } = ["urgent", "high"];

    /// <summary>Show the start of the comment text in notifications (my, CC and colleagues' tickets), or only who replied / added a note.</summary>
    public bool ShowCommentText { get; set; } = true;

    // Appearance of the notifications
    // Ticket notifications: the sound by type; how long they stay is by priority (TicketDisplay), so their Display isn't used.
    public NotificationStyle NewTicketStyle { get; set; } = new() { Sound = ToastSound.Default };
    public NotificationStyle MyTicketStyle { get; set; } = new() { Sound = ToastSound.Mail };
    public NotificationStyle HelpStyle { get; set; } = new() { Sound = ToastSound.Reminder };

    private NotificationStyle? _ccTicketStyle;

    /// <summary>CC / following tickets. Until chosen, the same as My tickets (they shared that setting before 1.3.11).</summary>
    public NotificationStyle CcTicketStyle
    {
        get => _ccTicketStyle ??= new NotificationStyle { Sound = MyTicketStyle.Sound };
        set => _ccTicketStyle = value;
    }

    /// <summary>The summary after the first check: silent, but on screen long enough to read.</summary>
    public NotificationStyle SummaryStyle { get; set; } = new() { Sound = ToastSound.Silent, Display = ToastDisplay.Medium };

    /// <summary>Update ready, welcome, monitored group not found, and the sound of "Sign-in required".</summary>
    public NotificationStyle AppMessageStyle { get; set; } = new() { Sound = ToastSound.Default, Display = ToastDisplay.Long };

    /// <summary>The sound: app messages and the summary by their style, ticket notifications by type.</summary>
    public ToastSound SoundFor(UI.Popups.NotificationContent content) => (content.Style ?? StyleFor(content.Kind)).Sound;

    /// <summary>How long ticket notifications stay on screen, by the ticket's priority (all ticket types).</summary>
    public TicketDisplayTimes TicketDisplay { get; set; } = new();

    /// <summary>Seconds on screen, 0 = until dismissed: app messages and the summary by their style, ticket notifications by priority.</summary>
    public int DisplaySecondsFor(UI.Popups.NotificationContent content) =>
        content.Style is { } style ? style.Display.Seconds() : TicketDisplay.For(content.Priority);

    public NotificationStyle StyleFor(NotificationKind kind) => kind switch
    {
        NotificationKind.NewTicket => NewTicketStyle,
        NotificationKind.HelpColleague => HelpStyle,
        NotificationKind.CcTicket => CcTicketStyle,
        _ => MyTicketStyle
    };
    /// <summary>The notification style when <see cref="NotificationsLikeApp"/> is off. (Before 1.3.11 the only style setting.)</summary>
    public NotificationTheme Theme { get; set; } = NotificationTheme.Optimizely;

    private NotificationTheme? _appTheme;

    /// <summary>Colors of the Settings window and its dialogs. Until chosen, the same as <see cref="Theme"/> (they were one setting).</summary>
    public NotificationTheme AppTheme
    {
        get => _appTheme ??= Theme;
        set => _appTheme = value;
    }

    /// <summary>Notifications look like the app (default), or use their own <see cref="Theme"/>.</summary>
    public bool NotificationsLikeApp { get; set; } = true;

    /// <summary>The style notifications are shown in.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public NotificationTheme ActiveNotificationTheme => NotificationsLikeApp ? AppTheme : Theme;
    public PopupPosition Position { get; set; } = PopupPosition.BottomRight;

    /// <summary>Size of Zendesk Watcher's own notifications in percent (text, buttons and spacing together).</summary>
    public int NotificationSize { get; set; } = 100;

    /// <summary>Several popups take turns: one counts down at a time, the others wait.</summary>
    public bool PopupsTakeTurns { get; set; } = true;

    /// <summary>Popups don't count down while the screen is locked or there's been no input for a minute.</summary>
    public bool PausePopupsWhenAway { get; set; } = true;

    /// <summary>
    /// Notifications that waited this long (in the line, or on screen while away) are checked against Zendesk before
    /// they show, and dropped when no longer needed. 0: never checked.
    /// </summary>
    public int RecheckWaitingAfterMinutes { get; set; } = 15;

    /// <summary>Coming back to more than this many waiting notifications: one summary instead. 0: never.</summary>
    public int SummarizeWaitingAbove { get; set; } = 5;

    /// <summary>
    /// A customer reply counts as handled once an agent answered it publicly (colleagues' tickets), and changes on
    /// your own / CC'd tickets once you've acted on the ticket yourself. Used by the checks above, Snooze, and
    /// "Customer waiting" (no notification when a colleague already answered).
    /// </summary>
    public bool DropAnswered { get; set; } = true;
    public bool ShowAppLogo { get; set; } = true;
    public bool GroupNotificationsByType { get; set; } = true;

    public string BaseUrl => $"https://{Subdomain}.zendesk.com";
    public string TicketUrl(long id) => $"{BaseUrl}/agent/tickets/{id}";

    public bool IsConnectionConfigured => !string.IsNullOrWhiteSpace(Subdomain);

    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    /// <summary>True when no settings file existed at start: the very first start (shows the welcome).</summary>
    public static bool IsFirstRun { get; private set; }

    /// <summary>Set when settings.json couldn't be read at start: what happened, for a notification. Null when all is well.</summary>
    public static string? LoadProblem { get; private set; }

    public static Settings Load() => Load(FilePath);

    /// <summary>
    /// Reads the settings. A damaged file is never silently lost: it's kept as settings.json.bad, and the copy from
    /// before the last save (settings.json.bak) is used instead; only if that fails too do the defaults apply.
    /// </summary>
    internal static Settings Load(string path)
    {
        LoadProblem = null;
        IsFirstRun = !File.Exists(path) && !File.Exists(path + ".bak");
        if (!File.Exists(path))
            return new Settings { Subdomain = DefaultSubdomain };
        if (TryRead(path) is { } settings)
            return settings;

        try { File.Copy(path, path + ".bad", overwrite: true); }
        catch (Exception ex) { Log.Error("Could not keep the damaged settings file", ex); }
        if (TryRead(path + ".bak") is { } backup)
        {
            Log.Info("Settings: using settings.json.bak");
            LoadProblem = "Your settings file was damaged, so the copy from before your last change is used. Check that " +
                "everything is as you want it. The damaged file is kept as settings.json.bad.";
            return backup;
        }
        LoadProblem = "Your settings file couldn't be read, so the defaults are used: choose your groups and options again " +
            "in Settings. The damaged file is kept as settings.json.bad.";
        return new Settings { Subdomain = DefaultSubdomain };
    }

    private static Settings? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions);
            settings?.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    /// <summary>
    /// The subdomain filled in on first start: set by the release build (-p:DefaultSubdomain=…, from a GitHub secret),
    /// so it isn't in the source code. Empty in builds without it.
    /// </summary>
    public static string DefaultSubdomain { get; } = NormalizeSubdomain(
        typeof(Settings).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DefaultSubdomain")?.Value ?? "");

    /// <summary>
    /// Replaces values this version doesn't know (e.g. a theme added in a newer version, saved before going back
    /// to an older one) with defaults, so nothing breaks on them.
    /// </summary>
    private void Normalize()
    {
        if (!Enum.IsDefined(Theme)) Theme = NotificationTheme.NeonDark;
        if (!Enum.IsDefined(AppTheme)) AppTheme = Theme;
        WorkdayStartHour = Math.Clamp(WorkdayStartHour, 0, 23);
        WorkdayEndHour = Math.Clamp(WorkdayEndHour, WorkdayStartHour + 1, 24);
        MyWorkdayStartHour = Math.Clamp(MyWorkdayStartHour, 0, 23);
        MyWorkdayEndHour = Math.Clamp(MyWorkdayEndHour, MyWorkdayStartHour + 1, 24);
        if (!Enum.IsDefined(Position)) Position = PopupPosition.BottomRight;
        NotificationSize = Math.Clamp(NotificationSize, 100, 200);
        if (!Enum.IsDefined(NewTicketStyle.Sound)) NewTicketStyle.Sound = ToastSound.Default;
        if (!Enum.IsDefined(MyTicketStyle.Sound)) MyTicketStyle.Sound = ToastSound.Mail;
        if (!Enum.IsDefined(HelpStyle.Sound)) HelpStyle.Sound = ToastSound.Reminder;
        TicketDisplay ??= new TicketDisplayTimes();
        TicketDisplay.Normalize();
    }

    public void Save() => Save(FilePath);

    /// <summary>
    /// Writes a temp file first and then swaps it in, keeping the previous file as settings.json.bak: a crash or a
    /// full disk while saving never leaves a half-written settings.json.
    /// </summary>
    internal void Save(string path)
    {
        PollIntervalSeconds = Math.Clamp(PollIntervalSeconds, 30, 3600);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        if (File.Exists(path))
            File.Replace(temp, path, path + ".bak");
        else
            File.Move(temp, path);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(ToJson(), JsonOptions)!;

    public static Settings FromJson(string json) => JsonSerializer.Deserialize<Settings>(json, JsonOptions)!;

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

/// <summary>A Zendesk agent, by id, with the name as last seen.</summary>
public sealed class Colleague
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class NotificationStyle
{
    public ToastSound Sound { get; set; } = ToastSound.Default;
    public ToastDisplay Display { get; set; } = ToastDisplay.Standard;
}

/// <summary>Windows sounds, then chiptune jingles (added last: settings store the number).</summary>
public enum ToastSound { Default, Mail, InstantMessage, Reminder, Sms, Silent, ChipBlip, ChipArpeggio, ChipBounce, ChipAlert, ChipFanfare,
    SongBouncy, SongGroove, SongCelebration, SongQuest, SongTurbo, SongStarfield }

public enum SnoozeBehavior
{
    /// <summary>Checks the ticket first: a new ticket must still be unassigned, any ticket not solved or closed.</summary>
    IfStillNeeded,

    /// <summary>Shows it again whatever happened to the ticket.</summary>
    Always
}

public enum ToastDisplay
{
    /// <summary>About 5 seconds, then it moves to the notification center.</summary>
    Standard,

    /// <summary>About 25 seconds.</summary>
    Long,

    /// <summary>Stays on screen until clicked or dismissed.</summary>
    UntilDismissed,

    /// <summary>About 10 seconds (added last, so saved values keep their meaning).</summary>
    Medium
}

public static class ToastDisplayExtensions
{
    /// <summary>Seconds on screen; 0 = until dismissed.</summary>
    public static int Seconds(this ToastDisplay display) => display switch
    {
        ToastDisplay.Medium => 10,
        ToastDisplay.Long => 25,
        ToastDisplay.UntilDismissed => 0,
        _ => 5
    };
}

/// <summary>Seconds a ticket notification stays on screen, by priority; 0 = until dismissed.</summary>
public sealed class TicketDisplayTimes
{
    /// <summary>The choices in Settings; other values (an edited file) snap to the nearest.</summary>
    public static readonly int[] Choices = [10, 15, 30, 60, 0];

    public int Urgent { get; set; }
    public int High { get; set; } = 60;
    public int Normal { get; set; } = 30;
    public int Low { get; set; } = 15;

    /// <summary>Tickets without a priority count as normal.</summary>
    public int For(string? priority) => priority?.ToLowerInvariant() switch
    {
        "urgent" => Urgent,
        "high" => High,
        "low" => Low,
        _ => Normal
    };

    internal void Normalize()
    {
        Urgent = Snap(Urgent);
        High = Snap(High);
        Normal = Snap(Normal);
        Low = Snap(Low);
    }

    private static int Snap(int seconds) => seconds <= 0 ? 0 : Choices.Where(c => c > 0).MinBy(c => Math.Abs(c - seconds));
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
    Optimizely,

    /// <summary>For late evenings: warm near-black, dimmed cream text, muted colors, no glow. (Added last: settings store the number.)</summary>
    Night,

    /// <summary>Classic 80s home computer: deep blue, white text, orange accents. (Added last: settings store the number.)</summary>
    Retro
}

public enum PopupPosition { BottomRight, TopRight }
