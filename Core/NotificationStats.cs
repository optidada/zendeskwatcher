using System.Text.Json;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

/// <summary>What happened to a ticket notification.</summary>
public enum StatOutcome
{
    /// <summary>It was shown (counted once; a snoozed one coming back isn't counted again).</summary>
    Shown,
    OpenedTicket,
    /// <summary>Opened Zendesk instead of a ticket, e.g. from "6 new unassigned tickets".</summary>
    OpenedZendesk,
    Took,
    Snoozed,
    /// <summary>Closed with ✕ (or dismissed in Windows).</summary>
    Closed,
    /// <summary>Disappeared on its own: the countdown ran out.</summary>
    TimedOut,

    /// <summary>Copied (for Teams). Not an ending: the notification stays, so it can also be opened afterwards.</summary>
    Copied,

    /// <summary>
    /// Acted on, counted once per notification: any reaction (opened, took or assigned it, copied it or its number,
    /// snoozed); not closing, a choice for the client, or only reading it. Days before this was counted are estimated (see <see cref="Totals"/>).
    /// </summary>
    Acted
}

/// <summary>
/// How many ticket notifications were shown and what was done with them, per day and kind, for the last
/// <see cref="KeepDays"/> days. Stored in stats.json on this PC only.
/// </summary>
public sealed class NotificationStats
{
    public const int KeepDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static NotificationStats? _shared;

    private readonly string? _file;
    private readonly object _gate = new();

    /// <summary>Day ("2026-11-12") -> "Kind.Outcome" ("NewTicket.Took") -> count. Plain strings, so new values never break old files.</summary>
    private Dictionary<string, Dictionary<string, int>> _days = [];

