using System.Text.Json;

namespace ZendeskWatcher.Zendesk;

/// <summary>
/// Users read from Zendesk, remembered for a week across checks, Settings saves and app restarts, so a start
/// doesn't look up every colleague and author again. Kept in the data folder (cache\users.json): agents and admins as
/// read (name, email, role, time zone, tags); people at clients only by name, role and email domain ("*@client.com",
/// enough to tell a client from a colleague), never their address, time zone or tags.
/// </summary>
public sealed class UserStore(string file, string subdomain)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7); // a profile change (time zone, name) shows within a week

    internal sealed class Entry
    {
        public User User { get; set; } = new();
        public DateTime At { get; set; }
    }

    private sealed class Saved
    {
        public string Subdomain { get; set; } = "";
        public List<Entry> Users { get; set; } = [];
    }

    private readonly Dictionary<long, Entry> _users = [];
    private readonly object _lock = new();
    private bool _loaded, _dirty;

    public string Subdomain => subdomain;

    public bool TryGet(long id, out User user)
    {
        lock (_lock)
        {
            LoadOnce();
            if (_users.TryGetValue(id, out var entry) && DateTime.UtcNow - entry.At < Lifetime)
            {
                user = entry.User;
                return true;
            }
            user = null!;
            return false;
        }
    }

    public void Remember(User user)
    {
        lock (_lock)
        {
            LoadOnce();
            // Written to disk only when it's news (someone new, a changed name, role or time zone, or a day since the last
            // write): users come along with every ticket history read now, and the file needn't be rewritten each check.
            var now = DateTime.UtcNow;
            var known = _users.TryGetValue(user.Id, out var old);
            _dirty |= !known || now - old!.At > TimeSpan.FromDays(1) || old.User.Name != user.Name || old.User.Role != user.Role
                      || old.User.IanaTimeZone != user.IanaTimeZone || old.User.TimeZone != user.TimeZone;
            _users[user.Id] = new Entry { User = user, At = known && now - old!.At <= TimeSpan.FromDays(1) ? old.At : now };
        }
    }

    /// <summary>Writes them to disk when one was added since the last save.</summary>
    public void Save()
    {
        Saved saved;
        lock (_lock)
        {
            if (!_dirty)
                return;
            _dirty = false;
            var now = DateTime.UtcNow;
            saved = new Saved { Subdomain = subdomain, Users = [.. _users.Values.Where(e => now - e.At < Lifetime).Select(OnDisk)] };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(saved));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the remembered users", ex);
        }
    }

    private static bool IsStaff(User user) => user.Role is "agent" or "admin";

    /// <summary>What's written to disk: staff as read; anyone else only name, role and email domain.</summary>
    internal static Entry OnDisk(Entry entry) => IsStaff(entry.User) ? entry : new Entry
    {
        At = entry.At,
        User = new User
        {
            Id = entry.User.Id, Name = entry.User.Name, Role = entry.User.Role,
            Email = entry.User.Email?.LastIndexOf('@') is > 0 and var at ? "*" + entry.User.Email[at..] : null
        }
    };

    private void LoadOnce()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (!File.Exists(file) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) is not { } saved || saved.Subdomain != subdomain)
                return;
            foreach (var entry in saved.Users.Where(e => DateTime.UtcNow - e.At < Lifetime))
                _users[entry.User.Id] = entry;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the remembered users", ex);
        }
    }
}
