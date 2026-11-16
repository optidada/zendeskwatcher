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

    /// <summary>A new ticket someone has taken meanwhile: its notification closes quietly (on screen or waiting).</summary>
    void CloseTaken(long ticketId) { }
}

/// <summary>What a click on a notification does.</summary>
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

    /// <summary>Opens a ticket search in Zendesk: "search:" and the query (e.g. a group's unassigned tickets).</summary>
    public const string SearchPrefix = "search:";

    public static string Search(string query) => SearchPrefix + query;

    /// <summary>Assigns the ticket to a colleague: "assign:", their user id, ":" and their name.</summary>
    public const string AssignPrefix = "assign:";

    public static string Assign(long userId, string name) => $"{AssignPrefix}{userId}:{name}";

    /// <summary>Opens a web page: "url:" and the address (e.g. the release notes).</summary>
    public const string UrlPrefix = "url:";

    public static string Url(string address) => UrlPrefix + address;

    /// <summary>Opens Settings at a page (and card): "settings:" and "Groups" or "Behavior/Snooze".</summary>
    public const string SettingsPrefix = "settings:";

    public static string SettingsPage(string page, string? section = null) => SettingsPrefix + page + (section is null ? "" : "/" + section);
}
