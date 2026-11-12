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
        var regions = HelpColleaguesCard.Arrange(Everyone, new HashSet<long>(), TimeRegion.EuropeAfrica).Select(s => s.Region);

        Assert.Equal([TimeRegion.Americas, TimeRegion.AsiaPacific, TimeRegion.EuropeAfrica, TimeRegion.NoTimeZone], regions);
    }

    [Fact]
    public void Covered_colleagues_first_then_by_name()
    {
        var excluded = new HashSet<long> { Chris.Id };

        var americas = HelpColleaguesCard.Arrange(Everyone, excluded, TimeRegion.EuropeAfrica).Single(s => s.Region == TimeRegion.Americas);

        Assert.Equal(["Jordan", "Chris"], americas.Members.Select(m => m.Name)); // Chris is unticked
    }

    [Fact]
    public void Alphabetical_within_the_ticked_ones()
    {
        var europe = HelpColleaguesCard.Arrange(Everyone, new HashSet<long>(), TimeRegion.Americas).Single(s => s.Region == TimeRegion.EuropeAfrica);

        Assert.Equal(["Alex", "Maria"], europe.Members.Select(m => m.Name));
    }

    [Fact]
    public void First_time_my_region_is_not_covered()
    {
        var unticked = HelpColleaguesCard.RegionDefaultExclusions(Everyone, TimeRegion.EuropeAfrica).Select(m => m.Name);

        Assert.Equal(["Alex", "Maria"], unticked);
    }
}
