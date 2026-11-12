using Microsoft.Toolkit.Uwp.Notifications;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

public enum NotificationKind
{
    /// <summary>New unassigned tickets in a monitored group.</summary>
    NewTicket,

    /// <summary>Changes on a ticket assigned to me (or that I'm CC'd on / follow).</summary>
    MyTicket,

    /// <summary>"Help colleagues": a customer replied on a colleague's ticket while they're off work.</summary>
    HelpColleague
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

    private static readonly Lazy<Uri> Logo = new(CreateLogo);
    private readonly PopupManager _popups;

    public Notifier(Action<string, long?> onAction) => _popups = new PopupManager(onAction, Snooze);

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
    /// For "only if still needed": whether the snoozed notification's ticket still needs attention. Set by the app;
    /// without it (or if the check fails) a snoozed popup simply shows again.
    /// </summary>
    public Func<NotificationContent, Task<bool>>? StillNeeded { get; set; }

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
                if (!await check(content))
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
        Show(content with { Heading = heading }, Settings);
    }

    /// <summary>The settings that decide theme, sound, display time, logo and grouping.</summary>
    public Settings Settings { get; set; } = new();

    public void Show(NotificationContent content)
    {
        if (!Paused)
            Show(content, Settings);
    }

    // App messages have their own fixed behavior, independent of the ticket styles in Appearance.
    private static readonly NotificationStyle QuietInfo = new() { Sound = ToastSound.Silent, Display = ToastDisplay.Standard };
    private static readonly NotificationStyle ActOnIt = new() { Sound = ToastSound.Default, Display = ToastDisplay.Long };
    private static readonly NotificationStyle Blocking = new() { Sound = ToastSound.Default, Display = ToastDisplay.UntilDismissed };

    /// <summary>Shown even when paused: the app cannot work until the user signs in.</summary>
    public void ShowSignInRequired() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Sign-in required", "Your Zendesk session has expired", "Click to sign in again.")
    {
        Action = ActionSignIn,
        Style = Blocking
    }, Settings);

    /// <summary>Monitored groups that Zendesk no longer lists. Shown even when paused; click opens Settings.</summary>
    /// <summary>The very first start: what to do next. Click opens Settings.</summary>
    public void ShowWelcome() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", "Welcome to Zendesk Watcher",
        "Enter your Zendesk subdomain (e.g. episerver) and sign in. Then pick the groups to watch, and make the " +
        "notifications yours under Notifications and Appearance.")
    {
        Action = ActionSettings,
        Style = ActOnIt
    }, Settings);

    public void ShowMissingGroups(IReadOnlyCollection<string> names) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher",
        names.Count == 1 ? "A monitored group was not found" : $"{names.Count} monitored groups were not found",
        $"{string.Join(", ", names)}: deleted, or you no longer have access. Open Settings to untick {(names.Count == 1 ? "it" : "them")}.")
    {
        Action = ActionSettings,
        Style = ActOnIt
    }, Settings);

    /// <summary>The start summary: silent, but on screen long enough to read. Click opens Zendesk.</summary>
    public void ShowSummary(NotificationContent content) => Show(content with
    {
        Action = ActionOpenZendesk,
        MaxBodyLines = 7,
        Style = new NotificationStyle { Sound = ToastSound.Silent, Display = ToastDisplay.Medium }
    });

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
        foreach (var sample in Samples(kind))
            Show(sample, settings);
    }

    /// <summary>Several samples at once, to see how popups stack.</summary>
    public void ShowTestBurst(Settings settings)
    {
        foreach (var sample in Samples(NotificationKind.NewTicket).Concat(Samples(NotificationKind.MyTicket)).Concat(
                     [new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123458 Scheduled publishing stuck in queue",
                         "", Priority: "urgent", Context: "Support Tier 1") { Action = ActionNone, CanTake = true }]))
        {
            Show(sample, settings);
        }
    }

    /// <summary>All samples (for the developer --render-popups helper).</summary>
    public static IEnumerable<NotificationContent> AllSamples() =>
        Samples(NotificationKind.NewTicket).Concat(Samples(NotificationKind.MyTicket)).Append(
            new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123458 Scheduled publishing stuck in queue", "",
                Priority: "urgent", Context: "Support Tier 1") { CanTake = true }).Append(
            new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Good morning, Alex",
                "5 unassigned tickets waiting\nSupport Tier 2: 3 · Premium Support: 2\nOldest: 2 h 10 min ago\nYour tickets: 3 open, 4 pending\n" +
                "CC / following: 2 tickets\nHelp colleagues: Urgent, High") { MaxBodyLines = 7 });

    private static IEnumerable<NotificationContent> Samples(NotificationKind kind) => kind switch
    {
        NotificationKind.NewTicket =>
          [new NotificationContent(kind, "New ticket", "#123456 Customer cannot publish content after upgrade",
              $"Requester: Alex Example · Created {DateTime.Now.AddMinutes(-7):HH:mm}", Priority: "high", Context: "Support Tier 2") { Action = ActionNone, CanTake = true, EventTime = DateTimeOffset.UtcNow.AddMinutes(-7) }],
        NotificationKind.HelpColleague =>
          [new NotificationContent(kind, "Customer waiting", "#123459 Checkout fails for all customers",
              "Sam Customer replied: Any update? This is blocking our launch tomorrow.\nAshwin is off work · Thu 21:40 their time",
              Priority: "urgent", Context: "Support Tier 2") { Action = ActionNone }],
        _ =>
          [new NotificationContent(kind, "Ticket updated", "#123457 Search results missing after deploy",
              "Jane Doe replied: Thanks, the reindex fixed it on our side, but one site still shows old results…\nStatus Pending → Open (Jane Doe)",
              Priority: "normal") { Action = ActionNone }]
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
            if (settings.Theme != NotificationTheme.WindowsNative && PopupManager.CanShowPopups())
                _popups.Show(content, settings);
            else
                ShowToast(content, settings);
        }
        catch (Exception ex)
        {
            Log.Error("Could not show notification", ex);
        }
    }

    // ------------------------------------------------------------------ Windows toasts

    private static void ShowToast(NotificationContent content, Settings settings)
    {
        var style = content.Style ?? settings.StyleFor(content.Kind);
        var action = content.ResolvedAction;

        var heading = content.Context is null ? content.Heading : $"{content.Heading} · {content.Context}";
        var builder = new ToastContentBuilder()
            .AddArgument("action", action)
            .AddText(heading)
            .AddText(content.Title);

        var body = content.DisplayBody(DateTimeOffset.UtcNow);
        if (!string.IsNullOrWhiteSpace(body))
            builder.AddText(body);
        builder.AddAttributionText(content.PriorityLabel is { } priority ? $"Zendesk Watcher · {priority}" : "Zendesk Watcher");

        if (content.TicketId is { } id)
            builder.AddArgument("ticketId", id);

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
            builder.AddButton(new ToastButton()
                .SetContent("Take it")
                .AddArgument("action", ActionTake)
                .AddArgument("ticketId", takeTicket));
        }

        var button = new ToastButton()
            .SetContent(action switch
            {
                ActionSignIn => "Sign in",
                ActionUpdate => "Update now",
                ActionSettings => "Open Settings",
                ActionInfo => "OK",
                _ => content.TicketId is null ? "Open Zendesk" : "Open ticket"
            })
            .AddArgument("action", action);
        if (content.TicketId is { } buttonTicket)
            button.AddArgument("ticketId", buttonTicket);
        builder.AddButton(button);

        switch (style.Display)
        {
            case ToastDisplay.Long:
                builder.SetToastDuration(ToastDuration.Long);
                break;
            case ToastDisplay.UntilDismissed:
                // The reminder scenario keeps the toast on screen; it needs a dismiss button.
                builder.SetToastScenario(ToastScenario.Reminder);
                builder.AddButton(new ToastButtonDismiss());
                break;
        }

        if (style.Sound == ToastSound.Silent)
            builder.AddAudio(new ToastAudio { Silent = true });
        else if (ChipSounds.IsChip(style.Sound))
        {
            builder.AddAudio(new ToastAudio { Silent = true }); // Windows can't play our own sounds in a toast
            ChipSounds.Play(style.Sound);
        }
        else
            builder.AddAudio(new Uri(SoundUri(style.Sound)));

        builder.Show();
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
