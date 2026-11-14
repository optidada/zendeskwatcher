namespace ZendeskWatcher.Tracking;

/// <summary>Where a target comes from: changed by you, the team's default, or estimated from Zendesk's SLA timers.</summary>
public enum SlaSource { Default, Estimated, Custom }

/// <summary>A changed target (Notifications → SLA): only the ones that differ are stored.</summary>
public sealed class SlaOverride
{
    /// <summary>The SLA type's value in Zendesk, e.g. "slapremium".</summary>
    public string Type { get; set; } = Sla.StandardKey;
    /// <summary>urgent, high, normal or low.</summary>
    public string Priority { get; set; } = "normal";
    public bool FirstReply { get; set; }
    public int Minutes { get; set; }
    public int WorkingDays { get; set; }
}

/// <summary>A target: a time, or whole working days (Monday–Friday).</summary>
public readonly record struct SlaTarget(int Minutes, int WorkingDays = 0)
{
    public static SlaTarget Min(int minutes) => new(minutes);
    public static SlaTarget Hours(int hours) => new(hours * 60);
    public static SlaTarget Days(int workingDays) => new(0, workingDays);

    /// <summary>"15 min", "2 h", "1 day", "2 working days".</summary>
    public string Label => WorkingDays > 0 ? $"{WorkingDays} working day{(WorkingDays == 1 ? "" : "s")}"
        : Minutes < 60 ? $"{Minutes} min"
        : Minutes % 1440 == 0 ? $"{Minutes / 1440} day{(Minutes == 1440 ? "" : "s")}"
        : Minutes % 60 == 0 ? $"{Minutes / 60} h"
        : $"{Minutes / 60} h {Minutes % 60} min";
}

/// <summary>
/// SLA targets: how quickly we answer, by the client's SLA type (its organization's "SLA Type" in Zendesk, any value)
/// and the ticket's priority. First reply: a new unassigned ticket, from when it was created. Follow-up: a customer
/// waiting on an assigned ticket, from when they wrote. The target used: your change, else the team's default (the
/// known types), else one estimated from Zendesk's SLA timers (<see cref="SlaLearning"/>), else Standard's.
/// </summary>
public static class Sla
{
    public const string StandardKey = "slastandard";

    /// <summary>The types the team has targets for, most demanding first (values as in Zendesk).</summary>
    public static readonly string[] KnownTypes = ["sla15min", "slapremium", "slahighattention", "sla24x7", StandardKey];

    private static readonly Dictionary<string, string> KnownNames = new()
    {
        ["sla15min"] = "Urgent (15 Min)",
        ["slapremium"] = "Premium Support",
        ["slahighattention"] = "High Attention",
        ["sla24x7"] = "24x7",
        [StandardKey] = "Standard"
    };

    /// <summary>The name Zendesk shows: known ones, then names learned from Zendesk, else the value itself.</summary>
    public static string Name(string type, SlaLearning? learned = null) =>
        KnownNames.TryGetValue(type, out var name) ? name
        : learned?.Names.GetValueOrDefault(type) is { Length: > 0 } learnedName ? learnedName
        : type;

    /// <summary>The organization field's value as the type's key; none counts as Standard.</summary>
    public static string Parse(string? value) =>
        string.IsNullOrWhiteSpace(value) ? StandardKey : value.Trim().ToLowerInvariant();

    private static SlaTarget M(int minutes) => SlaTarget.Min(minutes);
    private static SlaTarget H(int hours) => SlaTarget.Hours(hours);
    private static SlaTarget D(int workingDays) => SlaTarget.Days(workingDays);

    /// <summary>First reply, per type: P1 (urgent), P2 (high), P3 (normal), P4 (low).</summary>
    private static readonly Dictionary<string, SlaTarget[]> FirstReply = new()
    {
        ["sla15min"] = [M(15), M(30), H(2), H(4)],
        ["slapremium"] = [M(30), H(1), H(4), H(8)],
        ["slahighattention"] = [M(30), H(2), H(4), H(8)],
        ["sla24x7"] = [H(1), H(2), H(8), H(24)],
        [StandardKey] = [H(1), H(4), D(1), D(2)]
    };

