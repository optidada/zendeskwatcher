using System.Text.Json;
using ZendeskWatcher.Tracking;

namespace ZendeskWatcher;

/// <summary>User settings, edited in the Settings window and stored in %LOCALAPPDATA%\ZendeskWatcher\settings.json.</summary>
public sealed class Settings
{
    // Connection
    public string Subdomain { get; set; } = "";

    // What to watch
    public List<WatchedGroup> WatchedGroups { get; set; } = [];

    /// <summary>
    /// How often Zendesk is checked (fixed). Every 3 minutes keeps the app's share of the account's API limit small
    /// (it's shared with everyone and every integration); Zendesk's search lags 1–3 minutes anyway.
    /// </summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(3);

    /// <summary>New unassigned tickets are also checked halfway between two checks (not while away): new work, P1s
    /// especially, shows within about 1½ minutes, while the rest stays at <see cref="CheckInterval"/>.</summary>
    public static readonly TimeSpan NewTicketsCheckInterval = TimeSpan.FromSeconds(90);

    /// <summary>How often while you're away (<see cref="AwayForChecks"/>); a check runs as soon as you're back.</summary>
    public static readonly TimeSpan AwayCheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>Screen locked, or no mouse or keyboard this long: away, for how often to check.</summary>
    public static readonly TimeSpan AwayForChecks = TimeSpan.FromMinutes(10);
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
    /// Also changes made automatically (Zendesk triggers/automations, service accounts like the auto-reply on new
    /// tickets): off by default. Status changes notify either way (<see cref="Tracking.TicketRules.IsAutomaticChange"/>).
    /// </summary>
    public bool NotifyAutomaticChanges { get; set; }

    /// <summary>
    /// "Help colleagues": notify when a customer replies on a colleague's open ticket while the colleague is outside
    /// working hours (in their own time zone). On for new installs (since 1.3.19); saved files keep their choice.
    /// </summary>
    public bool HelpColleagues { get; set; } = true;

    /// <summary>
    /// Colleagues in your own region have been unticked once (the default; they usually work when you do). Null in
    /// files from before 1.3.19: <see cref="Normalize"/> works it out from what was saved then.
    /// </summary>
    public bool? HelpRegionDefaultDone { get; set; }

    /// <summary>Use the monitored groups for "Help colleagues" (default), or <see cref="HelpGroups"/>.</summary>
    public bool HelpSameAsMonitored { get; set; } = true;

    /// <summary>Custom groups for "Help colleagues" when <see cref="HelpSameAsMonitored"/> is off.</summary>
    public List<WatchedGroup> HelpGroups { get; set; } = [];

    /// <summary>
    /// Colleagues "Help colleagues" ignores (unticked in Settings). Only exclusions are stored, so new group members
    /// are covered automatically. The name is kept for display.
    /// </summary>
    public List<Colleague> HelpExcludedAssignees { get; set; } = [];

    /// <summary>
    /// Regions unticked on the Colleagues page: nobody there is covered (newcomers neither), except
    /// <see cref="HelpIncludedAssignees"/>. In other regions everyone is, except <see cref="HelpExcludedAssignees"/>.
    /// </summary>
    public List<TimeRegion> HelpOffRegions { get; set; } = [];

    /// <summary>Colleagues ticked one by one in an unticked region (see <see cref="HelpOffRegions"/>).</summary>
    public List<Colleague> HelpIncludedAssignees { get; set; } = [];

    /// <summary>
    /// Whether "Help colleagues" covers this colleague: their region's tick, then the exceptions. Until the list was
    /// shown once, your own region counts as unticked (<see cref="HelpRegionDefaultDone"/>).
    /// </summary>
    public bool CoversColleague(long id, TimeRegion region, TimeRegion myRegion) =>
        HelpOffRegions.Contains(region) || (HelpRegionDefaultDone != true && region == myRegion)
            ? HelpIncludedAssignees.Any(c => c.Id == id)
            : HelpExcludedAssignees.All(c => c.Id != id);

    /// <summary>
    /// Choices per client (Zendesk organization) on the Clients page or from a notification: muted (for good or for a
    /// while), only urgent, important, an own sound. Only clients with a choice are stored; everyone else is Normal,
    /// so new clients are included automatically. See <see cref="ClientChoice"/>.
    /// </summary>
    public List<ClientChoice> Clients { get; set; } = [];

    /// <summary>The choice for a client (organization id; <see cref="ClientChoice.NoClientId"/> for none), if any.</summary>
    public ClientChoice? ClientChoiceFor(long? organizationId) =>
        Clients.FirstOrDefault(c => c.Id == (organizationId ?? ClientChoice.NoClientId));

