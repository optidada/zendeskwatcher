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

    /// <summary>The saved list, if it's for this account and still fresh; null otherwise (or if it can't be read).</summary>
    public static (DateTime At, T Value)? Load<T>(string name, string subdomain) where T : class
    {
        try
        {
            var file = FileFor(name);
            if (!File.Exists(file))
                return null;
            var entry = JsonSerializer.Deserialize<Entry<T>>(File.ReadAllText(file), JsonOptions);
            if (entry?.Value is null || entry.Subdomain != subdomain || DateTime.UtcNow - entry.At > GroupsPage.CacheLifetime)
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