    /// <summary>Follow-up, per type: twice the first reply to begin with.</summary>
    private static readonly Dictionary<string, SlaTarget[]> FollowUp = new()
    {
        ["sla15min"] = [M(30), H(1), H(4), H(8)],
        ["slapremium"] = [H(1), H(2), H(8), H(16)],
        ["slahighattention"] = [H(1), H(4), H(8), H(16)],
        ["sla24x7"] = [H(2), H(4), H(16), H(48)],
        [StandardKey] = [H(2), H(8), D(2), D(4)]
    };

    public static readonly string[] Priorities = ["urgent", "high", "normal", "low"];

    /// <summary>Priority to column: urgent 0, high 1, normal (or none) 2, low 3.</summary>
    public static int Column(string? priority) => priority?.ToLowerInvariant() switch
    {
        "urgent" => 0,
        "high" => 1,
        "low" => 3,
        _ => 2
    };

    /// <summary>The team's default; null for a type without defaults (e.g. a new one in Zendesk).</summary>
    public static SlaTarget? TeamTarget(string type, string? priority, bool firstReply) =>
        (firstReply ? FirstReply : FollowUp).TryGetValue(type, out var row) ? row[Column(priority)] : null;

    /// <summary>The target in use and where it comes from: your change, the default, an estimate, else Standard's default.</summary>
    public static (SlaTarget Target, SlaSource Source) Resolve(string type, string? priority, bool firstReply,
        IReadOnlyList<SlaOverride>? overrides = null, SlaLearning? learned = null)
    {
        if (overrides?.LastOrDefault(o => o.Type == type && o.FirstReply == firstReply && Column(o.Priority) == Column(priority)) is { } changed)
            return (new SlaTarget(changed.Minutes, changed.WorkingDays), SlaSource.Custom);
        if (TeamTarget(type, priority, firstReply) is { } team)
            return (team, SlaSource.Default);
        if (learned?.Estimate(type, priority, firstReply) is { } estimate)
            return (estimate.Target, SlaSource.Estimated);
        return (TeamTarget(StandardKey, priority, firstReply)!.Value, SlaSource.Default);
    }

    /// <summary>The clock runs around the clock for P1, 24x7 and Urgent (15 Min); otherwise only Monday to Friday.</summary>
    public static bool AroundTheClock(string type, string? priority) =>
        Column(priority) == 0 || type is "sla24x7" or "sla15min";

    /// <summary>When the answer is due, counting from <paramref name="start"/>; weekends don't count unless around the clock.</summary>
    public static DateTimeOffset Due(DateTimeOffset start, string type, string? priority, bool firstReply,
        IReadOnlyList<SlaOverride>? overrides = null, SlaLearning? learned = null)
    {
        var (target, source) = Resolve(type, priority, firstReply, overrides, learned);
        var length = target.WorkingDays > 0 ? TimeSpan.FromDays(target.WorkingDays) : TimeSpan.FromMinutes(target.Minutes);
        // Estimates were measured on Zendesk's own clock, so they count around the clock like it did.
        if ((AroundTheClock(type, priority) || source == SlaSource.Estimated) && target.WorkingDays == 0)
            return start + length;
        return AddWeekdayTime(start, length);
    }

    /// <summary>Adds time that only runs Monday to Friday (local time): a ticket from Friday evening is due on Monday.</summary>
    internal static DateTimeOffset AddWeekdayTime(DateTimeOffset start, TimeSpan length)
    {
        var at = start.ToLocalTime();
        var left = length;
        while (left > TimeSpan.Zero)
        {
            if (at.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                at = new DateTimeOffset(at.Date.AddDays(1), at.Offset); // jump to the next midnight
                continue;
            }
            var untilMidnight = at.Date.AddDays(1) - at.DateTime;
            var step = left < untilMidnight ? left : untilMidnight;
            at += step;
            left -= step;
        }
        return at.ToUniversalTime();
    }

