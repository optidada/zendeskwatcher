using Microsoft.Toolkit.Uwp.Notifications;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

public enum NotificationKind
{
    /// <summary>New unassigned tickets in a monitored group.</summary>
    NewTicket,

    /// <summary>Changes on a ticket assigned to me.</summary>
    MyTicket
}

/// <summary>
/// Shows notifications as custom popups (Neon dark, Terminal, Clean light) or as Windows toasts, depending on the theme.
/// While presenting or in full screen, Windows toasts are used so nothing pops up over the screen.
/// </summary>
public sealed class Notifier(Action<string, long?> onAction)
{
    public const string ActionOpenTicket = "open";
    public const string ActionOpenZendesk = "zendesk";
    public const string ActionSignIn = "signin";
    public const string ActionNone = "none";
    public const string ActionUpdate = "update";
    public const string ActionTake = "take";

    private static readonly Lazy<Uri> Logo = new(CreateLogo);
    private readonly PopupManager _popups = new(onAction);

    /// <summary>While paused, changes are still recorded but nothing is shown.</summary>
    public bool Paused { get; set; }

    /// <summary>The settings that decide theme, sound, display time, logo and grouping.</summary>
    public Settings Settings { get; set; } = new();

    public void Show(NotificationContent content)
    {
        if (!Paused)
            Show(content, Settings);
    }

    /// <summary>Shown even when paused: the app cannot work until the user signs in.</summary>
    public void ShowSignInRequired() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Sign-in required", "Your Zendesk session has expired", "Click to sign in again.")
    {
        Action = ActionSignIn
    }, Settings);

    /// <summary>Feedback on something the user did (e.g. "Take it"). Shown even when paused.</summary>
    public void ShowInfo(string title, string body, long? ticketId = null) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", title, body, ticketId), Settings);

    /// <summary>A new version has been downloaded; clicking restarts into it.</summary>
    public void ShowUpdateReady(string version) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Update ready", $"Zendesk Watcher {version} is ready", "Click to restart and update now, or it installs on the next start.")
    {
        Action = ActionUpdate
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
                Priority: "urgent", Context: "Support Tier 1") { CanTake = true });

    private static IEnumerable<NotificationContent> Samples(NotificationKind kind) => kind == NotificationKind.NewTicket
        ? [new NotificationContent(kind, "New ticket", "#123456 Customer cannot publish content after upgrade",
              "Requester: Alex Example · Created just now", Priority: "high", Context: "Support Tier 2") { Action = ActionNone, CanTake = true }]
        : [new NotificationContent(kind, "Ticket updated", "#123457 Search results missing after deploy",
              "Jane Doe replied: Thanks, the reindex fixed it on our side, but one site still shows old results…\nStatus pending → open (Jane Doe)",
              Priority: "normal") { Action = ActionNone }];

    private void Show(NotificationContent content, Settings settings)
    {
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
        var style = content.Kind == NotificationKind.NewTicket ? settings.NewTicketStyle : settings.MyTicketStyle;
        var action = content.ResolvedAction;

        var heading = content.Context is null ? content.Heading : $"{content.Heading} · {content.Context}";
        var builder = new ToastContentBuilder()
            .AddArgument("action", action)
            .AddText(heading)
            .AddText(content.Title);

        if (!string.IsNullOrWhiteSpace(content.Body))
            builder.AddText(content.Body);
        builder.AddAttributionText(content.PriorityLabel is { } priority ? $"Zendesk Watcher · {priority}" : "Zendesk Watcher");

        if (content.TicketId is { } id)
            builder.AddArgument("ticketId", id);

        if (settings.ShowAppLogo)
            builder.AddAppLogoOverride(Logo.Value, ToastGenericAppLogoCrop.Circle);

        if (settings.GroupNotificationsByType)
        {
            var (headerId, headerTitle) = content.Kind == NotificationKind.NewTicket ? ("new-tickets", "New tickets") : ("my-tickets", "My tickets");
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
            .SetContent(action == ActionSignIn ? "Sign in" : action == ActionUpdate ? "Restart now" : content.TicketId is null ? "Open Zendesk" : "Open ticket")
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
