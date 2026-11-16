namespace ZendeskWatcher.Tests;

/// <summary>"Update ready" lists what every version since the installed one brings, not only the newest.</summary>
public class UpdateHighlightsTests
{
    private const string V108 = "- **Sounds are quieter**: a Volume on Look & sound.\n- **Filter Groups and Clients**: type to filter.";
    private const string V109 = "- **Volume stands on its own**: buttons at the top.";
    private const string V110 = "- **Catch up is easier to read**: an empty line.\n- The ▶ says Stop while it plays.";

    [Fact]
    public void One_version_shows_its_first_points()
    {
        var text = AppUpdater.Highlights([("1.8.10", V110)], "1.8.9");

        Assert.Equal("• Catch up is easier to read\n• The ▶ says Stop while it plays.", text);
    }

    [Fact]
    public void Several_versions_show_the_first_point_of_each_newest_first()
    {
        var text = AppUpdater.Highlights([("1.8.8", V108), ("1.8.9", V109), ("1.8.10", V110)], "1.8.7");

        Assert.Equal("• 1.8.10: Catch up is easier to read\n• 1.8.9: Volume stands on its own\n• 1.8.8: Sounds are quieter", text);
    }

    [Fact]
    public void Versions_already_installed_are_left_out()
    {
        var text = AppUpdater.Highlights([("1.8.8", V108), ("1.8.9", V109), ("1.8.10", V110)], "1.8.9");

        Assert.Equal("• Catch up is easier to read\n• The ▶ says Stop while it plays.", text);
    }

    [Fact]
    public void A_long_jump_ends_with_how_many_more()
    {
        var releases = Enumerable.Range(1, 7).Select(i => ($"1.9.{i}", (string?)$"- **Change {i}**: something."));

        var lines = AppUpdater.Highlights(releases, "1.9.0")!.Split('\n');

        Assert.Equal(4, lines.Length);
        Assert.Equal("• 1.9.7: Change 7", lines[0]);
        Assert.Equal("… and 4 more versions", lines[3]);
    }

    [Fact]
    public void Without_notes_there_is_nothing_to_say()
    {
        Assert.Null(AppUpdater.Highlights([("1.8.10", null)], "1.8.9"));
    }
}
