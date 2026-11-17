using ZendeskWatcher.UI;

namespace ZendeskWatcher.Tests;

public class PriorityLevelsTests
{
    [Fact]
    public void Four_levels_most_urgent_first() =>
        Assert.Equal(["P1", "P2", "P3", "P4"], PriorityLevels.All.Select(l => l.Tag));

    [Fact]
    public void Says_client_and_never_sev() =>
        Assert.All(PriorityLevels.All, l =>
        {
            Assert.DoesNotContain("customer", l.Definition, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Sev", l.Definition);
        });
}