    /// <summary>"First reply due in 25 min", "Reply overdue by 1 h 10 min".</summary>
    public static string DueText(DateTimeOffset due, DateTimeOffset now, bool firstReply)
    {
        var what = firstReply ? "First reply" : "Reply";
        var left = due - now;
        return left >= TimeSpan.Zero ? $"{what} due in {Span(left)}" : $"{what} overdue by {Span(-left)}";
    }

    private static string Span(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "less than a minute",
        < 60 => $"{(int)span.TotalMinutes} min",
        < 60 * 24 when span.Minutes > 0 && span.TotalHours < 10 => $"{(int)span.TotalHours} h {span.Minutes} min",
        < 60 * 24 => $"{(int)Math.Round(span.TotalHours)} h",
        _ => (int)span.TotalDays == 1 ? "1 day" : $"{(int)span.TotalDays} days"
    };
}

/// <summary>
/// What the app learns about the SLA policies from Zendesk's SLA timers, since agents can't read the policies: for a
/// ticket with a running timer, the time from when the clock started to when it breaches. Only round durations count
/// (15 min, 1 h, 4 h, …): business hours and paused clocks give odd ones, which are left out. A target is estimated
/// once at least <see cref="MinAgreeing"/> tickets agree and they're most of what was seen. Kept in state.json.
/// </summary>
public sealed class SlaLearning
{
    /// <summary>The latest, for the Settings window (the checks update it).</summary>
    public static SlaLearning? Current { get; set; }

    public const int MinAgreeing = 2;

    /// <summary>Durations that count as round (minutes): what SLA policies use.</summary>
    private static readonly int[] RoundMinutes = [15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 600, 720, 960, 1440, 2880, 4320];

    /// <summary>SLA types seen on clients' organizations (values), also new ones.</summary>
    public HashSet<string> Types { get; set; } = [];

    /// <summary>Names of SLA types as Zendesk shows them (from the organization field's options).</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    /// <summary>"type|column|first or follow" → minutes → how many tickets.</summary>
    public Dictionary<string, Dictionary<int, int>> Observed { get; set; } = [];

    /// <summary>"ticket|metric" → when recorded, so a ticket counts once per timer (forgotten after 60 days).</summary>
    public Dictionary<string, DateTimeOffset> Recorded { get; set; } = [];

    private static string Key(string type, string? priority, bool firstReply) =>
        $"{type}|{Sla.Column(priority)}|{(firstReply ? "first" : "follow")}";

    /// <summary>Notes a running timer: from <paramref name="start"/> to <paramref name="breachAt"/>. True if it counted.</summary>
    public bool Record(long ticketId, string type, string? priority, bool firstReply, DateTimeOffset start, DateTimeOffset breachAt)
    {
        Types.Add(type);
        var once = $"{ticketId}|{(firstReply ? "first" : "follow")}|{start.ToUnixTimeSeconds()}";
        if (!Recorded.TryAdd(once, DateTimeOffset.UtcNow))
            return false;
        foreach (var old in Recorded.Where(kv => DateTimeOffset.UtcNow - kv.Value > TimeSpan.FromDays(60)).Select(kv => kv.Key).ToList())
            Recorded.Remove(old);

        var minutes = (breachAt - start).TotalMinutes;
        var round = RoundMinutes.FirstOrDefault(r => Math.Abs(r - minutes) <= Math.Max(2, r * 0.01));
        if (round == 0)
            return false; // business hours or a paused clock: not a clean measurement
        var key = Key(type, priority, firstReply);
        if (!Observed.TryGetValue(key, out var counts))
            Observed[key] = counts = [];
        counts[round] = counts.GetValueOrDefault(round) + 1;
        return true;
    }

    /// <summary>The estimated target and how many tickets back it; null without enough agreeing tickets.</summary>
    public (SlaTarget Target, int Tickets)? Estimate(string type, string? priority, bool firstReply)
    {
        if (!Observed.TryGetValue(Key(type, priority, firstReply), out var counts) || counts.Count == 0)
            return null;
        var (minutes, count) = counts.MaxBy(kv => kv.Value) is var best ? (best.Key, best.Value) : (0, 0);
        return count >= MinAgreeing && count * 10 >= counts.Values.Sum() * 6 ? (SlaTarget.Min(minutes), count) : null;
    }
}