    /// <param name="file">Where to keep them; null keeps them in memory only (tests, screenshots).</param>
    public NotificationStats(string? file = null)
    {
        _file = file;
        if (file is null || !File.Exists(file))
            return;
        try
        {
            _days = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(file)) ?? [];
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the notification statistics, starting fresh", ex);
        }
    }

    /// <summary>The app's statistics (stats.json in the data folder).</summary>
    public static NotificationStats Shared => _shared ??= new NotificationStats(Path.Combine(AppPaths.DataDir, "stats.json"));

    public void Record(NotificationKind kind, StatOutcome outcome, DateOnly? day = null)
    {
        var today = day ?? DateOnly.FromDateTime(DateTime.Now);
        lock (_gate)
        {
            var key = today.ToString("yyyy-MM-dd");
            if (!_days.TryGetValue(key, out var counts))
                _days[key] = counts = [];
            var name = $"{kind}.{outcome}";
            counts[name] = counts.GetValueOrDefault(name) + 1;

            // Keep only the last 30 days.
            var oldest = today.AddDays(-(KeepDays - 1));
            foreach (var old in _days.Keys.Where(d => DateOnly.TryParse(d, out var date) && date < oldest).ToList())
                _days.Remove(old);
            Save();
        }
    }

    /// <summary>
    /// Counts what happened to a client's notification, for the per-client suggestion on Insights. Stored in the same
    /// day as "Client|id|name|Outcome" ('|' so the kind totals never mistake it for "Kind.Outcome").
    /// </summary>
    public void RecordClient(long clientId, string name, StatOutcome outcome, DateOnly? day = null)
    {
        var today = day ?? DateOnly.FromDateTime(DateTime.Now);
        lock (_gate)
        {
            var key = today.ToString("yyyy-MM-dd");
            if (!_days.TryGetValue(key, out var counts))
                _days[key] = counts = [];
            var entry = $"{ClientPrefix}{clientId}|{name.Replace('|', '/')}|{outcome}";
            counts[entry] = counts.GetValueOrDefault(entry) + 1;
            Save();
        }
    }

    private const string ClientPrefix = "Client|";

    /// <summary>A new ticket for another product that wasn't notified (My products). Stored as "HeldBack|OtherProduct".</summary>
    public void RecordHeldBack(DateOnly? day = null)
    {
        var today = day ?? DateOnly.FromDateTime(DateTime.Now);
        lock (_gate)
        {
            var key = today.ToString("yyyy-MM-dd");
            if (!_days.TryGetValue(key, out var counts))
                _days[key] = counts = [];
            counts[HeldBackKey] = counts.GetValueOrDefault(HeldBackKey) + 1;
            Save();
        }
    }

    private const string HeldBackKey = "HeldBack|OtherProduct";

    /// <summary>New tickets for other products not notified in the <paramref name="days"/> days up to <paramref name="today"/>.</summary>
    public int HeldBack(DateOnly today, int days = KeepDays)
    {
        var oldest = today.AddDays(-(days - 1));
        lock (_gate)
        {
            return _days.Where(d => DateOnly.TryParse(d.Key, out var date) && date >= oldest && date <= today)
                .Sum(d => d.Value.GetValueOrDefault(HeldBackKey));
        }
    }

    /// <summary>Before "acted on" was counted: the opened, taken and copied ones, at most as many as were shown.</summary>
    private static int EstimatedActed(int shown, int[] positive) => Math.Min(shown, positive.Sum());

    /// <summary>Per client in the period: its last seen name, notifications shown, and how many were acted on.</summary>
    public List<(long Id, string Name, int Shown, int Acted)> ClientTotals(DateOnly today, int days = KeepDays)
    {
        var oldest = today.AddDays(-(days - 1));
        var totals = new Dictionary<long, (string Name, int Shown, int Acted)>();
        lock (_gate)
        {
            foreach (var (day, counts) in _days.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                if (!DateOnly.TryParse(day, out var date) || date < oldest || date > today)
                    continue;
                var day_ = new Dictionary<long, (string Name, int Shown, int Acted, int Positive, bool Counted)>();
                foreach (var (entry, count) in counts)
                {
                    var parts = entry.Split('|');
                    if (parts.Length != 4 || parts[0] != "Client" || !long.TryParse(parts[1], out var id) || !Enum.TryParse<StatOutcome>(parts[3], out var outcome))
                        continue;
                    var (_, shown, acted, positive, counted) = day_.GetValueOrDefault(id);
                    day_[id] = (parts[2],
                        shown + (outcome == StatOutcome.Shown ? count : 0),
                        acted + (outcome == StatOutcome.Acted ? count : 0),
                        positive + (outcome is StatOutcome.OpenedTicket or StatOutcome.OpenedZendesk or StatOutcome.Took or StatOutcome.Copied or StatOutcome.Snoozed ? count : 0),
                        counted || outcome == StatOutcome.Acted);
                }
                foreach (var (id, d) in day_)
                {
                    var (_, shown, acted) = totals.GetValueOrDefault(id);
                    totals[id] = (d.Name, shown + d.Shown, acted + (d.Counted ? d.Acted : EstimatedActed(d.Shown, [d.Positive])));
                }
            }
        }
        return totals.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Shown, kv.Value.Acted)).ToList();
    }

    /// <summary>Totals per kind and outcome for the <paramref name="days"/> days up to and including <paramref name="today"/>.</summary>
    public Dictionary<(NotificationKind Kind, StatOutcome Outcome), int> Totals(DateOnly today, int days = KeepDays)
    {
        var oldest = today.AddDays(-(days - 1));
        var totals = new Dictionary<(NotificationKind, StatOutcome), int>();
        lock (_gate)
        {
            foreach (var (day, counts) in _days)
            {
                if (!DateOnly.TryParse(day, out var date) || date < oldest || date > today)
                    continue;
                var dayTotals = new Dictionary<(NotificationKind Kind, StatOutcome Outcome), int>();
                foreach (var (name, count) in counts)
                {
                    var parts = name.Split('.');
                    if (parts.Length == 2 && Enum.TryParse<NotificationKind>(parts[0], out var kind) && Enum.TryParse<StatOutcome>(parts[1], out var outcome))
                        dayTotals[(kind, outcome)] = dayTotals.GetValueOrDefault((kind, outcome)) + count;
                }
                // A day without "acted on" counts (before it was counted): estimated from the opened, taken and copied ones.
                foreach (var kind in dayTotals.Keys.Select(k => k.Kind).Distinct().ToList())
                    if (!dayTotals.ContainsKey((kind, StatOutcome.Acted)))
                        dayTotals[(kind, StatOutcome.Acted)] = EstimatedActed(dayTotals.GetValueOrDefault((kind, StatOutcome.Shown)),
                            [.. new[] { StatOutcome.OpenedTicket, StatOutcome.OpenedZendesk, StatOutcome.Took, StatOutcome.Copied, StatOutcome.Snoozed }
                                .Select(o => dayTotals.GetValueOrDefault((kind, o)))]);
                foreach (var (key, count) in dayTotals)
                    totals[key] = totals.GetValueOrDefault(key) + count;
            }
        }
        return totals;
    }

    /// <summary>Days with at least one ticket notification shown, in the <paramref name="days"/> days up to <paramref name="today"/>.</summary>
    public int ActiveDays(DateOnly today, int days = KeepDays)
    {
        var oldest = today.AddDays(-(days - 1));
        lock (_gate)
        {
            return _days.Count(d => DateOnly.TryParse(d.Key, out var date) && date >= oldest && date <= today &&
                                    d.Value.Any(c => c.Key.EndsWith($".{StatOutcome.Shown}") && c.Value > 0));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _days.Clear();
            Save();
        }
    }

    private void Save()
    {
        if (_file is null)
            return;
        try
        {
            var temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_days, JsonOptions));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the notification statistics", ex);
        }
    }
}