    /// <summary>Sets a client's choice (replacing any earlier one); a choice back to the defaults is removed.</summary>
    public void SetClientChoice(ClientChoice choice)
    {
        Clients.RemoveAll(c => c.Id == choice.Id);
        if (!choice.IsDefaultAt(DateTimeOffset.UtcNow))
            Clients.Add(choice.Copy());
    }

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

    /// <summary>
    /// With <see cref="OnlyNotifyDuringMyHours"/> (and the Colleagues page's "only while I'm working"): also outside my
    /// hours while I'm active in Zendesk (a change or comment in the last 30 minutes). Off by default.
    /// </summary>
    public bool NotifyWhenWorkingLate { get; set; }

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

    /// <summary>After the first check of each start: Catch up as a greeting (the one-by-one ones only once a working day).</summary>
    public bool ShowStartSummary { get; set; } = true;

    /// <summary>How long the Snooze button on a popup hides it before it shows again.</summary>
    public int SnoozeMinutes { get; set; } = 10;

    /// <summary>The ticket priorities, in Zendesk's words. Tickets without a priority count as "normal".</summary>
    public static readonly string[] AllPriorities = ["urgent", "high", "normal", "low"];

    /// <summary>
    /// New unassigned tickets: group id -> the priorities that notify and count (Notifications → New tickets). A group not
    /// listed: all. One raised to a chosen priority while still unassigned notifies then; important clients always do.
    /// </summary>
    public Dictionary<long, List<string>> NewTicketPriorities { get; set; } = [];

    /// <summary>
    /// New tickets someone took between two checks, before the app saw them unassigned: with these priorities they notify
    /// anyway ("Taken by Anna", Notifications → New tickets). Empty: off, the default. For managers who don't want to miss
    /// a P1 even when it was assigned out within a minute.
    /// </summary>
    public List<string> TakenQuicklyPriorities { get; set; } = [];

    /// <summary>
    /// The products I work with: values of the ticket field "Product" (Groups → Products). Empty: all products,
    /// the default. New tickets and colleagues' tickets for another product only notify with
    /// <see cref="OtherProductPriorities"/>; when unsure (no product, or one that doesn't fit the client) they notify.
    /// </summary>
    public List<string> MyProducts { get; set; } = [];

    /// <summary>Tickets for other products still notify with these priorities (with <see cref="MyProducts"/> picked). Empty: never.</summary>
    public List<string> OtherProductPriorities { get; set; } = ["urgent", "high"];

    /// <summary>The priorities of new unassigned tickets that notify in this group.</summary>
    public IReadOnlyList<string> NewTicketPrioritiesFor(long groupId) =>
        NewTicketPriorities.TryGetValue(groupId, out var chosen) && chosen.Count > 0 ? chosen : AllPriorities;

    /// <summary>The priority choices in Settings (new tickets per group, ticket changes, Help colleagues), most to fewest;
    /// null is all priorities.</summary>
    public static readonly (string Label, string[]? Priorities)[] PriorityChoices =
    [
        ("All", null),
        ("P1–P3", ["urgent", "high", "normal"]),
        ("P1, P2", ["urgent", "high"]),
        ("P1 only", ["urgent"])
    ];

    /// <summary>
    /// The choice for a list of priorities: by the lowest one in it (a list from an older version with a gap, e.g. P1 and
    /// P3, gets P1–P3). An empty list gets P1 only, the fewest there is.
    /// </summary>
    public static int PriorityChoiceIndex(IReadOnlyCollection<string> priorities) =>
        priorities.Contains("low") ? 0 : priorities.Contains("normal") ? 1 : priorities.Contains("high") ? 2 : 3;

    /// <summary>Only changes on my tickets with these (current) priorities notify.</summary>
    public List<string> MyTicketPriorities { get; set; } = [.. AllPriorities];

    /// <summary>Only changes on CC'd / followed tickets with these (current) priorities notify. Urgent and high by default:
    /// you're only watching these, so the important ones are enough.</summary>
    public List<string> CcTicketPriorities { get; set; } = ["urgent", "high"];

    /// <summary>Show the start of the comment text in notifications (my, CC and colleagues' tickets), or only who replied / added a note.</summary>
    public bool ShowCommentText { get; set; } = true;

    /// <summary>Show the SLA deadline ("Reply due in 25 min") in red bold on ticket notifications (Notifications → SLA).</summary>
    public bool ShowSlaDeadlines { get; set; } = true;

    /// <summary>Experimental: "Set as P3 · looks like P1: site down" when what the customer wrote sounds more urgent than the priority
    /// (Experimental page). Plain rules on this PC; ticket text never leaves it.</summary>
    public bool GuessPriority { get; set; }

