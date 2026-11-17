using ZendeskWatcher.Tracking;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>Insights → Team workload: open tickets per teammate.</summary>
public class TeamWorkloadTests
{
    private static Ticket T(long id, long? assignee, string status, string? priority = "normal", long? customStatus = null) =>
        new() { Id = id, AssigneeId = assignee, Status = status, Priority = priority, CustomStatusId = customStatus };

    private static readonly Dictionary<long, string> Statuses = new()
    {
        [77] = "Escalated to Engineering", [78] = "Escalated internally", [79] = "Scheduled", [80] = "In progress"
    };

    [Fact]
    public void Open_in_progress_urgent_escalated_scheduled_and_waiting_per_teammate_busiest_first() // Maria 3*3+1 = 10, Alex 2+2+1+2 = 7
    {
        var team = new Dictionary<long, string> { [1] = "Maria", [2] = "Alex", [3] = "Robin" };
        Ticket[] tickets =
        [
            T(10, 1, "open", "urgent"), T(11, 1, "new", "high"), T(12, 1, "open", "high", customStatus: 80), T(13, 1, "pending"),
            T(20, 2, "open"), T(21, 2, "hold"), T(22, 2, "pending"),
            T(23, 2, "hold", "urgent", customStatus: 77), T(24, 2, "open", customStatus: 78), // escalated: their own column
            T(25, 2, "hold", customStatus: 79),                                               // scheduled: its own column
            T(30, 9, "open"),        // not in the team
            T(12, 1, "open", "high", customStatus: 80), // the same ticket found in a second group
            T(40, null, "new")       // unassigned
        ];

        var rows = TeamWorkload.Count(tickets, team, meId: 2, Statuses);

        Assert.Equal(["Maria", "Alex", "Robin"], rows.Select(r => r.Name));
        Assert.Equal((2, 1, 3, 0, 0, 1), (rows[0].Open, rows[0].InProgress, rows[0].UrgentOrHigh, rows[0].Escalated, rows[0].Scheduled, rows[0].Pending));
        Assert.Equal(10, TeamWorkload.Score(rows[0])); // In progress weighs like Open
        Assert.Equal((1, 0, 2, 1, 2, true), (rows[1].Open, rows[1].UrgentOrHigh, rows[1].Escalated, rows[1].Scheduled, rows[1].Pending, rows[1].IsMe));
        Assert.Equal(1, rows[1].EscalatedBy!["Escalated to Engineering"]); // and per status
        Assert.Equal(1, rows[1].EscalatedBy!["Escalated internally"]);
        Assert.Equal(7, TeamWorkload.Score(rows[1]));
        Assert.Equal((0, 0, 0), (rows[2].Open, rows[2].UrgentOrHigh, rows[2].Pending)); // no tickets: still listed
    }

    [Theory]
    [InlineData("Escalated to Engineering", true)]
    [InlineData("Escalated internally", true)]
    [InlineData("In progress", false)]
    [InlineData(null, false)]
    public void Escalated_statuses_by_name(string? label, bool expected) => Assert.Equal(expected, TeamWorkload.IsEscalatedStatus(label));

    [Theory]
    [InlineData("In progress", true)]
    [InlineData("In-Progress", true)]
    [InlineData("Work in progress", true)]
    [InlineData("Open", false)]
    [InlineData(null, false)]
    public void In_progress_statuses_by_name(string? label, bool expected) => Assert.Equal(expected, TeamWorkload.IsInProgressStatus(label));

    [Theory]
    [InlineData("Escalated to Engineering", "Esc. Eng.")]
    [InlineData("Escalated internally", "Esc. Int.")]
    [InlineData("escalated - tier 2", "Esc. Tier")]
    [InlineData("Escalated", "Escalated")]
    public void Short_headings_for_escalated_statuses(string label, string expected) => Assert.Equal(expected, TeamWorkload.ShortEscalatedName(label));

