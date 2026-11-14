using System.Reflection;

namespace ZendeskWatcher;

/// <summary>
/// After an update (by the app or with the installer): "What's new" once, with the highlights of every version since
/// the last one that ran, from CHANGELOG.md (which comes with the app). The last version is kept in the data folder.
/// </summary>
internal static class WhatsNew
{
    /// <summary>At most this many lines, so a big jump (1.5 → 1.8) doesn't fill the screen.</summary>
    private const int MaxLines = 6;

    private static string LastVersionFile => Path.Combine(AppPaths.DataDir, "last-version.txt");

    /// <summary>
    /// The version this one replaces, if it's newer than the last that ran (then there's news); remembers this one
    /// either way. Null on a first install (the welcome says hello instead) or when nothing changed.
    /// </summary>
    public static string? UpdatedFrom(string current)
    {
        string? last = null;
        try
        {
            if (File.Exists(LastVersionFile))
                last = File.ReadAllText(LastVersionFile).Trim();
            File.WriteAllText(LastVersionFile, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Could not remember the app version", ex);
            return null;
        }
        return last is not null && Version.TryParse(last, out var before) && Version.TryParse(current, out var now) && now > before
            ? last : null;
    }

    /// <summary>The heading and the lines for an update from <paramref name="from"/> to <paramref name="to"/>; null if the changelog has nothing.</summary>
    public static (string Title, string Body)? Summary(string from, string to) => Summary(Changelog(), from, to);

    /// <summary>
    /// One version: its first highlights. Several: the first one or two of each, newest first, then "… and more" when it
    /// doesn't all fit.
    /// </summary>
    internal static (string Title, string Body)? Summary(string? changelog, string from, string to)
    {
        if (changelog is null || !Version.TryParse(from, out var since) || !Version.TryParse(to, out var until))
            return null;
        var versions = Sections(changelog).Where(s => s.Version > since && s.Version <= until).OrderByDescending(s => s.Version).ToList();
        if (versions.Count == 0)
            return null;

        var perVersion = versions.Count == 1 ? MaxLines - 1 : versions.Count <= 3 ? 2 : 1;
        var lines = new List<string>();
        var more = false;
        foreach (var (version, bullets) in versions)
        {
            var take = bullets.Take(perVersion).Select(b => versions.Count == 1 ? $"• {b}" : $"• {version.ToString(3)}: {b}").ToList();
            if (lines.Count + take.Count > MaxLines - 1)
            {
                more = true;
                break;
            }
            lines.AddRange(take);
            more |= bullets.Count > take.Count;
        }
        if (more)
            lines.Add("… and more: click for all release notes");
        var title = versions.Count == 1 ? $"What's new in {to}" : $"What's new since {from}";
        return (title, string.Join("\n", lines));
    }

    /// <summary>Each "## 1.7.0" section of the changelog and its bullet points as short headlines.</summary>
    private static List<(Version Version, List<string> Bullets)> Sections(string changelog)
    {
        var sections = new List<(Version, List<string>)>();
        List<string>? current = null;
        foreach (var raw in changelog.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## "))
            {
                current = Version.TryParse(line[3..].Trim(), out var v) ? [] : null;
                if (current is not null)
                    sections.Add((v!, current));
            }
            else if (current is not null && line.StartsWith("- "))
                current.Add(AppUpdater.Headline(line[2..]));
        }
        return sections;
    }

    /// <summary>All release notes on GitHub (one version: that one's page).</summary>
    public static string ReleaseNotesUrl(string? version = null) =>
        version is null ? $"{AppUpdater.RepoUrl}/releases" : $"{AppUpdater.RepoUrl}/releases/tag/v{version}";

    private static string? Changelog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
