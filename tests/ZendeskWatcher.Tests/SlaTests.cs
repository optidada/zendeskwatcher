using ZendeskWatcher.UI;

namespace ZendeskWatcher.Tests;

/// <summary>
/// The SLA rules: the client's type from Zendesk (any value), the targets and where they come from (yours, default,
/// estimated), learning from Zendesk's timers, deadlines (weekends) and the red bold text.
/// </summary>
public class SlaTests
{
    [Theory]
    [InlineData("slastandard", "slastandard")]
    [InlineData("SlaPremium ", "slapremium")]
    [InlineData(null, "slastandard")]
    [InlineData("", "slastandard")]
    [InlineData("slagold", "slagold")] // a new type stays itself
    public void Type_comes_from_the_organization_field(string? value, string expected) =>
        Assert.Equal(expected, Sla.Parse(value));

    [Fact]
    public void Names_known_learned_or_the_value()
    {
        var learned = new SlaLearning { Names = { ["slagold"] = "Gold" } };
        Assert.Equal("Premium Support", Sla.Name("slapremium"));
        Assert.Equal("Gold", Sla.Name("slagold", learned));
        Assert.Equal("slasilver", Sla.Name("slasilver", learned));
    }

    [Fact]
    public void Defaults_by_type_and_priority()
    {
        Assert.Equal("15 min", Sla.TeamTarget("sla15min", "urgent", firstReply: true)!.Value.Label);
        Assert.Equal("4 h", Sla.TeamTarget("slastandard", "high", firstReply: true)!.Value.Label);
        Assert.Equal("1 working day", Sla.TeamTarget("slastandard", null, firstReply: true)!.Value.Label); // no priority: normal
        Assert.Equal("8 h", Sla.TeamTarget("slastandard", "high", firstReply: false)!.Value.Label); // follow-up
        Assert.Null(Sla.TeamTarget("slagold", "high", firstReply: true));
    }

    [Fact]
    public void Yours_then_default_then_estimated_then_standards()
    {
        List<SlaOverride> yours = [new() { Type = "slapremium", Priority = "high", FirstReply = true, Minutes = 120 }];
        var learned = new SlaLearning();
        for (var id = 1; id <= 3; id++)
            learned.Record(id, "slagold", "high", true, Start, Start.AddMinutes(45));

        Assert.Equal((SlaTarget.Hours(2), SlaSource.Custom), Sla.Resolve("slapremium", "high", true, yours, learned));
        Assert.Equal((SlaTarget.Hours(1), SlaSource.Default), Sla.Resolve("slapremium", "high", true, null, learned));
        Assert.Equal((SlaTarget.Min(45), SlaSource.Estimated), Sla.Resolve("slagold", "high", true, null, learned));
        // Nothing known about it: Standard's default.
        Assert.Equal((SlaTarget.Hours(4), SlaSource.Default), Sla.Resolve("slasilver", "high", true, null, learned));
    }

    private static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Learning_needs_agreeing_round_durations_and_counts_each_timer_once()
    {
        var learned = new SlaLearning();
        Assert.True(learned.Record(1, "slagold", "urgent", true, Start, Start.AddMinutes(30).AddSeconds(20)));
        Assert.Null(learned.Estimate("slagold", "urgent", true)); // one ticket isn't enough
        Assert.False(learned.Record(1, "slagold", "urgent", true, Start, Start.AddMinutes(30))); // the same timer again
        Assert.False(learned.Record(2, "slagold", "urgent", true, Start, Start.AddMinutes(1234))); // business hours: odd, left out
        Assert.True(learned.Record(3, "slagold", "urgent", true, Start, Start.AddMinutes(30)));
        Assert.Equal((SlaTarget.Min(30), 2), learned.Estimate("slagold", "urgent", true));
        Assert.Contains("slagold", learned.Types);

        // Disagreement: no estimate until most agree.
        learned.Record(4, "slagold", "urgent", true, Start, Start.AddMinutes(60));
        learned.Record(5, "slagold", "urgent", true, Start, Start.AddMinutes(60));
        Assert.Null(learned.Estimate("slagold", "urgent", true));
    }

    [Fact]
    public void P1_runs_around_the_clock_others_skip_the_weekend()
    {
        var friday = new DateTimeOffset(new DateTime(2026, 11, 13, 16, 0, 0, DateTimeKind.Local));
        Assert.Equal(friday.AddHours(1), Sla.Due(friday, "slastandard", "urgent", firstReply: true));
        Assert.Equal(friday.AddHours(4), Sla.Due(friday, "slastandard", "high", firstReply: true));
        // 1 working day from Friday 16:00 = Monday 16:00.
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 16, 16, 0, 0, DateTimeKind.Local)),
            Sla.Due(friday, "slastandard", "normal", firstReply: true));
        // Saturday morning, Standard P2: the clock starts Monday 00:00.
        var saturday = friday.AddHours(18);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 16, 4, 0, 0, DateTimeKind.Local)),
            Sla.Due(saturday, "slastandard", "high", firstReply: true));
        // 24x7: around the clock for every priority.
        Assert.Equal(saturday.AddHours(2), Sla.Due(saturday, "sla24x7", "high", firstReply: true));
    }

    [Fact]
    public void Due_text_counts_down_and_says_how_late()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("First reply due in 25 min", Sla.DueText(now.AddMinutes(25).AddSeconds(30), now, firstReply: true));
        Assert.Equal("Reply due in 3 h 10 min", Sla.DueText(now.AddMinutes(190).AddSeconds(30), now, firstReply: false));
        Assert.Equal("Reply overdue by 1 h 5 min", Sla.DueText(now.AddMinutes(-65), now, firstReply: false));
        Assert.Equal("Reply overdue by 2 days", Sla.DueText(now.AddDays(-2.2), now, firstReply: false));
    }

    [Fact]
    public void The_deadline_shows_in_red_bold_above_the_body()
    {
        var now = DateTimeOffset.UtcNow;
        var content = new NotificationContent(NotificationKind.NewTicket, "New ticket", "#1 Help", "Created 09:00")
            { SlaDue = now.AddMinutes(10).AddSeconds(30), SlaFirstReply = true };

        var body = content.DisplayBody(now);
        Assert.Equal("!!First reply due in 10 min!!\nCreated 09:00", body);
        var first = Markdown.Parse(body)[0];
        Assert.True(first.Alert && first.Bold);
        Assert.Equal("First reply due in 10 min\nCreated 09:00", Markdown.Strip(body)); // Windows notifications: plain
    }

    [Fact]
    public void Text_from_zendesk_never_turns_red()
    {
        var escaped = Markdown.Escape("Help!! Now!!");
        Assert.DoesNotContain(Markdown.Parse(escaped), r => r.Alert);
        Assert.Equal("Help!! Now!!", Markdown.Strip(escaped));
    }
}
