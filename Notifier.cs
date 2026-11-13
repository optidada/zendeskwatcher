using Microsoft.Toolkit.Uwp.Notifications;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

public enum NotificationKind
{
    /// <summary>New unassigned tickets in a monitored group.</summary>
    NewTicket,

    /// <summary>Changes on a ticket assigned to me.</summary>
    MyTicket,

    /// <summary>"Help colleagues": a customer replied on a colleague's ticket while they're off work.</summary>
    HelpColleague,

    /// <summary>Changes on a ticket where I'm CC'd or follow (looks like <see cref="MyTicket"/>, has its own sound).</summary>
    CcTicket
}

/// <summary>
/// Shows notifications as custom popups (Neon dark, Terminal, Clean light) or as Windows toasts, depending on the theme.
/// While presenting or in full screen, Windows toasts are used so nothing pops up over the screen.
/// </summary>
/// <summary>Where the ticket checks send notifications; the real <see cref="Notifier"/>, or a recorder in tests.</summary>
public interface INotificationSink
{
    void Show(NotificationContent content);
    void ShowMissingGroups(IReadOnlyCollection<string> names);
}

public sealed class Notifier : INotificationSink
{
    public const string ActionOpenTicket = "open";
    public const string ActionOpenZendesk = "zendesk";
    public const string ActionSignIn = "signin";
    public const string ActionNone = "none";
    public const string ActionInfo = "info"; // information only: the button says OK, a click just dismisses it
    public const string ActionUpdate = "update";
    public const string ActionTake = "take";
    public const string ActionSettings = "settings";
    public const string ActionCopy = "copy";

    private static readonly Lazy<Uri> Logo = new(CreateLogo);
    private readonly PopupManager _popups;

    public Notifier(Action<string, long?> onAction) => _popups = new PopupManager(onAction, Snooze, OnPopupClosed, Copy) { Shown = OnShown };

    /// <summary>A notification appeared on screen: ticket notifications count in the statistics (snoozed repeats don't).</summary>
    private void OnShown(NotificationContent content)
    {
        if (content.IsTicketNotification && !content.IsSnoozedRepeat)
            Stats.Record(content.Kind, StatOutcome.Shown);
    }

    /// <summary>Recent ticket notifications by id, so "Copy" on a Windows notification knows what it said.</summary>
    private readonly Dictionary<string, NotificationContent> _copyable = [];
    private readonly Queue<string> _copyableOrder = new();

    /// <summary>Copies a ticket notification for Teams and counts it. Runs on the UI thread.</summary>
    public void Copy(NotificationContent content)
    {
        try
        {
            TicketShare.Copy(content, content.TicketId is { } id ? Settings.TicketUrl(id) : null);
            if (content.IsTicketNotification)
                Stats.Record(content.Kind, StatOutcome.Copied);
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy the notification", ex);
        }
    }

    /// <summary>"Copy" on a Windows notification; false if it's no longer known (e.g. after a restart).</summary>
    public bool CopyFromToast(string copyId)
    {
        if (!_copyable.TryGetValue(copyId, out var content))
            return false;
        Copy(content);
        return true;
    }

