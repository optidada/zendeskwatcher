using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

/// <summary>
/// Shows notifications in Zendesk Watcher's own styles (popups in the chosen theme).
/// While presenting or in full screen, popups wait until it's over, and nothing makes a sound.
/// </summary>
public sealed class Notifier : INotificationSink
{
    public const string ActionOpenTicket = NotificationActions.OpenTicket;
    public const string ActionOpenZendesk = NotificationActions.OpenZendesk;
    public const string ActionSignIn = NotificationActions.SignIn;
    public const string ActionNone = NotificationActions.None;
    public const string ActionInfo = NotificationActions.Info;
    public const string ActionUpdate = NotificationActions.Update;
    public const string ActionTake = NotificationActions.Take;
    public const string ActionCatchUp = NotificationActions.CatchUp;
    public const string ActionSettings = NotificationActions.Settings;

    private readonly PopupManager _popups;

    public Notifier(Action<string, long?> onAction) =>
        _popups = new PopupManager(onAction, Snooze, OnPopupClosed, Copy)
        {
            Shown = OnShown,
            ClientChosen = (content, choice) => { if (content.IsTicketNotification) ChooseForClient(choice); } // samples: nothing to change
        };

    /// <summary>
    /// A choice from the "Mute ▾" menu on a notification (mute, mute for a while, only urgent, important): the app
    /// saves it for the client (Clients page).
    /// </summary>
    public event Action<ClientChoice>? ClientChoiceRequested;

    private void ChooseForClient(ClientChoice choice)
    {
        Log.Info($"Client {choice.Name} ({choice.Id}) set to {choice.Mode}{(choice.IsMuted ? ", muted" : "")}{(choice.IsMuted && choice.MutedUntil is { } until ? $" until {until.ToLocalTime():yyyy-MM-dd HH:mm}" : "")} from a notification");
        ClientChoiceRequested?.Invoke(choice);
    }

    /// <summary>Counts what happened to a ticket notification, per kind and (for the Insights suggestion) per client.</summary>
    private void Count(NotificationContent content, StatOutcome outcome)
    {
        Stats.Record(content.Kind, outcome);
        if (content.ClientId is { } id && content.ClientName is { } name)
            Stats.RecordClient(id, name, outcome);
        if (IsAction(outcome))
            MarkActed(content);
    }

    /// <summary>Opened, took or copied: acted on. Snoozing, closing and only reading it aren't.</summary>
    private static bool IsAction(StatOutcome outcome) =>
        outcome is StatOutcome.OpenedTicket or StatOutcome.OpenedZendesk or StatOutcome.Took or StatOutcome.Copied;

    /// <summary>Notifications already counted as acted on (by <see cref="NotificationContent.StatId"/>, so snoozed repeats too).</summary>
    private readonly HashSet<Guid> _acted = [];

    /// <summary>Counts a ticket notification as acted on, once.</summary>
    private void MarkActed(NotificationContent content)
    {
        if (!content.IsTicketNotification)
            return;
        if (_acted.Count > 5000)
            _acted.Clear(); // a long-running app: old notifications are long gone
        if (_acted.Add(content.StatId))
            Count(content, StatOutcome.Acted);
    }

    /// <summary>A notification appeared on screen: ticket notifications count in the statistics (snoozed repeats don't).</summary>
    private void OnShown(NotificationContent content)
    {
        if (content.IsTicketNotification && !content.IsSnoozedRepeat)
            Count(content, StatOutcome.Shown);
    }

    /// <summary>Copies a ticket notification for Teams and counts it. Runs on the UI thread.</summary>
    public void Copy(NotificationContent content)
    {
        ChipSounds.Stop();
        try
        {
            TicketShare.Copy(content, content.TicketId is { } id ? Settings.TicketUrl(id) : null);
            if (content.IsTicketNotification)
                Count(content, StatOutcome.Copied);
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy the notification", ex);
        }
    }

    /// <summary>Where ticket notifications and what was done with them are counted.</summary>
    public NotificationStats Stats { get; set; } = NotificationStats.Shared;

