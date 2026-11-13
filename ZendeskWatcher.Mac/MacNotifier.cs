using System.Diagnostics;
using AppKit;
using Foundation;
using UserNotifications;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Mac;

/// <summary>
/// macOS notifications (Notification Center). A click opens the ticket, or Zendesk. If macOS refuses notifications
/// for this unsigned app, they fall back to AppleScript's "display notification" (no click action, but visible).
/// </summary>
public sealed class MacNotifier : UNUserNotificationCenterDelegate, INotificationSink
{
    private readonly Func<Settings> _settings;
    private bool _allowed;

    public MacNotifier(Func<Settings> settings)
    {
        _settings = settings;
        var center = UNUserNotificationCenter.Current;
        center.Delegate = this;
        center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (granted, error) =>
        {
            _allowed = granted;
            Log.Info(granted ? "Notifications allowed" : $"Notifications not allowed ({error?.LocalizedDescription ?? "turned off"}); using AppleScript");
        });
    }

    public void Show(NotificationContent content) =>
        Post(content.FullHeading, content.Title, Markdown.Strip(content.DisplayBody(DateTimeOffset.Now)),
            content.TicketId is { } id ? _settings().TicketUrl(id) : _settings().BaseUrl);

    public void ShowMissingGroups(IReadOnlyCollection<string> names) =>
        Info("Groups not found", $"No longer in Zendesk (or no access): {string.Join(", ", names)}. Choose groups again in the menu.");

    public void Info(string title, string body) => Post("Zendesk Watcher", title, body, null);

    private void Post(string subtitle, string title, string body, string? url)
    {
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            if (!_allowed)
            {
                AppleScriptNotification(title, string.IsNullOrEmpty(body) ? subtitle : body, subtitle);
                return;
            }
            var content = new UNMutableNotificationContent { Title = title, Subtitle = subtitle, Body = body };
            if (url is not null)
                content.UserInfo = NSDictionary.FromObjectAndKey(new NSString(url), new NSString("url"));
            var request = UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString(), content, null);
            UNUserNotificationCenter.Current.AddNotificationRequest(request, error =>
            {
                if (error is not null)
                    Log.Error($"Notification failed: {error.LocalizedDescription}");
            });
        });
    }

    /// <summary>Show notifications while the app is "in front" too (a menu bar app often is).</summary>
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification,
        Action<UNNotificationPresentationOptions> completionHandler) =>
        completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List);

    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response,
        Action completionHandler)
    {
        if (response.Notification.Request.Content.UserInfo?["url"]?.ToString() is { Length: > 0 } url)
            Open(url);
        completionHandler();
    }

    public static void Open(string url) => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(url));

    private static void AppleScriptNotification(string title, string body, string subtitle)
    {
        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        try
        {
            Process.Start("/usr/bin/osascript", ["-e", $"display notification {Quote(body)} with title {Quote(title)} subtitle {Quote(subtitle)}"]);
        }
        catch (Exception ex)
        {
            Log.Error("AppleScript notification failed", ex);
        }
    }
}