    private string RememberForCopy(NotificationContent content)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        _copyable[id] = content;
        _copyableOrder.Enqueue(id);
        while (_copyableOrder.Count > 200)
            _copyable.Remove(_copyableOrder.Dequeue());
        return id;
    }

    /// <summary>Where ticket notifications and what was done with them are counted.</summary>
    public NotificationStats Stats { get; set; } = NotificationStats.Shared;

    private void OnPopupClosed(NotificationContent content, PopupForm.Result result)
    {
        if (content.IsTicketNotification && NotificationStats.ForPopup(result, content.ResolvedAction) is { } outcome)
            Stats.Record(content.Kind, outcome);
    }

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
        if (Settings.SnoozeBehavior == SnoozeBehavior.IfStillNeeded && content.TicketId is not null && StillNeeded is { } check)
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
        if (!Paused)
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

    /// <summary>The very first start, while Settings opens: the two steps to get going.</summary>
    private const string WelcomeBody =
        "Two steps in Settings, which is open now:\n1. **Account**: click Sign in.\n2. **Groups**: tick the groups to watch.\n" +
        "After that, Zendesk Watcher runs in the tray next to the clock.";

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

    /// <summary>The start summary, as chosen under Appearance (silent, about 10 seconds by default). Click opens Zendesk.</summary>
    public void ShowSummary(NotificationContent content) => Show(content with
    {
        Action = ActionOpenZendesk,
        MaxBodyLines = 7,
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
            lines.Add(Plural(fresh, "new ticket", "new tickets"));
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
            Show(sample, settings);
    }

    /// <summary>Several samples at once, to see how popups stack.</summary>
    public void ShowTestBurst(Settings settings)
    {
        foreach (var sample in TestSamples(null))
            Show(sample, settings);
    }

    /// <summary>
    /// What "Try it" shows: the samples of one kind, or several at once (<paramref name="kind"/> null). Never marked as
    /// ticket notifications, so they don't count in the statistics.
    /// </summary>
    internal static IEnumerable<NotificationContent> TestSamples(NotificationKind? kind) => kind is { } single
        ? Samples(single)
        : Samples(NotificationKind.NewTicket).Concat(Samples(NotificationKind.MyTicket)).Concat(Samples(NotificationKind.HelpColleague)).Append(
            new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123458 Scheduled publishing stuck in queue",
                $"Robin Client · Created {DateTime.Now.AddMinutes(-2):HH:mm}", TicketId: 123458, Priority: "urgent", Context: "Support Tier 1")
            { Action = ActionNone, CanTake = true, EventTime = DateTimeOffset.UtcNow.AddMinutes(-2), Client = "Northwind" });

    /// <summary>All samples (for the developer --render-popups helper).</summary>
    public static IEnumerable<NotificationContent> AllSamples() =>
        Samples(NotificationKind.NewTicket).Concat(Samples(NotificationKind.MyTicket)).Append(
            new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123458 Scheduled publishing stuck in queue", "",
                TicketId: 123458, Priority: "urgent", Context: "Support Tier 1") { CanTake = true, Client = "Northwind" }).Append(
            new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Good morning, Alex",
                "**Unassigned:** 5, oldest 2 h 10 min ago\nSupport Tier 2: 3 · Premium Support: 2\n" +
                "**Your tickets:** 3 open, 4 pending\n**CC / following:** 2 tickets\n**Colleagues off work:** customer replies on their Urgent (P1) and High (P2) tickets")
                { MaxBodyLines = 7 }).Append(
            new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Welcome to Zendesk Watcher", WelcomeBody)
                { Action = ActionSettings });

    private static IEnumerable<NotificationContent> Samples(NotificationKind kind) => kind switch
    {
        NotificationKind.NewTicket =>
          [new NotificationContent(kind, "New ticket", "#123456 Customer cannot publish content after upgrade",
              $"Alex Example · Created {DateTime.Now.AddMinutes(-7):HH:mm}", TicketId: 123456, Priority: "high", Context: "Support Tier 2")
              { Action = ActionNone, CanTake = true, EventTime = DateTimeOffset.UtcNow.AddMinutes(-7), Client = "Contoso" }],
        NotificationKind.HelpColleague =>
          [new NotificationContent(kind, "Customer waiting", "#123459 Checkout fails for all customers",
              "**Sam Customer** replied: Any update? This is blocking our launch tomorrow.\nAshwin is off work · Thu 21:40 their time",
              TicketId: 123459, Priority: "urgent", Context: "Support Tier 2") { Action = ActionNone, Client = "Northwind" }],
        _ =>
          [new NotificationContent(kind, "Ticket updated", "#123457 Search results missing after deploy",
              "**Jane Doe** replied: Thanks, the reindex fixed it on our side, but one site still shows old results…\nStatus Pending → Open (Jane Doe)",
              TicketId: 123457, Priority: "normal") { Action = ActionNone, Client = "Fabrikam" }]
    };

    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTime> _recent = [];

    /// <summary>True if the same notification was already shown within the last minute (then it's skipped).</summary>
    private bool IsDuplicate(NotificationContent content)
    {
        if (content.ResolvedAction is ActionNone or ActionInfo)
            return false; // test samples: you may want to see the same one again right away (e.g. in another theme)

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

        try
        {
            // Counted as shown once it's really on screen: a notification that waits in line and is dropped
            // (no longer needed) never counts. The popup manager calls OnShown when it appears.
            if (settings.ActiveNotificationTheme != NotificationTheme.WindowsNative && PopupManager.CanShowPopups())
                _popups.Show(content, settings);
            else
            {
                ShowToast(content, settings);
                OnShown(content);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not show notification", ex);
        }
    }

    // ------------------------------------------------------------------ Windows toasts

    /// <summary>Toast argument with the kind of a ticket notification, so a click on it can be counted.</summary>
    public const string StatArgument = "stat";

    private void ShowToast(NotificationContent content, Settings settings)
    {
        var sound = settings.SoundFor(content);
        var action = content.ResolvedAction;

        var builder = new ToastContentBuilder()
            .AddArgument("action", action)
            .AddText(content.FullHeading)
            .AddText(content.Title);

        // Windows can't show bold: plain text, with the client first.
        var body = Markdown.Strip(content.DisplayBody(DateTimeOffset.UtcNow));
        if (!string.IsNullOrWhiteSpace(content.Client))
            body = string.IsNullOrWhiteSpace(body) ? $"Client: {content.Client}" : $"Client: {content.Client}\n{body}";
        if (!string.IsNullOrWhiteSpace(body))
            builder.AddText(body);
        builder.AddAttributionText("Zendesk Watcher");

        if (content.TicketId is { } id)
            builder.AddArgument("ticketId", id);
        if (content.IsTicketNotification)
            builder.AddArgument(StatArgument, content.Kind.ToString());

        if (settings.ShowAppLogo)
            builder.AddAppLogoOverride(Logo.Value, ToastGenericAppLogoCrop.Circle);

        if (settings.GroupNotificationsByType)
        {
            var (headerId, headerTitle) = content.Kind switch
            {
                NotificationKind.NewTicket => ("new-tickets", "New tickets"),
                NotificationKind.HelpColleague => ("colleagues", "Colleagues' tickets"),
                _ => ("my-tickets", "My tickets")
            };
            builder.AddHeader(headerId, headerTitle, new ToastArguments().Add("action", ActionOpenZendesk));
        }

        if (content.CanTake && content.TicketId is { } takeTicket)
        {
            var take = new ToastButton()
                .SetContent("Take it")
                .AddArgument("action", ActionTake)
                .AddArgument("ticketId", takeTicket);
            if (content.IsTicketNotification)
                take.AddArgument(StatArgument, content.Kind.ToString());
            builder.AddButton(take);
        }

        if (content.IsTicketNotification)
        {
            builder.AddButton(new ToastButton()
                .SetContent("Copy")
                .AddArgument("action", ActionCopy)
                .AddArgument("copyId", RememberForCopy(content))
                .AddArgument(StatArgument, content.Kind.ToString()));
        }

        var button = new ToastButton()
            .SetContent(action switch
            {
                ActionSignIn => "Sign in",
                ActionUpdate => "Update now",
                ActionSettings => "Open Settings",
                ActionInfo => "Close",
                _ => content.TicketId is null ? "Open Zendesk" : "Open ticket"
            })
            .AddArgument("action", action);
        if (content.TicketId is { } buttonTicket)
            button.AddArgument("ticketId", buttonTicket);
        if (content.IsTicketNotification)
            button.AddArgument(StatArgument, content.Kind.ToString());
        builder.AddButton(button);

        // Windows only has short (~5 s), long (~25 s) and until dismissed.
        switch (settings.DisplaySecondsFor(content))
        {
            case 0:
                // The reminder scenario keeps the toast on screen; it needs a dismiss button.
                builder.SetToastScenario(ToastScenario.Reminder);
                builder.AddButton(new ToastButtonDismiss());
                break;
            case > 15:
                builder.SetToastDuration(ToastDuration.Long);
                break;
        }

        if (sound == ToastSound.Silent)
            builder.AddAudio(new ToastAudio { Silent = true });
        else if (ChipSounds.IsChip(sound))
        {
            builder.AddAudio(new ToastAudio { Silent = true }); // Windows can't play our own sounds in a toast
            ChipSounds.Play(sound);
        }
        else
            builder.AddAudio(new Uri(SoundUri(sound)));

        builder.Show(toast =>
        {
            if (!content.IsTicketNotification)
                return;
            // Closed or timed out (only noticed while the app runs). Clicks arrive via OnToastActivated.
            toast.Dismissed += (_, e) =>
            {
                StatOutcome? outcome = e.Reason switch
                {
                    Windows.UI.Notifications.ToastDismissalReason.UserCanceled => StatOutcome.Closed,
                    Windows.UI.Notifications.ToastDismissalReason.TimedOut => StatOutcome.TimedOut,
                    _ => null
                };
                if (outcome is { } o)
                    Stats.Record(content.Kind, o);
            };
        });
    }

    private static string SoundUri(ToastSound sound) => sound switch
    {
        ToastSound.Mail => "ms-winsoundevent:Notification.Mail",
        ToastSound.InstantMessage => "ms-winsoundevent:Notification.IM",
        ToastSound.Reminder => "ms-winsoundevent:Notification.Reminder",
        ToastSound.Sms => "ms-winsoundevent:Notification.SMS",
        _ => "ms-winsoundevent:Notification.Default"
    };

    /// <summary>The app icon as a PNG in the data folder (toasts need an image file for the logo).</summary>
    private static Uri CreateLogo() => new(AppIcon.SavePng(Path.Combine(AppPaths.DataDir, "logo.png"), 96));
}
