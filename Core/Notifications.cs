using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

/// <summary>Which kind of ticket news a notification is; decides its sound and display settings.</summary>

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

/// <summary>Where the ticket checks send notifications: the app's notifier, or a recorder in tests.</summary>
public interface INotificationSink
{
    void Show(NotificationContent content);
    void ShowMissingGroups(IReadOnlyCollection<string> names);
}

/// <summary>What a click on a notification does (also the arguments of Windows notifications, so keep the values).</summary>
public static class NotificationActions
{
    public const string OpenTicket = "open";
    public const string OpenZendesk = "zendesk";
    public const string SignIn = "signin";
    public const string None = "none";
    public const string Info = "info"; // information only: the button says OK, a click just dismisses it
    public const string Update = "update";
    public const string Take = "take";
    public const string CatchUp = "catchup";
    public const string Settings = "settings";
    public const string Copy = "copy";
    public const string MuteClient = "muteclient";
}
