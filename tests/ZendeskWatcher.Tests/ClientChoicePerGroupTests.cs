using ZendeskWatcher.Tracking;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>Client choices apply in the group they were made in; older saved ones (from before) in every group.</summary>
public class ClientChoicePerGroupTests : IDisposable
{
    private const long Acme = 70, Tier1 = 1, Tier2 = 2;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ZendeskWatcher.PerGroupTests", Guid.NewGuid().ToString("N"));

    public ClientChoicePerGroupTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ClientMode ModeIn(Settings settings, long group) =>
        TicketRules.ClientMode(new Ticket { OrganizationId = Acme, GroupId = group }, settings, Now);

    [Fact]
    public void Muting_in_one_group_leaves_the_other_groups_alone()
    {
        var settings = new Settings();
        settings.SetClientChoice(new ClientChoice { Id = Acme, Name = "Acme", GroupId = Tier1, IsMuted = true });
        Assert.Equal(ClientMode.Muted, ModeIn(settings, Tier1));
        Assert.Equal(ClientMode.Normal, ModeIn(settings, Tier2));

        settings.SetClientChoice(new ClientChoice { Id = Acme, Name = "Acme", GroupId = Tier2, Mode = ClientMode.Important });
        Assert.Equal(ClientMode.Muted, ModeIn(settings, Tier1));
        Assert.Equal(ClientMode.Important, ModeIn(settings, Tier2));

        settings.SetClientChoice(new ClientChoice { Id = Acme, Name = "Acme", GroupId = Tier1 }); // back to normal there
        Assert.Equal(ClientMode.Normal, ModeIn(settings, Tier1));
        Assert.Single(settings.Clients); // nothing stored for a Normal choice
    }

    [Fact]
    public void An_older_choice_still_applies_everywhere_until_changed_in_a_group()
    {
        // As saved before choices were per group: no PerGroup.
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{ "Subdomain": "acme", "Clients": [ { "Id": 70, "Name": "Acme", "GroupId": 1, "IsMuted": true } ] }""");
        var settings = Settings.Load(Path.Combine(_dir, "settings.json"));
        Assert.Equal(ClientMode.Muted, ModeIn(settings, Tier1));
        Assert.Equal(ClientMode.Muted, ModeIn(settings, Tier2));

        // Unmuted in Tier 2 only: kept as a Normal choice there, so the old one doesn't apply there any more.
        settings.SetClientChoice(new ClientChoice { Id = Acme, Name = "Acme", GroupId = Tier2 });
        Assert.Equal(ClientMode.Muted, ModeIn(settings, Tier1));
        Assert.Equal(ClientMode.Normal, ModeIn(settings, Tier2));

        settings.Save(Path.Combine(_dir, "settings.json"));
        var again = Settings.Load(Path.Combine(_dir, "settings.json"));
        Assert.Equal(ClientMode.Muted, ModeIn(again, Tier1));
        Assert.Equal(ClientMode.Normal, ModeIn(again, Tier2));
    }

    [Fact]
    public void Insights_still_suggest_muting_a_client_muted_only_in_another_group()
    {
        var settings = new Settings { WatchedGroups = [new WatchedGroup { Id = Tier1, Name = "Tier 1" }, new WatchedGroup { Id = Tier2, Name = "Tier 2" }] };
        settings.SetClientChoice(new ClientChoice { Id = Acme, Name = "Acme", GroupId = Tier2, IsMuted = true });
        (long, string, int, int)[] noisy = [(Acme, "Acme", 40, 0)];
        Assert.Single(StatsInsights.Clients(noisy, settings)); // the 40 came from Tier 1, where it isn't muted

        settings.SetClientChoice(new ClientChoice { Id = Acme, Name = "Acme", GroupId = Tier1, Mode = ClientMode.Important });
        Assert.Empty(StatsInsights.Clients(noisy, settings)); // important somewhere: a choice made on purpose
    }

    [Fact]
    public void The_confirmation_says_which_group()
    {
        var choice = new ClientChoice { Id = Acme, Name = "Acme", GroupId = Tier1, GroupName = "Tier 1", IsMuted = true };
        var (title, body) = TicketRules.ClientChoiceConfirmation(choice, Now);
        Assert.Equal("Muted Acme in Tier 1", title);
        Assert.Contains("other groups aren't affected", body);
    }
}