    /// <summary>Experimental: a customer's plain thank-you notifies without sound and briefly; Catch up and colleagues' tickets skip it.</summary>
    public bool QuietThankYous { get; set; }

    /// <summary>A feature that uses the language model and 90 days of tickets is on.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsTicketHistory => GuessPriority;

    /// <summary>SLA targets changed from the team's (Notifications → SLA); empty: the team's targets.</summary>
    public List<Tracking.SlaOverride> SlaOverrides { get; set; } = [];

    // Appearance of the notifications
    // Ticket notifications: the sound by type; how long they stay is by length and priority (TicketDisplayLength), so their Display isn't used.
    // Silent by default (since 1.3.19): most people turn sounds off; they're one click away under Look & sound.
    // Except new tickets: a very quiet chime, so one isn't missed while you look elsewhere. Saved settings keep the
    // sounds chosen before.
    public NotificationStyle NewTicketStyle { get; set; } = new() { Sound = ToastSound.SoftChime };
    public NotificationStyle MyTicketStyle { get; set; } = new() { Sound = ToastSound.Silent };
    public NotificationStyle HelpStyle { get; set; } = new() { Sound = ToastSound.Silent };

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
    public NotificationStyle AppMessageStyle { get; set; } = new() { Sound = ToastSound.Silent, Display = ToastDisplay.Long };


    /// <summary>The sound: app messages and the summary by their style, ticket notifications by type.</summary>
    public ToastSound SoundFor(UI.Popups.NotificationContent content) =>
        content.Quiet && !content.Important ? ToastSound.Silent
        : content.Style is null && content.Sound is { } clientSound ? clientSound : (content.Style ?? StyleFor(content.Kind)).Sound;

    /// <summary>
    /// When your working day starts, Catch up runs once by itself: what came in while you were off and still needs
    /// attention (still unassigned, customers still waiting). On by default; Notifications → Working hours.
    /// </summary>
    public bool CatchUpAtDayStart { get; set; } = true;

    /// <summary>How long ticket notifications stay on screen: Short, Normal or Long, then by priority (all ticket types).</summary>
    public DisplayLength TicketDisplayLength { get; set; } = DisplayLength.Normal;

    /// <summary>The start summary and app messages stay as long as the same Short / Normal / Long choice says.</summary>
    public void ApplyDisplayLength()
    {
        var (summary, app) = TicketDisplayTimes.Others(TicketDisplayLength);
        SummaryStyle.Display = summary;
        AppMessageStyle.Display = app;
    }

    /// <summary>Seconds on screen, 0 = until dismissed: app messages and the summary by their style, ticket notifications by priority.</summary>
    public int DisplaySecondsFor(UI.Popups.NotificationContent content) =>
        content.Style is { } style ? style.Display.Seconds() : content.Important ? 0 : TicketDisplayTimes.For(TicketDisplayLength, content.Quiet ? "low" : content.Priority);

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

    /// <summary>Several notifications at once: below each other (default) or stacked like cards (Look & sound).</summary>
    public PopupStacking Stacking { get; set; } = PopupStacking.List;

    /// <summary>Size of Zendesk Watcher's own notifications in percent (text, buttons and spacing together).</summary>
    public int NotificationSize { get; set; } = 100;

    /// <summary>
    /// The app's sound volume in percent (Look &amp; sound): half by default, so a notification doesn't startle someone
    /// with headphones on loud; it only affects this app.
    /// </summary>
    public int SoundVolume { get; set; } = 50;

    /// <summary>Popups don't count down while the screen is locked or there's been no input for a minute.</summary>
    public bool PausePopupsWhenAway { get; set; } = true;

    /// <summary>A short glow at the screen edge for Urgent, important clients and SLA breaches (Look & sound).</summary>
    public bool EdgeGlow { get; set; }

    /// <summary>
    /// Notifications that waited this long (in the line, or on screen while away) are checked against Zendesk before
    /// they show, and dropped when no longer needed.
    /// </summary>
    public static readonly TimeSpan RecheckWaitingAfter = TimeSpan.FromMinutes(15);

    /// <summary>Coming back to more than this many waiting notifications: one summary instead.</summary>
    public const int SummarizeWaitingAbove = 5;

