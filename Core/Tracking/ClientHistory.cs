using System.Text.Json;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The groups' ticket records (<see cref="GroupDigest"/>), saved in cache\clients-digest.json: read by the Clients page
/// (only what changed is asked for next time). Ticket number, client, assignee and date only.
/// </summary>
public static class ClientHistory
{
    private sealed class Entry
    {
        public string Subdomain { get; set; } = "";
        public DateTime At { get; set; }
        public Dictionary<long, GroupDigest>? Value { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    private static string FileFor() => Path.Combine(AppPaths.DataDir, "cache", "clients-digest.json");

    /// <summary>The saved records for this account (empty when none or another account's).</summary>
    public static Dictionary<long, GroupDigest> Load(string subdomain)
    {
        try
        {
            lock (Gate)
            {
                var file = FileFor();
                if (!File.Exists(file))
                    return [];
                var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(file), JsonOptions);
                return entry?.Value is { } value && entry.Subdomain == subdomain ? value : [];
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the saved client history", ex);
            return [];
        }
    }

    public static void Save(string subdomain, Dictionary<long, GroupDigest> digests)
    {
        try
        {
            lock (Gate)
            {
                var file = FileFor();
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(new Entry { Subdomain = subdomain, At = DateTime.UtcNow, Value = digests }, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the client history", ex);
        }
    }

    /// <summary>Reload on the Clients page: everything is read again next time.</summary>
    public static void Clear()
    {
        try
        {
            lock (Gate)
                File.Delete(FileFor());
        }
        catch (Exception ex)
        {
            Log.Error("Could not clear the client history", ex);
        }
    }
}
