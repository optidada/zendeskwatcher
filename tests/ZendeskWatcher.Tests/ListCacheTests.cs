using ZendeskWatcher.SettingsPages;
using Member = ZendeskWatcher.SettingsPages.HelpColleaguesCard.Member;

namespace ZendeskWatcher.Tests;

/// <summary>The lists Settings loads (groups, members, clients) survive a restart for a day, for the same account.</summary>
public class ListCacheTests
{
    [Fact]
    public void Saved_lists_come_back_after_a_restart()
    {
        var at = DateTime.UtcNow;
        ListCache.Save("test-members", "acme", at, new Dictionary<long, List<Member>> { [1] = [new(9, "Alex Example", "Europe/Stockholm")] });
        ListCache.Save("test-clients", "acme", at, new Dictionary<long, List<ClientsView.Client>> { [1] = [new(21, "Contoso", 4)] });

        var members = ListCache.Load<Dictionary<long, List<Member>>>("test-members", "acme");
        var clients = ListCache.Load<Dictionary<long, List<ClientsView.Client>>>("test-clients", "acme");

        Assert.Equal(new Member(9, "Alex Example", "Europe/Stockholm"), members!.Value.Value[1].Single());
        Assert.Equal(new ClientsView.Client(21, "Contoso", 4), clients!.Value.Value[1].Single());
    }

    [Fact]
    public void Another_account_an_old_list_or_reload_fetches_again()
    {
        ListCache.Save("test-old", "acme", DateTime.UtcNow - GroupsPage.CacheLifetime - TimeSpan.FromMinutes(1), new List<string> { "x" });
        ListCache.Save("test-fresh", "acme", DateTime.UtcNow, new List<string> { "x" });

        Assert.Null(ListCache.Load<List<string>>("test-old", "acme"));
        Assert.Null(ListCache.Load<List<string>>("test-fresh", "other"));
        Assert.NotNull(ListCache.Load<List<string>>("test-fresh", "acme"));
        ListCache.Clear("test-fresh");
        Assert.Null(ListCache.Load<List<string>>("test-fresh", "acme"));
    }
}
