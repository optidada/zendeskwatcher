namespace ZendeskWatcher.Tests;

/// <summary>Priorities ticked in an older version (any combination) get the choice that covers their lowest one.</summary>
public class PriorityChoiceTests
{
    [Theory]
    [InlineData(new[] { "urgent", "high", "normal", "low" }, "All")]
    [InlineData(new[] { "urgent", "high", "normal" }, "P1–P3")]
    [InlineData(new[] { "urgent", "high" }, "P1, P2")]
    [InlineData(new[] { "urgent" }, "P1 only")]
    [InlineData(new[] { "urgent", "normal" }, "P1–P3")] // a gap: the lowest one decides
    [InlineData(new[] { "high", "low" }, "All")]
    [InlineData(new string[0], "P1 only")] // nothing ticked: the fewest there is
    public void Older_lists_get_the_nearest_choice(string[] priorities, string expected) =>
        Assert.Equal(expected, Settings.PriorityChoices[Settings.PriorityChoiceIndex(priorities)].Label);
}
