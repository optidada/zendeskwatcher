using System.Text.Json.Nodes;

namespace ZendeskWatcher.Tests;

/// <summary>Sound and display per kind of notification, including settings saved by older versions.</summary>
public class NotificationStyleTests
{
    [Fact]
    public void Cc_tickets_keep_a_customized_my_tickets_choice_from_older_settings()
    {
        // Settings saved before CC tickets had their own style: no CcTicketStyle in the file.
        var old = new Settings { MyTicketStyle = new NotificationStyle { Sound = ToastSound.ChipBlip, Display = ToastDisplay.Long } };
        var json = JsonNode.Parse(old.ToJson())!.AsObject();
        var ccKey = json.Select(p => p.Key).Single(k => k.Contains("CcTicketStyle", StringComparison.OrdinalIgnoreCase));
        json.Remove(ccKey);

        var loaded = Settings.FromJson(json.ToJsonString());

        Assert.Equal(ToastSound.ChipBlip, loaded.CcTicketStyle.Sound);
        Assert.Equal(ToastDisplay.Long, loaded.CcTicketStyle.Display);
    }

    [Fact]
    public void Cc_tickets_once_chosen_are_independent()
    {
        var settings = new Settings { CcTicketStyle = new NotificationStyle { Sound = ToastSound.Sms, Display = ToastDisplay.Standard } };
        var loaded = Settings.FromJson(settings.ToJson());

        Assert.Equal(ToastSound.Sms, loaded.CcTicketStyle.Sound);
        Assert.Equal(ToastSound.Mail, loaded.MyTicketStyle.Sound);
    }

    [Fact]
    public void Defaults_match_the_behavior_before_they_were_configurable()
    {
        var settings = new Settings();

        Assert.Equal((ToastSound.Mail, ToastDisplay.Standard), (settings.CcTicketStyle.Sound, settings.CcTicketStyle.Display));
        Assert.Equal((ToastSound.Silent, ToastDisplay.Medium), (settings.SummaryStyle.Sound, settings.SummaryStyle.Display));
        Assert.Equal((ToastSound.Default, ToastDisplay.Long), (settings.AppMessageStyle.Sound, settings.AppMessageStyle.Display));
    }

    [Theory]
    [InlineData(NotificationKind.NewTicket, ToastSound.Default)]
    [InlineData(NotificationKind.MyTicket, ToastSound.Mail)]
    [InlineData(NotificationKind.CcTicket, ToastSound.Sms)]
    [InlineData(NotificationKind.HelpColleague, ToastSound.Reminder)]
    public void Each_kind_uses_its_own_style(NotificationKind kind, ToastSound expected)
    {
        var settings = new Settings { CcTicketStyle = new NotificationStyle { Sound = ToastSound.Sms } };
        Assert.Equal(expected, settings.StyleFor(kind).Sound);
    }
}
