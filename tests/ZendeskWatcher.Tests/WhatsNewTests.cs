namespace ZendeskWatcher.Tests;

/// <summary>"What's new" after an update: the highlights of every version since the last one that ran.</summary>
public class WhatsNewTests
{
    private const string Changelog = """
        # Changelog

        ## 1.7.1

        - **Clickable tickets** in lists: long explanation.
        - **See all** opens the whole list.

        ## 1.7.0

        - **New Native theme**: follows Windows.
        - **Faster Settings**: much.
        - **Colleagues by region**: tick a region.

        ## 1.6.0

        - **Experimental page**: extras.

        ## 1.5.0

        - **SLA deadlines**: on notifications.
        """;

    [Fact]
    public void One_version_shows_its_highlights()
    {
        var (title, body) = WhatsNew.Summary(Changelog, "1.7.0", "1.7.1")!.Value;
        Assert.Equal("What's new in 1.7.1", title);
        Assert.Equal("• Clickable tickets\n• See all", body);
    }

    [Fact]
    public void Several_versions_show_the_top_of_each_newest_first()
    {
        var (title, body) = WhatsNew.Summary(Changelog, "1.5.0", "1.7.1")!.Value;
        Assert.Equal("What's new since 1.5.0", title);
        Assert.Equal("• 1.7.1: Clickable tickets\n• 1.7.1: See all\n• 1.7.0: New Native theme\n• 1.7.0: Faster Settings\n" +
                     "• 1.6.0: Experimental page\n… and more: click for all release notes", body);
    }

    [Fact]
    public void Nothing_new_says_nothing() => Assert.Null(WhatsNew.Summary(Changelog, "1.7.1", "1.7.1"));

    [Fact]
    public void The_app_carries_its_release_notes() =>
        Assert.NotNull(WhatsNew.Summary("1.6.0", "1.7.0")); // CHANGELOG.md is built into the app
}
