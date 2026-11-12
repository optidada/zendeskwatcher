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
    /// <summary>Disappeared on its own: the popup timed out, or the Windows notification went to the notification center.</summary>
    TimedOut,

    /// <summary>Copied (for Teams). Not an ending: the notification stays, so it can also be opened afterwards.</summary>
    Copied
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
                foreach (var (name, count) in counts)
                {
                    var parts = name.Split('.');
                    if (parts.Length == 2 && Enum.TryParse<NotificationKind>(parts[0], out var kind) && Enum.TryParse<StatOutcome>(parts[1], out var outcome))
                        totals[(kind, outcome)] = totals.GetValueOrDefault((kind, outcome)) + count;
                }
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

    /// <summary>What a closed popup means, or null if it isn't worth counting.</summary>
    public static StatOutcome? ForPopup(PopupForm.Result result, string action) => result switch
    {
        PopupForm.Result.Open when action == Notifier.ActionOpenTicket => StatOutcome.OpenedTicket,
        PopupForm.Result.Open when action == Notifier.ActionOpenZendesk => StatOutcome.OpenedZendesk,
        PopupForm.Result.Take => StatOutcome.Took,
        PopupForm.Result.Snooze => StatOutcome.Snoozed,
        PopupForm.Result.Dismiss => StatOutcome.Closed,
        PopupForm.Result.Timeout => StatOutcome.TimedOut,
        _ => null
    };

    /// <summary>What a click on a Windows notification (or one of its buttons) means.</summary>
    public static StatOutcome? ForToastAction(string action) => action switch
    {
        Notifier.ActionOpenTicket => StatOutcome.OpenedTicket,
        Notifier.ActionOpenZendesk => StatOutcome.OpenedZendesk,
        Notifier.ActionTake => StatOutcome.Took,
        _ => null
    };

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
