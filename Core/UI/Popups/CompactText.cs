namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// Short one-line texts for the compact ways notifications can appear (Look &amp; sound → How they appear): the pill's
/// summary at the top of the screen and a ticker item.
/// </summary>
public static class CompactText
{
    private const string Dot = " · ";

    /// <summary>
    /// The pill: one notification as its heading and ticket number ("New ticket · #123456"); several counted
    /// ("2 new · 1 update · 1 P1").
    /// </summary>
    public static string PillSummary(IReadOnlyList<NotificationContent> items)
    {
        if (items.Count == 0)
            return "";
        if (items.Count == 1)
        {
            var one = items[0];
            var parts = new List<string> { OneLine(one.Heading) };
            if (one.PriorityShort is { } p && one.TicketCount <= 1)
                parts.Add(p);
            if (one.TicketId is { } id)
                parts.Add($"#{id}");
            else if (parts[0].Length == 0)
                parts[0] = Shorten(OneLine(one.Title), 40);
            return string.Join(Dot, parts.Where(s => s.Length > 0));
        }

        bool IsMessage(NotificationContent c) => c.TicketId is null && c.TicketCount <= 1;
        var news = items.Count(c => !IsMessage(c) && c.Kind == NotificationKind.NewTicket);
        var waiting = items.Count(c => !IsMessage(c) && c.Kind == NotificationKind.HelpColleague);
        var updates = items.Count(c => !IsMessage(c) && c.Kind is NotificationKind.MyTicket or NotificationKind.CcTicket);
        var messages = items.Count(IsMessage);
        var urgent = items.Count(c => string.Equals(c.Priority, "urgent", StringComparison.OrdinalIgnoreCase));
        var summary = new List<string>();
        if (news > 0) summary.Add($"{news} new");
        if (updates > 0) summary.Add(updates == 1 ? "1 update" : $"{updates} updates");
        if (waiting > 0) summary.Add($"{waiting} waiting");
        if (messages > 0) summary.Add(messages == 1 ? "1 message" : $"{messages} messages");
        if (urgent > 0) summary.Add($"{urgent} P1");
        return string.Join(Dot, summary);
    }

    /// <summary>A ticker item: "New ticket · #123456 Customer cannot log in · Contoso" (the priority shows as a tag beside it).</summary>
    public static string TickerItem(NotificationContent content)
    {
        var client = OneLine(content.ClientName ?? content.Client ?? "");
        var parts = new[] { OneLine(content.Heading), Shorten(OneLine(content.Title), 80), client };
        return string.Join(Dot, parts.Where(s => s.Length > 0));
    }

    /// <summary>Line breaks and runs of spaces as one space.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
