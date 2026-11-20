using System.Text.Json;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// "Usually handles this client" for Take it ▾: per client and group, how many of the client's tickets (last 90 days)
/// each agent has. Kept on disk (cache\client-handlers.json: ids and counts only) for
/// <see cref="CacheAges.ClientHandlersKeep"/>, so it survives a restart. Which age is fresh is the caller's rule.
/// </summary>
internal static class ClientHandlerStore
{
    private sealed class Saved
    {
        public string Subdomain { get; set; } = "";
        public long Group { get; set; }
        public long Client { get; set; }
        public DateTime At { get; set; }
        public Dictionary<long, int> Counts { get; set; } = [];
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Subdomain, long Group, long Client), (DateTime At, Dictionary<long, int> Counts)> Known = [];
    private static readonly HashSet<(string Subdomain, long Group, long Client)> Reading = [];
    private static bool _loaded;

    private static string FileFor() => Path.Combine(AppPaths.DataDir, "cache", "client-handlers.json");

    /// <summary>The counts and when they were read; default when never read (or too long ago).</summary>
    public static (DateTime At, Dictionary<long, int>? Counts) Get((string, long, long) key)
    {
        lock (Gate)
        {
            LoadOnce();
            return Known.TryGetValue(key, out var known) ? known : default;
        }
    }

    /// <summary>False when that client is already being read.</summary>
    public static bool StartRead((string, long, long) key)
    {
        lock (Gate)
            return Reading.Add(key);
    }

    public static void EndRead((string, long, long) key)
    {
        lock (Gate)
            Reading.Remove(key);
    }

    public static void Set((string Subdomain, long Group, long Client) key, Dictionary<long, int> counts)
    {
        string json;
        lock (Gate)
        {
            LoadOnce();
            Known[key] = (DateTime.UtcNow, counts);
            var now = DateTime.UtcNow;
            json = JsonSerializer.Serialize(Known.Where(k => now - k.Value.At <= CacheAges.ClientHandlersKeep).Select(k => new Saved
            {
                Subdomain = k.Key.Subdomain, Group = k.Key.Group, Client = k.Key.Client, At = k.Value.At, Counts = k.Value.Counts
            }).ToList());
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileFor())!);
                File.WriteAllText(FileFor(), json);
            }
            catch (Exception ex)
            {
                Log.Error("Could not save who handles which client", ex);
            }
        }
    }

    /// <summary>Tests: forgets what's in memory, as after a restart (the file is read again).</summary>
    internal static void Reload()
    {
        lock (Gate)
        {
            Known.Clear();
            _loaded = false;
        }
    }

    private static void LoadOnce()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (!File.Exists(FileFor()) || JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(FileFor())) is not { } saved)
                return;
            var now = DateTime.UtcNow;
            foreach (var entry in saved.Where(e => now - e.At <= CacheAges.ClientHandlersKeep))
                Known.TryAdd((entry.Subdomain, entry.Group, entry.Client), (entry.At, entry.Counts));
        }
        catch (Exception ex)
        {
            Log.Error("Could not read who handles which client", ex);
        }
    }
}
