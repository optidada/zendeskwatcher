using System.Text.Json;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// The lists Settings loads from Zendesk (groups and suggestions, group members, clients), kept in the data folder
/// (cache\*.json) so they survive a restart. Used for <see cref="GroupsPage.CacheLifetime"/> after they were fetched,
/// for the same Zendesk account; Reload fetches them again. Names only, no ticket content.
/// </summary>
internal static class ListCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    private sealed class Entry<T> where T : class
    {
        public string Subdomain { get; set; } = "";
        public DateTime At { get; set; }
        public T? Value { get; set; }
    }

    private static string FileFor(string name) => Path.Combine(AppPaths.DataDir, "cache", name + ".json");

    /// <summary>
    /// The saved list, if it's for this account and still fresh (any age with <paramref name="anyAge"/>: shown while a
    /// fresh one loads, or while signed out); null otherwise (or if it can't be read).
    /// </summary>
    public static (DateTime At, T Value)? Load<T>(string name, string subdomain, bool anyAge = false) where T : class
    {
        try
        {
            var file = FileFor(name);
            if (!File.Exists(file))
                return null;
            var entry = JsonSerializer.Deserialize<Entry<T>>(File.ReadAllText(file), JsonOptions);
            if (entry?.Value is null || entry.Subdomain != subdomain || !anyAge && DateTime.UtcNow - entry.At > GroupsPage.CacheLifetime)
                return null;
            return (entry.At, entry.Value);
        }
        catch (Exception ex)
        {
            Log.Error($"Settings: could not read the saved {name} list", ex);
            return null;
        }
    }

    public static void Save<T>(string name, string subdomain, DateTime at, T value) where T : class
    {
        try
        {
            var file = FileFor(name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new Entry<T> { Subdomain = subdomain, At = at, Value = value }, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"Settings: could not save the {name} list", ex);
        }
    }

    /// <summary>
    /// Notes who the saved lists belong to. True when they were saved for someone else (another user or Zendesk): then
    /// they're fetched again. Signing in again as the same person keeps them.
    /// </summary>
    public static bool ChangeOwner(string subdomain, long userId)
    {
        var owner = $"{subdomain}/{userId}";
        try
        {
            var file = Path.Combine(AppPaths.DataDir, "cache", "owner.txt");
            var previous = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            if (previous == owner)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, owner);
            return previous is not null; // lists saved before owners were noted: the subdomain check covers them
        }
        catch (Exception ex)
        {
            Log.Error("Settings: could not note who the saved lists belong to", ex);
            return false;
        }
    }

    /// <summary>Reload: forget the saved list.</summary>
    public static void Clear(string name)
    {
        try
        {
            File.Delete(FileFor(name));
        }
        catch (Exception ex)
        {
            Log.Error($"Settings: could not delete the saved {name} list", ex);
        }
    }
}
