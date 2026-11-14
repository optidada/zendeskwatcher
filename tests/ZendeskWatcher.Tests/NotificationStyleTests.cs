using System.Text.Json.Nodes;

namespace ZendeskWatcher.Tests;

/// <summary>Sound and display per kind of notification, including settings saved by older versions.</summary>
public class NotificationStyleTests
{
    [Fact]
    public void Cc_tickets_keep_a_customized_my_tickets_choice_from_older_settings()
    {
        // Settings saved before CC tickets had their own style: no CcTicketStyle in the file.
        var old = new Settings { MyTicketStyle = new NotificationStyle { Sound = ToastSound.ChipBlip } };
        var json = JsonNode.Parse(old.ToJson())!.AsObject();
        var ccKey = json.Select(p => p.Key).Single(k => k.Contains("CcTicketStyle", StringComparison.OrdinalIgnoreCase));
        json.Remove(ccKey);

        var loaded = Settings.FromJson(json.ToJsonString());

        Assert.Equal(ToastSound.ChipBlip, loaded.CcTicketStyle.Sound);
    }

    [Fact]
    public void Cc_tickets_once_chosen_are_independent()
    {
        var settings = new Settings { CcTicketStyle = new NotificationStyle { Sound = ToastSound.Sms, Display = ToastDisplay.Standard } };
        var loaded = Settings.FromJson(settings.ToJson());

        Assert.Equal(ToastSound.Sms, loaded.CcTicketStyle.Sound);
        Assert.Equal(ToastSound.Silent, loaded.MyTicketStyle.Sound);
    }

    [Fact]
    public void Only_a_soft_chime_for_new_tickets_by_default()
    {
        var settings = new Settings();

        Assert.Equal(ToastSound.SoftChime, settings.NewTicketStyle.Sound);
        Assert.All(new[] { settings.MyTicketStyle, settings.CcTicketStyle, settings.HelpStyle, settings.SummaryStyle, settings.AppMessageStyle },
            style => Assert.Equal(ToastSound.Silent, style.Sound));
        Assert.Equal(ToastDisplay.Medium, settings.SummaryStyle.Display);
        Assert.Equal(ToastDisplay.Long, settings.AppMessageStyle.Display);
    }

    [Fact]
    public void Sounds_chosen_before_are_kept()
    {
        // Saved by an older version, when sounds were on by default.
        var json = new Settings { NewTicketStyle = new NotificationStyle { Sound = ToastSound.Default }, MyTicketStyle = new NotificationStyle { Sound = ToastSound.Mail } }.ToJson();
        var loaded = Settings.FromJson(json);

        Assert.Equal(ToastSound.Default, loaded.NewTicketStyle.Sound);
        Assert.Equal(ToastSound.Mail, loaded.MyTicketStyle.Sound);
    }

    [Theory]
    [InlineData("urgent", 0)]
    [InlineData("high", 60)]
    [InlineData("normal", 30)]
    [InlineData(null, 30)]
    [InlineData("low", 15)]
    public void Ticket_notifications_stay_by_priority(string? priority, int seconds)
    {
        var content = new UI.Popups.NotificationContent(NotificationKind.MyTicket, "Ticket updated", "#1 Title", Priority: priority);
        Assert.Equal(seconds, new Settings().DisplaySecondsFor(content));
    }

    [Fact]
    public void App_messages_keep_their_own_display_time()
    {
        var settings = new Settings();
        var content = new UI.Popups.NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Update ready", Priority: "urgent")
            { Style = settings.AppMessageStyle };
        Assert.Equal(25, settings.DisplaySecondsFor(content));
    }

    [Theory]
    [InlineData(DisplayLength.Short, "urgent", 0)] // urgent: always until dismissed
    [InlineData(DisplayLength.Short, "high", 30)]
    [InlineData(DisplayLength.Short, "low", 10)]
    [InlineData(DisplayLength.Long, "high", 0)]
    [InlineData(DisplayLength.Long, null, 60)]
    public void Short_and_long_shift_every_priority(DisplayLength length, string? priority, int seconds) =>
        Assert.Equal(seconds, TicketDisplayTimes.For(length, priority));

    [Theory]
    [InlineData(NotificationKind.NewTicket, ToastSound.Default)]
    [InlineData(NotificationKind.MyTicket, ToastSound.Mail)]
    [InlineData(NotificationKind.CcTicket, ToastSound.Sms)]
    [InlineData(NotificationKind.HelpColleague, ToastSound.Reminder)]
    public void Each_kind_uses_its_own_style(NotificationKind kind, ToastSound expected)
    {
        var settings = new Settings
        {
            NewTicketStyle = new NotificationStyle { Sound = ToastSound.Default },
            MyTicketStyle = new NotificationStyle { Sound = ToastSound.Mail },
            CcTicketStyle = new NotificationStyle { Sound = ToastSound.Sms },
            HelpStyle = new NotificationStyle { Sound = ToastSound.Reminder }
        };
        Assert.Equal(expected, settings.StyleFor(kind).Sound);
    }

    [Fact]
    public void Older_settings_keep_their_style_for_app_and_notifications()
    {
        // Saved before the app theme existed: only Theme, which styled both.
        var json = JsonNode.Parse(new Settings { Theme = NotificationTheme.Terminal }.ToJson())!.AsObject();
        foreach (var key in json.Select(p => p.Key).Where(k => k.Contains("AppTheme") || k.Contains("NotificationsLikeApp")).ToList())
            json.Remove(key);

        var loaded = Settings.FromJson(json.ToJsonString());

        Assert.Equal(NotificationTheme.Terminal, loaded.AppTheme);
        Assert.True(loaded.NotificationsLikeApp);
        Assert.Equal(NotificationTheme.Terminal, loaded.ActiveNotificationTheme);
    }

    [Fact]
    public void Notifications_can_have_their_own_style()
    {
        var settings = new Settings { AppTheme = NotificationTheme.CleanLight, Theme = NotificationTheme.NeonDark };
        Assert.Equal(NotificationTheme.CleanLight, settings.ActiveNotificationTheme); // like the app by default

        settings.NotificationsLikeApp = false;
        Assert.Equal(NotificationTheme.NeonDark, settings.ActiveNotificationTheme);
    }

    [Theory]
    [InlineData(DisplayLength.Short, ToastDisplay.Standard, ToastDisplay.Medium)]
    [InlineData(DisplayLength.Normal, ToastDisplay.Medium, ToastDisplay.Long)]
    [InlineData(DisplayLength.Long, ToastDisplay.Long, ToastDisplay.UntilDismissed)]
    public void The_summary_and_app_messages_follow_short_normal_long(DisplayLength length, ToastDisplay summary, ToastDisplay app)
    {
        var settings = new Settings { TicketDisplayLength = length };
        settings.ApplyDisplayLength();
        Assert.Equal((summary, app), (settings.SummaryStyle.Display, settings.AppMessageStyle.Display));
    }
}