    [Fact]
    public void Solved_and_closed_count_as_solved_not_as_workload()
    {
        var team = new Dictionary<long, string> { [1] = "Maria" };
        var rows = TeamWorkload.Count([T(1, 1, "solved"), T(2, 1, "closed", customStatus: 77), T(3, 1, "pending")], team, meId: 9, Statuses);

        Assert.Equal((2, 1, 0), (rows[0].Solved, rows[0].Pending, rows[0].Escalated));
        Assert.Equal(1, TeamWorkload.Score(rows[0]));
    }

    [Fact]
    public void Short_names_unless_two_would_look_the_same()
    {
        var names = TeamWorkload.ShortNames(["Maria Svensson", "You", "Anna van der Berg", "Kim Larsen", "Kim Lund"]);

        Assert.Equal("Maria S.", names["Maria Svensson"]);
        Assert.Equal("You", names["You"]);
        Assert.Equal("Anna B.", names["Anna van der Berg"]);
        Assert.Equal("Kim Larsen", names["Kim Larsen"]);
        Assert.Equal("Kim Lund", names["Kim Lund"]);
    }

    private static WorkloadRow Row(long id, string name, int open, int urgent = 0, int escalated = 0, int scheduled = 0, int pending = 0, int inProgress = 0) =>
        new(id, name, open, urgent, escalated, scheduled, pending, false, inProgress);

    [Fact]
    public void Summary_says_only_what_there_is()
    {
        Assert.Equal("10 open (2 P1/P2) · 3 escalated · 1 scheduled · 6 waiting",
            TeamWorkload.Summary(Row(1, "A", 6, urgent: 2, escalated: 3, scheduled: 1, pending: 6, inProgress: 4)));
        Assert.Equal("2 open", TeamWorkload.Summary(Row(1, "A", 2)));
        Assert.Equal("4 waiting", TeamWorkload.Summary(Row(1, "A", 0, pending: 4)));
        Assert.Equal("nothing open", TeamWorkload.Summary(Row(1, "A", 0)));
    }

    [Fact]
    public void Off_work_in_their_own_time_zone_and_unknown_without_one()
    {
        var weekdays = TicketRules.Weekdays;
        var tuesdayNoonUtc = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        Assert.False(TeamWorkload.OffWork(new User { IanaTimeZone = "Europe/Stockholm" }, tuesdayNoonUtc, 8, 17, weekdays)); // 13:00
        Assert.True(TeamWorkload.OffWork(new User { IanaTimeZone = "Asia/Tokyo" }, tuesdayNoonUtc, 8, 17, weekdays));        // 21:00
        Assert.Null(TeamWorkload.OffWork(new User(), tuesdayNoonUtc, 8, 17, weekdays));
    }

    [Fact]
    public void Order_puts_people_off_work_last_and_sorts_either_way()
    {
        WorkloadRow[] rows = [Row(1, "Busy", 8), Row(2, "Light", 1), Row(3, "Off", 20), Row(4, "Middle", 4)];
        Func<long, bool> off = id => id == 3;
        Assert.Equal(["Busy", "Middle", "Light", "Off"], TeamWorkload.Order(rows, off, busiestFirst: true).Select(r => r.Name));
        Assert.Equal(["Light", "Middle", "Busy", "Off"], TeamWorkload.Order(rows, off, busiestFirst: false).Select(r => r.Name));
    }

    [Fact]
    public void Rows_survive_the_saved_file() // Insights keeps them in cache\team-workload.json (System.Text.Json, like ListCache)
    {
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        List<WorkloadRow> rows = [new(1, "Maria", 3, 1, 2, 1, 4, true, 2, new Dictionary<string, int> { ["Escalated to Engineering"] = 2 })];
        var back = System.Text.Json.JsonSerializer.Deserialize<List<WorkloadRow>>(System.Text.Json.JsonSerializer.Serialize(rows, options), options)!;
        Assert.Equal(rows[0] with { EscalatedBy = null }, back[0] with { EscalatedBy = null });
        Assert.Equal(2, back[0].EscalatedBy!["Escalated to Engineering"]);
    }
}
