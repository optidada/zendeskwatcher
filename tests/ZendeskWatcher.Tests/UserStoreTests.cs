using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

public class UserStoreTests
{
    [Fact]
    public void Keeps_users_across_restarts_but_only_name_role_and_domain_of_clients()
    {
        var file = Path.Combine(Path.GetTempPath(), $"zw-users-{Guid.NewGuid():N}.json");
        try
        {
            var store = new UserStore(file, "yourcompany");
            store.Remember(new User { Id = 1, Name = "Anna Agent", Role = "agent" });
            store.Remember(new User { Id = 2, Name = "Carl Client", Email = "carl@example.com", Role = "end-user", TimeZone = "Berlin", Tags = ["vip"] });
            store.Save();

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("carl@", text);
            Assert.DoesNotContain("Berlin", text);
            Assert.DoesNotContain("vip", text);
            var restarted = new UserStore(file, "yourcompany");
            Assert.True(restarted.TryGet(1, out var anna));
            Assert.Equal("Anna Agent", anna.Name);
            Assert.True(restarted.TryGet(2, out var carl));
            Assert.Equal(("Carl Client", "end-user", "*@example.com"), (carl.Name, carl.Role, carl.Email));
            Assert.True(ZendeskWatcher.Tracking.TicketRules.IsCustomer(carl, new Ticket { RequesterId = 9 }, "me@optimizely.com")); // still a client
            Assert.False(new UserStore(file, "othercompany").TryGet(1, out _)); // another Zendesk: not used
        }
        finally
        {
            File.Delete(file);
        }
    }
}
