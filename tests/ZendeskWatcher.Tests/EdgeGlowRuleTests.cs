using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

public class EdgeGlowRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static NotificationContent Ticket(string? priority = "normal") =>
        new(NotificationKind.NewTicket, "New ticket", "#1 Site down", Priority: priority, TicketId: 1) { IsTicketNotification = true };

    [Theory]
    [InlineData("urgent", GlowReason.Urgent)]
    [InlineData("Urgent", GlowReason.Urgent)]
    [InlineData("high", GlowReason.None)]
    [InlineData("normal", GlowReason.None)]
    [InlineData(null, GlowReason.None)]
    public void Only_urgent_glows_by_priority(string? priority, GlowReason expected) =>
        Assert.Equal(expected, EdgeGlowRule.For(Ticket(priority), Now));

    [Theory]
    [InlineData(-30, GlowReason.Sla)] // already breached
    [InlineData(0, GlowReason.Sla)]
    [InlineData(15, GlowReason.Sla)]  // breaches within 15 minutes
    [InlineData(16, GlowReason.None)]
    [InlineData(240, GlowReason.None)]
    public void Sla_glows_when_breached_or_due_soon(int minutes, GlowReason expected) =>
        Assert.Equal(expected, EdgeGlowRule.For(Ticket() with { SlaDue = Now.AddMinutes(minutes) }, Now));

    [Fact]
    public void Important_client_glows_but_urgent_and_sla_win()
    {
        Assert.Equal(GlowReason.ImportantClient, EdgeGlowRule.For(Ticket() with { Important = true }, Now));
        Assert.Equal(GlowReason.Urgent, EdgeGlowRule.For(Ticket("urgent") with { Important = true }, Now));
        Assert.Equal(GlowReason.Sla, EdgeGlowRule.For(Ticket() with { Important = true, SlaDue = Now }, Now));
    }

    [Fact]
    public void App_messages_and_quiet_thank_yous_never_glow_samples_only_when_testing()
    {
        var appMessage = Ticket("urgent") with { IsTicketNotification = false };
        Assert.Equal(GlowReason.None, EdgeGlowRule.For(appMessage, Now));
        Assert.Equal(GlowReason.Urgent, EdgeGlowRule.For(appMessage, Now, isSample: true));
        Assert.Equal(GlowReason.None, EdgeGlowRule.For(Ticket("urgent") with { Quiet = true }, Now));
    }
}
