namespace ZendeskWatcher.Tests;

/// <summary>"Signed in for 5 h 12 min" on the Account page.</summary>
public class SignInTimeTests
{
    [Theory]
    [InlineData(0, "Signed in just now.")]
    [InlineData(12, "Signed in for 12 min.")]
    [InlineData(312, "Signed in for 5 h 12 min.")]
    [InlineData(60 * 25, null)] // over a day: says little, not shown
    [InlineData(-5, null)]      // the clock went back
    public void Says_how_long_ago(int minutes, string? expected) =>
        Assert.Equal(expected, SignInTime.Text(TimeSpan.FromMinutes(minutes)));
}
