using System.Text;
using System.Text.RegularExpressions;

namespace ZendeskWatcher.Tests;

public class TicketShareTests
{
    private static readonly NotificationContent Update = new(NotificationKind.MyTicket, "Ticket updated", "#123456 Login <broken> & slow",
        "Jane Customer replied: Still broken\nStatus Pending (P) → Open (Jane)", 123456, "high", "You're CC") { IsTicketNotification = true };

    [Fact]
    public void Html_has_a_clickable_title_and_the_details()
    {
        var (html, _) = TicketShare.Format(Update, "https://acme.zendesk.com/agent/tickets/123456");

        Assert.Equal(
            "<b><a href=\"https://acme.zendesk.com/agent/tickets/123456\">#123456 Login &lt;broken&gt; &amp; slow</a></b> · High (P2) · You&#39;re CC" +
            "<br>Jane Customer replied: Still broken<br>Status Pending (P) → Open (Jane)", html);
    }

    [Fact]
    public void Text_has_the_link_on_its_own_line()
    {
        var (_, text) = TicketShare.Format(Update, "https://acme.zendesk.com/agent/tickets/123456");

        Assert.Equal(
            "#123456 Login <broken> & slow · High (P2) · You're CC\r\nhttps://acme.zendesk.com/agent/tickets/123456\r\n" +
            "Jane Customer replied: Still broken\r\nStatus Pending (P) → Open (Jane)", text);
    }

    [Fact]
    public void Several_tickets_have_no_link()
    {
        var summary = new NotificationContent(NotificationKind.NewTicket, "New tickets", "6 new unassigned tickets", "#1 A\n#2 B", Context: "Tier 2");

        var (html, text) = TicketShare.Format(summary, null);

        Assert.DoesNotContain("<a ", html);
        Assert.Equal("6 new unassigned tickets · Tier 2\r\n#1 A\r\n#2 B", text);
    }

    [Fact]
    public void Clipboard_header_offsets_point_at_the_fragment()
    {
        var fragment = "<b>Ö → ✓</b>"; // non-ASCII: the offsets are in UTF-8 bytes
        var cf = TicketShare.ClipboardHtml(fragment);
        var bytes = Encoding.UTF8.GetBytes(cf);
        int Offset(string name) => int.Parse(Regex.Match(cf, name + @":(\d+)").Groups[1].Value);

        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment")));
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, Offset("StartHTML"), 6));
        Assert.Equal(bytes.Length, Offset("EndHTML"));
    }
}
