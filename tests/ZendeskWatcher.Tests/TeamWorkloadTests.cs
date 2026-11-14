using ZendeskWatcher.Tracking;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tests;

/// <summary>Insights → Your team: open tickets per teammate.</summary>
public class TeamWorkloadTests
{
    private static Ticket T(long id, long? assignee, string status, string? priority = "normal", long? customStatus = null) =>
        new() { Id = id, AssigneeId = assignee, Status = status, Priority = priority, CustomStatusId = customStatus };

    private static readonly Dictionary<long, string> Statuses = new()
    {
        [77] = "Escalated to Engineering", [78] = "Escalated internally", [79] = "Scheduled", [80] = "In progress"
    };

    [Fact]
    public void Open_urgent_escalated_scheduled_and_waiting_per_teammate_busiest_first() // Maria 2+3+2+1 = 8, Alex 2+3 = 5
    {
        var team = new Dictionary<long, string> { [1] = "Maria", [2] = "Alex", [3] = "Robin" };
        Ticket[] tickets =
        [
            T(10, 1, "open", "urgent"), T(11, 1, "new", "high"), T(12, 1, "open", customStatus: 80), T(13, 1, "pending"),
            T(20, 2, "open"), T(21, 2, "hold"), T(22, 2, "pending"),
            T(23, 2, "hold", "urgent", customStatus: 77), T(24, 2, "open", customStatus: 78), // escalated: their own column
            T(25, 2, "hold", customStatus: 79),                                               // scheduled: its own column
            T(30, 9, "open"),        // not in the team
            T(12, 1, "open", customStatus: 80), // the same ticket found in a second group
            T(40, null, "new")       // unassigned
        ];

        var rows = TeamWorkload.Count(tickets, team, meId: 2, Statuses);

        Assert.Equal(["Maria", "Alex", "Robin"], rows.Select(r => r.Name));
        Assert.Equal((3, 2, 0, 0, 1), (rows[0].Open, rows[0].UrgentOrHigh, rows[0].Escalated, rows[0].Scheduled, rows[0].Waiting)); // In progress is open
        Assert.Equal((1, 0, 2, 1, 2, true), (rows[1].Open, rows[1].UrgentOrHigh, rows[1].Escalated, rows[1].Scheduled, rows[1].Waiting, rows[1].IsMe));
        Assert.Equal((0, 0, 0), (rows[2].Open, rows[2].UrgentOrHigh, rows[2].Waiting)); // no tickets: still listed
    }

    [Theory]
    [InlineData("Escalated to Engineering", true)]
    [InlineData("Escalated internally", true)]
    [InlineData("In progress", false)]
    [InlineData(null, false)]
    public void Escalated_statuses_by_name(string? label, bool expected) => Assert.Equal(expected, TeamWorkload.IsEscalatedStatus(label));
}
