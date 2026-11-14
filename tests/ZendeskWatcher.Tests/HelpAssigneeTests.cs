using ZendeskWatcher.SettingsPages;
using Member = ZendeskWatcher.SettingsPages.HelpColleaguesCard.Member;

namespace ZendeskWatcher.Tests;

/// <summary>How "Help colleagues" lists the assignees, and who's covered the first time.</summary>
public class HelpAssigneeTests
{
    private static readonly Member Alex = new(1, "Alex", "Europe/Stockholm");
    private static readonly Member Maria = new(2, "Maria", "Europe/Berlin");
    private static readonly Member Jordan = new(3, "Jordan", "America/New_York");
    private static readonly Member Chris = new(4, "Chris", "America/Los_Angeles");
    private static readonly Member Priya = new(5, "Priya", "Asia/Kolkata");
    private static readonly Member Robin = new(6, "Robin", null);
    private static readonly Member[] Everyone = [Alex, Maria, Jordan, Chris, Priya, Robin];

    [Fact]
    public void My_region_comes_last_and_no_time_zone_at_the_very_end()
    {
        var regions = HelpColleaguesCard.Arrange(Everyone, _ => true, TimeRegion.EuropeAfrica).Select(s => s.Region);

        Assert.Equal([TimeRegion.Americas, TimeRegion.AsiaPacific, TimeRegion.EuropeAfrica, TimeRegion.NoTimeZone], regions);
    }

    [Fact]
    public void Covered_colleagues_first_then_by_name()
    {
        var excluded = new HashSet<long> { Chris.Id };

        var americas = HelpColleaguesCard.Arrange(Everyone, id => !excluded.Contains(id), TimeRegion.EuropeAfrica).Single(s => s.Region == TimeRegion.Americas);

        Assert.Equal(["Jordan", "Chris"], americas.Members.Select(m => m.Name)); // Chris is unticked
    }

    [Fact]
    public void Alphabetical_within_the_ticked_ones()
    {
        var europe = HelpColleaguesCard.Arrange(Everyone, _ => true, TimeRegion.Americas).Single(s => s.Region == TimeRegion.EuropeAfrica);

        Assert.Equal(["Alex", "Maria"], europe.Members.Select(m => m.Name));
    }

    [Theory]
    [InlineData(TimeRegion.Americas, false, true)]      // ticked region: covered, newcomers too
    [InlineData(TimeRegion.Americas, true, false)]      // ... except the ones unticked one by one
    [InlineData(TimeRegion.EuropeAfrica, false, false)] // unticked region: not covered, newcomers neither
    [InlineData(TimeRegion.EuropeAfrica, true, true)]   // ... except the ones ticked one by one
    public void Region_tick_then_the_exceptions(TimeRegion region, bool exception, bool covered)
    {
        var settings = new Settings { HelpRegionDefaultDone = true, HelpOffRegions = [TimeRegion.EuropeAfrica] };
        if (exception)
            (region == TimeRegion.EuropeAfrica ? settings.HelpIncludedAssignees : settings.HelpExcludedAssignees).Add(new Colleague { Id = 7 });

        Assert.Equal(covered, settings.CoversColleague(7, region, TimeRegion.EuropeAfrica));
    }

    [Fact]
    public void Until_the_list_was_shown_my_region_counts_as_unticked()
    {
        var settings = new Settings();

        Assert.False(settings.CoversColleague(7, TimeRegion.EuropeAfrica, TimeRegion.EuropeAfrica));
        Assert.True(settings.CoversColleague(7, TimeRegion.Americas, TimeRegion.EuropeAfrica));
    }
}