    /// <summary>
    /// A customer reply counts as handled once an agent answered it publicly (colleagues' tickets), and changes on
    /// your own / CC'd tickets once you've acted on the ticket yourself. Used by the checks above, Snooze, and
    /// "Colleague's ticket" (no notification when a colleague already answered).
    /// </summary>
    public bool DropAnswered { get; set; } = true;

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
        Clients.ForEach(c => c.Upgrade());
        Clients.RemoveAll(c => c.IsDefaultAt(DateTimeOffset.UtcNow)); // e.g. a mute for a day that has run out
        WorkdayStartHour = Math.Clamp(WorkdayStartHour, 0, 23);
        WorkdayEndHour = Math.Clamp(WorkdayEndHour, WorkdayStartHour + 1, 24);
        MyWorkdayStartHour = Math.Clamp(MyWorkdayStartHour, 0, 23);
        MyWorkdayEndHour = Math.Clamp(MyWorkdayEndHour, MyWorkdayStartHour + 1, 24);
        if (!Enum.IsDefined(Position)) Position = PopupPosition.BottomRight;
        if (!Enum.IsDefined(Stacking)) Stacking = PopupStacking.List;
        NotificationSize = Math.Clamp(NotificationSize, 100, 200);
        SoundVolume = Math.Clamp(SoundVolume, 0, 100);
        if (!Enum.IsDefined(NewTicketStyle.Sound)) NewTicketStyle.Sound = ToastSound.Silent;
        if (!Enum.IsDefined(MyTicketStyle.Sound)) MyTicketStyle.Sound = ToastSound.Silent;
        if (!Enum.IsDefined(HelpStyle.Sound)) HelpStyle.Sound = ToastSound.Silent;
        // Before 1.3.19 the own-region untick happened when Help colleagues was first switched on.
        HelpRegionDefaultDone ??= HelpColleagues || HelpExcludedAssignees.Count > 0 ? true : null;
        if (!Enum.IsDefined(TicketDisplayLength)) TicketDisplayLength = DisplayLength.Normal;
        ApplyDisplayLength();
    }

    public void Save() => Save(FilePath);

    /// <summary>
    /// Writes a temp file first and then swaps it in, keeping the previous file as settings.json.bak: a crash or a
    /// full disk while saving never leaves a half-written settings.json.
    /// </summary>
    internal void Save(string path)
    {
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

/// <summary>How a client's tickets notify (Clients page). Stored as a number: new values go at the end.</summary>
public enum ClientMode
{
    /// <summary>Like everyone else (the default).</summary>
    Normal,

    /// <summary>
    /// New tickets and customer replies on colleagues' tickets stay quiet. Only as the mode right now
    /// (<see cref="ClientChoice.ModeAt"/>): muting is <see cref="ClientChoice.IsMuted"/>, on top of the client's own mode.
    /// </summary>
    Muted,

    /// <summary>Only Urgent (P1) and High (P2) new tickets and colleagues' tickets notify.</summary>
    OnlyUrgent,

    /// <summary>Always notify (also outside your hours and for priorities left out), stay until dismissed, ★ in the heading.</summary>
    Important
}

/// <summary>A client (Zendesk organization) with a choice that isn't the default, with the name as last seen.</summary>
public sealed class ClientChoice
{
    /// <summary>Pseudo client for tickets without an organization (Zendesk ids are always positive).</summary>
    public const long NoClientId = 0;
    public const string NoClientName = "(No client)";

    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The group it was set in, so the Clients list can show it there even before it's loaded.</summary>
    public long GroupId { get; set; }

    /// <summary>The client's own choice: Normal, Only urgent or Important. Kept while muted, so unmuting brings it back.</summary>
    public ClientMode Mode { get; set; }

    /// <summary>Muted (until <see cref="MutedUntil"/>, if set), whatever <see cref="Mode"/> is.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Muted for a while: back to <see cref="Mode"/> after this. Null: for good.</summary>
    public DateTimeOffset? MutedUntil { get; set; }

    /// <summary>Its own notification sound; null uses the sound of the notification's type.</summary>
    public ToastSound? Sound { get; set; }

    /// <summary>Muted right now (a mute for a while that has run out isn't).</summary>
    public bool IsMutedAt(DateTimeOffset now) => IsMuted && (MutedUntil is not { } until || until > now);

    /// <summary>The mode right now: Muted while muted, otherwise the client's own choice.</summary>
    public ClientMode ModeAt(DateTimeOffset now) =>
        IsMutedAt(now) ? ClientMode.Muted : Mode == ClientMode.Muted ? ClientMode.Normal : Mode;

    /// <summary>Nothing set (or only a mute that has run out): not worth storing.</summary>
    public bool IsDefaultAt(DateTimeOffset now) => ModeAt(now) == ClientMode.Normal && Sound is null;

    /// <summary>A file from a test build stored "muted" as the mode: muted now means <see cref="IsMuted"/>.</summary>
    internal void Upgrade()
    {
        if (Mode != ClientMode.Muted)
            return;
        Mode = ClientMode.Normal;
        IsMuted = true;
    }

    public ClientChoice Copy() => (ClientChoice)MemberwiseClone();
}

/// <summary>A Zendesk agent, by id, with the name as last seen.</summary>
public sealed class Colleague
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class NotificationStyle
{
    public ToastSound Sound { get; set; } = ToastSound.Silent;
    public ToastDisplay Display { get; set; } = ToastDisplay.Standard;
}

/// <summary>Windows sounds, then chiptune jingles (added last: settings store the number).</summary>
public enum ToastSound { Default, Mail, InstantMessage, Reminder, Sms, Silent, ChipBlip, ChipArpeggio, ChipBounce, ChipAlert, ChipFanfare,
    SongBouncy, SongGroove, SongCelebration, SongQuest, SongTurbo, SongStarfield, SoftChime,
    AmericanaBanjoRoad, AmericanaSlide, AmericanaHollow, NatureStream, NatureWindChimes, NatureBirdsong }

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

/// <summary>How long ticket notifications stay (Notifications → Behavior); Normal first, so it's the default.</summary>
public enum DisplayLength { Normal, Short, Long }

/// <summary>Seconds a ticket notification stays on screen, by length and priority; 0 = until dismissed.</summary>
public static class TicketDisplayTimes
{
    /// <summary>The start summary and app messages: Short 5 s / 10 s, Normal 10 s / 25 s, Long 25 s / until closed.</summary>
    public static (ToastDisplay Summary, ToastDisplay AppMessages) Others(DisplayLength length) => length switch
    {
        DisplayLength.Short => (ToastDisplay.Standard, ToastDisplay.Medium),
        DisplayLength.Long => (ToastDisplay.Long, ToastDisplay.UntilDismissed),
        _ => (ToastDisplay.Medium, ToastDisplay.Long)
    };

    /// <summary>Urgent always stays until dismissed; the others longer the higher the priority. No priority counts as normal.</summary>
    public static int For(DisplayLength length, string? priority)
    {
        var (high, normal, low) = length switch
        {
            DisplayLength.Short => (30, 15, 10),
            DisplayLength.Long => (0, 60, 30),
            _ => (60, 30, 15)
        };
        return priority?.ToLowerInvariant() switch
        {
            "urgent" => 0,
            "high" => high,
            "low" => low,
            _ => normal
        };
    }
}

public enum NotificationTheme
{
    NeonDark,
    Terminal,
    CleanLight,

    /// <summary>
    /// Native: like Windows 11, a bit edgier; follows Windows' light/dark and accent color. (Was the Windows toast style
    /// until 1.7.0; the stored number stays, so those settings get Native.)
    /// </summary>
    WindowsNative,

    /// <summary>Dusky purple with warm cream text and soft pastel accents. (Added last: settings store the number.)</summary>
    CozyPurple,

    /// <summary>The Optimizely brand pack: light green-grey and white, Dark Fir text, lime accents. (Added last: settings store the number.)</summary>
    Optimizely,

    /// <summary>For late evenings: warm near-black, dimmed cream text, muted colors, no glow. (Added last: settings store the number.)</summary>
    Night,

    /// <summary>Classic 80s home computer: deep blue, white text, orange accents. (Added last: settings store the number.)</summary>
    Retro
}

/// <summary>Where Zendesk Watcher's own notifications appear. Stored as a number: new values go at the end.</summary>
/// <summary>Where notifications appear. New values go at the end: settings store the number.</summary>
public enum PopupPosition { BottomRight, TopRight, TopCenter, LeftCenter, Center, BottomCenter, RightCenter }

/// <summary>How several notifications on screen at once are arranged.</summary>
public enum PopupStacking
{
    /// <summary>Below (or above) each other, all visible (the default).</summary>
    List,

    /// <summary>Like a deck of cards: the newest in front, older ones behind it with their edge peeking out.</summary>
    Deck,

    /// <summary>
    /// Tucked away at the screen edge with only a tab showing; the mouse over it slides it out. Needs an edge, so not
    /// with <see cref="PopupPosition.Center"/> (Top center is used then).
    /// </summary>
    Peek,

    /// <summary>A small pill at the top center ("2 new · 1 P1"); pointing at it opens the notifications below it. Always top center.</summary>
    Pill,

    /// <summary>A thin bar along the bottom (or top) of the screen scrolling the notifications' one-line text like a news ticker.</summary>
    Ticker,

    /// <summary>Game style: slides up at the bottom center with a bounce, a trophy and a short tune; one at a time. Always bottom center.</summary>
    Achievement
}
