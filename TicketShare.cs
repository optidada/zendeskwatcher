using System.Net;
using System.Text;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

/// <summary>
/// "Copy" on a ticket notification: the ticket as a clickable title plus its details, for pasting into Teams (HTML),
/// and as plain text with the link for anywhere else.
/// </summary>
public static class TicketShare
{
    /// <summary>The clipboard contents for a notification. <paramref name="ticketUrl"/> is null when it isn't about one ticket.</summary>
    public static (string Html, string Text) Format(NotificationContent content, string? ticketUrl)
    {
        var metaParts = new[] { content.PriorityLabel, content.Context }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        var meta = string.Join(" · ", metaParts);
        var lines = content.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var html = new StringBuilder();
        var title = WebUtility.HtmlEncode(content.Title);
        html.Append(ticketUrl is null ? $"<b>{title}</b>" : $"<b><a href=\"{WebUtility.HtmlEncode(ticketUrl)}\">{title}</a></b>");
        if (meta.Length > 0)
            html.Append(" · ").Append(string.Join(" · ", metaParts.Select(WebUtility.HtmlEncode)));
        foreach (var line in lines)
            html.Append("<br>").Append(WebUtility.HtmlEncode(line));

        var text = new StringBuilder(content.Title);
        if (meta.Length > 0)
            text.Append($" ({meta})");
        if (ticketUrl is not null)
            text.Append("\r\n").Append(ticketUrl);
        foreach (var line in lines)
            text.Append("\r\n").Append(line);

        return (html.ToString(), text.ToString());
    }

    /// <summary>Puts the notification on the clipboard, as HTML (Teams, Outlook) and plain text. Runs on the UI thread.</summary>
    public static void Copy(NotificationContent content, string? ticketUrl)
    {
        var (html, text) = Format(content, ticketUrl);
        var data = new DataObject();
        data.SetData(DataFormats.Html, ClipboardHtml(html));
        data.SetData(DataFormats.UnicodeText, text);
        Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
    }

    /// <summary>
    /// The Windows "HTML Format" clipboard text: a header with byte offsets (UTF-8) of the document and of the fragment
    /// that gets pasted.
    /// </summary>
    public static string ClipboardHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string start = "<html><body><!--StartFragment-->";
        const string end = "<!--EndFragment--></body></html>";

        var headerLength = string.Format(header, 0, 0, 0, 0).Length; // all ASCII
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(start);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(end);
        return string.Format(header, startHtml, endHtml, startFragment, endFragment) + start + fragment + end;
    }
}
