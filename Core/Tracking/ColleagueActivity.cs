using System.Text.Json;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// When people were last seen doing something in Zendesk, from everything the app reads anyway: each ticket history
/// and comment list (any reason: my own and CC'd tickets, colleagues' tickets, Catch up, Take it ▾) and Zendesk's
/// "assignee updated at". No requests of its own. A sighting proves someone was active at least that recently; no
/// sighting proves nothing (they may work in groups this app doesn't read). One for the app (<see cref="Shared"/>, used
/// by the checks, Take it ▾ and the Settings window alike), kept on disk for <see cref="CacheAges.ActivityKeep"/>
/// (cache\activity.json: user id and times, nothing else; <see cref="UseFile"/>); a test's fake Zendesk has its own.
/// </summary>
public sealed class ColleagueActivity
{
    /// <summary>The app's one memory, whichever connection read what.</summary>
    public static ColleagueActivity Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<long, DateTimeOffset> _seen = [];
    private readonly Dictionary<long, DateTimeOffset> _checked = [];
    private readonly Dictionary<long, DateTimeOffset> _failed = []; // memory only: a failed read isn't retried every check
    private string? _file, _subdomain;
    private bool _dirty;

    /// <summary>A change or comment by <paramref name="userId"/> at <paramref name="at"/>. Automations (author -1) are nobody.</summary>
    public void Saw(long userId, DateTimeOffset at)
    {
        if (userId <= 0 || at > DateTimeOffset.UtcNow.AddMinutes(5))
            return;
        lock (_gate)
            if (!_seen.TryGetValue(userId, out var known) || at > known)
            {
                _seen[userId] = at;
                _dirty = true;
            }
    }

    /// <summary>The latest sighting, or null.</summary>
    public DateTimeOffset? LastSeen(long userId)
    {
        lock (_gate)
            return _seen.TryGetValue(userId, out var at) ? at : null;
    }

    /// <summary>Their activity was read on purpose (Zendesk's "assignee updated at", or their latest tickets' histories) at <paramref name="at"/>.</summary>
    public void NoteChecked(long userId, DateTimeOffset at)
    {
        lock (_gate)
        {
            _checked[userId] = at;
            _failed.Remove(userId);
            _dirty = true;
        }
    }

    /// <summary>Reading their activity failed at <paramref name="at"/>: not tried again for a while (<see cref="LastRead"/>).</summary>
    public void NoteFailed(long userId, DateTimeOffset at)
    {
        lock (_gate)
            _failed[userId] = at;
    }

    /// <summary>The latest attempt to read their activity on purpose, and whether it worked; null when never tried.</summary>
    public (DateTimeOffset At, bool Known)? LastRead(long userId)
    {
        lock (_gate)
        {
            DateTimeOffset? ok = _checked.TryGetValue(userId, out var c) ? c : null;
            DateTimeOffset? failed = _failed.TryGetValue(userId, out var f) ? f : null;
            if (failed is { } fail && (ok is null || fail > ok))
                return (fail, false);
            return ok is { } read ? (read, true) : null;
        }
    }

    private sealed class Saved
    {
        public string Subdomain { get; set; } = "";
        public Dictionary<long, DateTimeOffset> Seen { get; set; } = [];
        public Dictionary<long, DateTimeOffset> Checked { get; set; } = [];
    }

    /// <summary>
    /// Kept in <paramref name="file"/> for this Zendesk: what's there (younger than <see cref="CacheAges.ActivityKeep"/>)
    /// is known at once, so a restart doesn't read everyone's activity again. Another Zendesk starts empty.
    /// </summary>
    public void UseFile(string file, string subdomain)
    {
        lock (_gate)
        {
            if (_file == file && _subdomain == subdomain)
                return;
            if (_subdomain is not null && _subdomain != subdomain)
            {
                _seen.Clear();
                _checked.Clear();
                _failed.Clear();
            }
            _file = file;
            _subdomain = subdomain;
            try
            {
                if (!File.Exists(file) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) is not { } saved || saved.Subdomain != subdomain)
                    return;
                var since = DateTimeOffset.UtcNow - CacheAges.ActivityKeep;
                foreach (var (id, at) in saved.Seen.Where(e => e.Value >= since))
                    if (!_seen.TryGetValue(id, out var known) || at > known)
                        _seen[id] = at;
                foreach (var (id, at) in saved.Checked.Where(e => e.Value >= since))
                    _checked.TryAdd(id, at);
            }
            catch (Exception ex)
            {
                Log.Error("Could not read the saved colleague activity", ex);
            }
        }
    }

    /// <summary>Writes to the file (<see cref="UseFile"/>) when something changed since the last save.</summary>
    public void Save()
    {
        string file, json;
        lock (_gate)
        {
            if (!_dirty || _file is null)
                return;
            _dirty = false;
            file = _file;
            var since = DateTimeOffset.UtcNow - CacheAges.ActivityKeep;
            json = JsonSerializer.Serialize(new Saved
            {
                Subdomain = _subdomain ?? "",
                Seen = _seen.Where(e => e.Value >= since).ToDictionary(),
                Checked = _checked.Where(e => e.Value >= since).ToDictionary()
            });
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, json);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the colleague activity", ex);
        }
    }

    /// <summary>When their activity was last read on purpose, or null: a recent read without a sighting means none lately.</summary>
    public DateTimeOffset? LastChecked(long userId)
    {
        lock (_gate)
            return _checked.TryGetValue(userId, out var at) ? at : null;
    }
}

/// <summary>A Zendesk session with its own activity memory instead of <see cref="ColleagueActivity.Shared"/> (a test's fake).</summary>
public interface IHasOwnActivity
{
    ColleagueActivity Activity { get; }
}