    private void OnPopupClosed(NotificationContent content, PopupForm.Result result)
    {
        if (result is not (PopupForm.Result.Timeout or PopupForm.Result.None))
            ChipSounds.Stop(); // acted on: the music can stop
        if (content.IsTicketNotification && StatForPopup(result, content.ResolvedAction) is { } outcome)
            Count(content, outcome);
    }

    /// <summary>What a closed popup means, or null if it isn't worth counting.</summary>
    public static StatOutcome? StatForPopup(PopupForm.Result result, string action) => result switch
    {
        PopupForm.Result.Open when action == ActionOpenTicket => StatOutcome.OpenedTicket,
        PopupForm.Result.Open when action == ActionOpenZendesk || action.StartsWith(NotificationActions.SearchPrefix, StringComparison.Ordinal) => StatOutcome.OpenedZendesk,
        PopupForm.Result.Take => StatOutcome.Took,
        PopupForm.Result.Snooze => StatOutcome.Snoozed,
        PopupForm.Result.Dismiss => StatOutcome.Closed,
        PopupForm.Result.Timeout => StatOutcome.TimedOut,
        _ => null
    };

    /// <summary>While paused, changes are still recorded but nothing is shown. Snoozed popups wait for the end of the pause.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value)
                ShowHeldSnoozes();
        }
    }

    private bool _paused;

    /// <summary>
    /// Of the given notifications, the ones still needed (deep: also read what happened on the ticket since, see
    /// <see cref="Settings.DropAnswered"/>). Set by the app; without it (or if the check fails) everything shows.
    /// Used for "only if still needed" snoozes and for notifications that waited long.
    /// </summary>
    public Func<IReadOnlyList<NotificationContent>, bool, Task<List<NotificationContent>>>? StillNeeded
    {
        get => _stillNeeded;
        set => _popups.StillNeeded = _stillNeeded = value;
    }

    private Func<IReadOnlyList<NotificationContent>, bool, Task<List<NotificationContent>>>? _stillNeeded;

    // ------------------------------------------------------------------ snooze

    private readonly List<NotificationContent> _heldSnoozes = [];

    /// <summary>The user snoozed a popup: show it again after the snooze time, if it still applies then.</summary>
    private void Snooze(NotificationContent content)
    {
        var minutes = Math.Clamp(Settings.SnoozeMinutes, 1, 24 * 60);
        Log.Info($"Snoozed for {minutes} min: {content.Title}");
        var timer = new System.Windows.Forms.Timer { Interval = minutes * 60_000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (Paused)
                _heldSnoozes.Add(content);
            else
                _ = ShowSnoozedAsync(content);
        };
        timer.Start();
    }

    private void ShowHeldSnoozes()
    {
        var held = _heldSnoozes.ToList();
        _heldSnoozes.Clear();
        foreach (var content in held)
            _ = ShowSnoozedAsync(content);
    }

    private async Task ShowSnoozedAsync(NotificationContent content)
    {
        if (content.TicketId is not null && StillNeeded is { } check) // only if the ticket still needs attention
        {
            try
            {
                if ((await check([content], true)).Count == 0)
                {
                    Log.Info($"Snoozed notification no longer needed, not shown again: {content.Title}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not check a snoozed ticket; showing it again anyway", ex);
            }
        }

        if (Paused)
        {
            _heldSnoozes.Add(content); // paused while checking
            return;
        }
        var heading = content.Heading.StartsWith("Snoozed · ") ? content.Heading : $"Snoozed · {content.Heading}";
        Show(content with { Heading = heading, IsSnoozedRepeat = true }, Settings);
    }

    /// <summary>The settings that decide theme, sound, display time, logo and grouping.</summary>
    public Settings Settings { get; set; } = new();

    public void Show(NotificationContent content)
    {
        if (!Paused || content.RequestedByUser)
            Show(content, Settings);
    }

    // Replies to something the user just clicked ("No updates", "already taken") stay quiet and short.
    private static readonly NotificationStyle QuietInfo = new() { Sound = ToastSound.Silent, Display = ToastDisplay.Standard };

    /// <summary>App messages (update ready, welcome, group not found) as chosen under Appearance.</summary>
    private NotificationStyle ActOnIt => Settings.AppMessageStyle;

    /// <summary>Sign-in required: the App messages sound, but always on screen until dismissed (nothing works until then).</summary>
    private NotificationStyle Blocking => new() { Sound = Settings.AppMessageStyle.Sound, Display = ToastDisplay.UntilDismissed };

    /// <summary>Shown even when paused: the app cannot work until the user signs in.</summary>
    public void ShowSignInRequired() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Sign-in required", "Your Zendesk session has expired", "Click to sign in again.")
    {
        Action = ActionSignIn,
        Style = Blocking
    }, Settings);

    /// <summary>The very first start, while Settings opens: what the app does and how to get going.</summary>
    private const string WelcomeBody =
        "Get started in two steps:\n" +
        "1. Sign in on the **Account** page (Settings is open)\n" +
        "2. Choose groups to watch on the **Groups** page";

    /// <summary>The very first start: what to do next. Click opens Settings.</summary>
    public void ShowWelcome() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", "Welcome to Zendesk Watcher", WelcomeBody)
    {
        Action = ActionSettings,
        Style = ActOnIt
    }, Settings);

    /// <summary>settings.json was damaged at start (see <see cref="Settings.Load()"/>). Shown even when paused; click opens Settings.</summary>
    public void ShowSettingsProblem(string text) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", "Settings file damaged", text)
    {
        Action = ActionSettings,
        Style = Blocking
    }, Settings);

    /// <summary>Monitored groups that Zendesk no longer lists. Shown even when paused; click opens Settings.</summary>
    public void ShowMissingGroups(IReadOnlyCollection<string> names) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher",
        names.Count == 1 ? "A monitored group was not found" : $"{names.Count} monitored groups were not found",
        $"{string.Join(", ", names)}: deleted, or you no longer have access. Open Settings to untick {(names.Count == 1 ? "it" : "them")}.")
    {
        Action = ActionSettings,
        Style = ActOnIt
    }, Settings);

    /// <summary>The start summary, with the sound and duration chosen in Settings (silent, 10 seconds by default). Click opens Zendesk.</summary>
    public void ShowSummary(NotificationContent content) => Show(content with
    {
        Action = ActionOpenZendesk,
        MaxBodyLines = 9, // running text: a few more lines than a list
        CanCatchUp = true,
        Style = Settings.SummaryStyle
    });

    /// <summary>
    /// "While you were away": one notification instead of many waiting ones, e.g. "12 new tickets" / "3 updates on your
    /// tickets" / "2 customers waiting on colleagues' tickets". Click opens Zendesk.
    /// </summary>
    internal static NotificationContent AwaySummary(IReadOnlyList<NotificationContent> waiting, int dropped)
    {
        int Count(NotificationKind kind) => waiting.Where(c => c.Kind == kind).Sum(c => c.TicketCount);
        static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        var lines = new List<string>();
        if (Count(NotificationKind.NewTicket) is > 0 and var fresh)
            lines.Add(Plural(fresh, "new ticket", "new tickets") +
                Tracking.TicketRules.ScheduledNote(fresh, waiting.Where(c => c.Kind == NotificationKind.NewTicket).Sum(c => c.ScheduledCount)));
        if (Count(NotificationKind.MyTicket) is > 0 and var mine)
            lines.Add(Plural(mine, "update on your tickets", "updates on your tickets"));
        if (Count(NotificationKind.CcTicket) is > 0 and var cc)
            lines.Add(Plural(cc, "update on CC'd tickets", "updates on CC'd tickets"));
        if (Count(NotificationKind.HelpColleague) is > 0 and var help)
            lines.Add(Plural(help, "customer waiting on a colleague's ticket", "customers waiting on colleagues' tickets"));
        if (dropped > 0)
            lines.Add($"{Plural(dropped, "other is", "others are")} no longer needed (taken, solved or answered)");

        return new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "While you were away", string.Join("\n", lines))
        {
            Action = ActionOpenZendesk,
            MaxBodyLines = 7
        };
    }

    /// <summary>The app was started again while running: a short, quiet message with only Close. Shown even when paused.</summary>
    public void ShowAlreadyRunning() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", "Zendesk Watcher is already running",
        "It's the icon in the system tray, next to the clock.")
    {
        Action = ActionInfo,
        Style = QuietInfo
    }, Settings);

    /// <summary>Feedback on something the user did (e.g. "Take it"). Shown even when paused.</summary>
    public void ShowInfo(string title, string body, long? ticketId = null) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", title, body, ticketId)
    {
        Action = ticketId is null ? ActionInfo : null,
        Style = QuietInfo
    }, Settings);

    /// <summary>A new version has been downloaded; clicking restarts into it.</summary>
    public void ShowUpdateReady(string version, string? highlights = null) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Update ready", $"Zendesk Watcher {version} is ready",
        (highlights is null ? "" : highlights + "\n") + "Click to restart and update now, or it installs on the next start.")
    {
        Action = ActionUpdate,
        Style = ActOnIt
    });

    /// <summary>Sample notifications with the given (possibly unsaved) settings. Shown even when paused.</summary>
    public void ShowTest(Settings settings, NotificationKind kind)
    {
        foreach (var sample in TestSamples(kind))
            Show(WithGuess(sample, settings), settings);
    }

    /// <summary>Several samples at once, to see how popups stack.</summary>
    public void ShowTestBurst(Settings settings)
    {
        foreach (var sample in TestSamples(null))
            Show(WithGuess(sample, settings), settings);
    }

    /// <summary>With the priority guess on (Experimental page), the colleague sample gets it, from the same rules as real tickets.</summary>
    private static NotificationContent WithGuess(NotificationContent sample, Settings settings) =>
        settings.GuessPriority && sample.Kind == NotificationKind.HelpColleague
            ? sample with { PriorityHint = Tracking.PriorityGuess.Guess(sample.Priority, sample.Title,
                [Markdown.Strip(sample.Body.Split('\n')[0])], "slapremium", TimeSpan.Zero) }
            : sample;

    /// <summary>
    /// What "Try it" shows: the samples of one kind, or several at once (<paramref name="kind"/> null). Never marked as
    /// ticket notifications, so they don't count in the statistics.
    /// </summary>
    internal static IEnumerable<NotificationContent> TestSamples(NotificationKind? kind) => (kind is { } single
        ? Samples(single)
        : Samples(NotificationKind.NewTicket).Concat(Samples(NotificationKind.MyTicket)).Concat(Samples(NotificationKind.HelpColleague)).Append(
            new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123458 Scheduled publishing stuck in queue",
                $"Robin Client · Created {Clock.Time(DateTime.Now.AddMinutes(-2))}", TicketId: 123458, Priority: "urgent", Context: "Support Tier 1")
            { Action = ActionNone, CanTake = true, EventTime = DateTimeOffset.UtcNow.AddMinutes(-2), Client = "Northwind" }))
        .Select(sample => sample with { Priority = RandomPriority() }).ToList();

    /// <summary>
    /// A random priority for each sample (sometimes none), so "Try it" shows the colors and how long each priority stays
    /// (Behavior → On screen) instead of always the same.
    /// </summary>
    private static string? RandomPriority() => Random.Shared.Next(5) switch
    {
        0 => "urgent",
        1 => "high",
        2 => "normal",
        3 => "low",
        _ => null
    };

    /// <summary>All samples (for the developer --render-popups helper).</summary>
    public static IEnumerable<NotificationContent> AllSamples() =>
        Samples(NotificationKind.NewTicket).Concat(Samples(NotificationKind.MyTicket)).Append(
            new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123458 Scheduled publishing stuck in queue", "",
                TicketId: 123458, Priority: "urgent", Context: "Support Tier 1") { CanTake = true, Client = "Northwind" }).Append(
            new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Good morning, Alex",
                Tracking.TicketRules.SummaryText(Tracking.WatchSnapshot.Sample(),
                    [new WatchedGroup { Id = 1, Name = "Support Tier 2" }, new WatchedGroup { Id = 2, Name = "Premium Support" }],
                    ["urgent", "high"], DateTimeOffset.UtcNow)) { MaxBodyLines = 9, CanCatchUp = true }).Append(
            new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Welcome to Zendesk Watcher", WelcomeBody)
                { Action = ActionSettings });

    /// <summary>Shows "Mute" on samples; muting a sample does nothing (see <see cref="Mute"/>).</summary>
    private static ClientChoice SampleClient(string name) => new() { Id = -1, Name = name };

    private static IEnumerable<NotificationContent> Samples(NotificationKind kind) => kind switch
    {
        NotificationKind.NewTicket =>
          [new NotificationContent(kind, "New ticket", "#123456 Customer cannot publish content after upgrade",
              $"Alex Example · Created {Clock.Time(DateTime.Now.AddMinutes(-7))}", TicketId: 123456, Priority: "high", Context: "Support Tier 2")
              { Action = ActionNone, CanTake = true, EventTime = DateTimeOffset.UtcNow.AddMinutes(-7), Client = "Contoso", MuteTarget = SampleClient("Contoso"),
                SlaDue = DateTimeOffset.UtcNow.AddMinutes(53), SlaFirstReply = true }],
        NotificationKind.HelpColleague =>
          [new NotificationContent(kind, "Customer waiting", "#123459 Checkout fails for all customers",
              "**Sam Customer** replied: Any update? This is blocking our launch tomorrow.\nAshwin is off work · Thu 21:40 their time",
              TicketId: 123459, Priority: "urgent", Context: "Support Tier 2")
              { Action = ActionNone, Client = "Northwind · Premium Support", MuteTarget = SampleClient("Northwind"), SlaDue = DateTimeOffset.UtcNow.AddMinutes(-25) }],
        _ =>
          [new NotificationContent(kind, "Ticket updated", "#123457 Search results missing after deploy",
              "**Jane Doe** replied: Thanks, the reindex fixed it on our side, but one site still shows old results…",
              TicketId: 123457, Priority: "normal") { Action = ActionNone, Client = "Fabrikam", SlaDue = DateTimeOffset.UtcNow.AddMinutes(190) }]
    };

    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTime> _recent = [];

    /// <summary>True if the same notification was already shown within the last minute (then it's skipped).</summary>
    private bool IsDuplicate(NotificationContent content)
    {
        if (content.ResolvedAction is ActionNone or ActionInfo)
            return false; // test samples: you may want to see the same one again right away (e.g. in another theme)
        if (content.RequestedByUser)
            return false; // asked for (Catch up): always answered; the same one still on screen is replaced

        var now = DateTime.UtcNow;
        foreach (var old in _recent.Where(kv => now - kv.Value > DuplicateWindow).Select(kv => kv.Key).ToList())
            _recent.Remove(old);

        var key = string.Join("|", content.Kind, content.ResolvedAction, content.TicketId, content.Title, content.Body);
        if (_recent.ContainsKey(key))
            return true;
        _recent[key] = now;
        return false;
    }

    private void Show(NotificationContent content, Settings settings)
    {
        if (IsDuplicate(content))
        {
            Log.Info($"Skipped a duplicate notification: {content.Title}");
            return;
        }
        if (content.RequestedByUser)
            _popups.CloseSame(content); // Catch up again: the new one takes the old one's place

        try
        {
            // Counted as shown once it's really on screen: a notification that waits in line and is dropped
            // (no longer needed) never counts. The popup manager calls OnShown when it appears. While you present or
            // use a full-screen app it's held, and shown when that's over.
            _popups.Show(content, settings);
        }
        catch (Exception ex)
        {
            Log.Error("Could not show notification", ex);
        }
    }
}
