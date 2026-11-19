using Velopack;
using Velopack.Sources;

namespace ZendeskWatcher;

/// <summary>
/// Updates from GitHub Releases via Velopack. Only works in the installed app (Setup.exe);
/// when running from bin\ during development, <see cref="IsInstalled"/> is false and nothing happens.
/// </summary>
public sealed class AppUpdater
{
    /// <summary>
    /// Public repo holding only the installers, update feed and release notes (since 1.9.4), so the source repo can be
    /// private. Versions up to 1.9.3 update from the source repo's releases.
    /// </summary>
    public const string RepoUrl = "https://github.com/optidada/zendeskwatcher-releases";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The downloaded update waiting for a restart, if any.</summary>
    public UpdateInfo? Pending { get; private set; }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "?";

    public string? PendingVersion => Pending?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// What the update brings, from the release notes of every version between this one and the pending one (the
    /// update's delta packages carry each version's notes): see <see cref="Highlights"/>.
    /// </summary>
    public string? PendingHighlights
    {
        get
        {
            if (Pending is not { } pending)
                return null;
            var releases = pending.DeltasToTarget.Select(d => (d.Version.ToString(), (string?)d.NotesMarkdown))
                .Append((pending.TargetFullRelease.Version.ToString(), pending.TargetFullRelease.NotesMarkdown));
            return Highlights(releases, CurrentVersion);
        }
    }

    /// <summary>At most this many lines in "Update ready".</summary>
    private const int MaxHighlightLines = 4;

    /// <summary>
    /// The versions newer than <paramref name="current"/>: one version, its first 3 points; several, the first point of
    /// each, newest first ("• 1.8.10: Catch up is easier to read"), then "… and N more versions" when they don't all fit.
    /// </summary>
    internal static string? Highlights(IEnumerable<(string Version, string? Notes)> releases, string current)
    {
        Version.TryParse(current, out var since);
        var versions = releases
            .Select(r => (Version: Version.TryParse(r.Version, out var v) ? v : null, Bullets: Bullets(r.Notes)))
            .Where(r => r.Version is not null && (since is null || r.Version > since) && r.Bullets.Count > 0)
            .DistinctBy(r => r.Version)
            .OrderByDescending(r => r.Version)
            .ToList();
        if (versions.Count == 0)
            return null;
        if (versions.Count == 1)
            return string.Join("\n", versions[0].Bullets.Take(3).Select(b => "• " + b));
        var lines = versions.Take(MaxHighlightLines).Select(r => $"• {r.Version!.ToString(3)}: {r.Bullets[0]}").ToList();
        if (versions.Count > MaxHighlightLines)
            lines[^1] = $"… and {versions.Count - MaxHighlightLines + 1} more versions";
        return string.Join("\n", lines);
    }

    private static List<string> Bullets(string? notes) => (notes ?? "").Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.StartsWith("- ") || l.StartsWith("* "))
        .Select(l => Headline(l[2..]))
        .ToList();

    /// <summary>"**Take it**: long explanation" → "Take it"; other bullets are shortened to fit a notification.</summary>
    internal static string Headline(string bullet)
    {
        if (bullet.StartsWith("**") && bullet.IndexOf("**", 2, StringComparison.Ordinal) is var end and > 2)
            return bullet[2..end];
        var text = bullet.Replace("**", "");
        return text.Length <= 60 ? text : text[..57].TrimEnd() + "…";
    }

    /// <summary>Checks GitHub and downloads a newer version. Returns true when an update is ready to install.</summary>
    public async Task<bool> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (!IsInstalled)
            return false;
        if (Pending is not null)
            return true;

        await _gate.WaitAsync(ct);
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
                return false;

            Log.Info($"Downloading update {update.TargetFullRelease.Version}");
            await _manager.DownloadUpdatesAsync(update, cancelToken: ct);
            Pending = update;
            Log.Info($"Update {update.TargetFullRelease.Version} downloaded");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// At app start, before anything shows: a newer version found and downloaded within <paramref name="limit"/> is
    /// installed right away (the app exits and the new version starts and says what's new). Any failure, a slow
    /// connection (no network yet right after logon) or a version that already failed to install once: carry on with
    /// this version; the usual check a couple of minutes later still offers it.
    /// </summary>
    /// <summary>The check at start answered "no newer version": the next scheduled check can wait a day.</summary>
    public static bool UpToDateAtStart { get; private set; }

    public void UpdateOnStart(TimeSpan limit)
    {
        if (!IsInstalled)
            return;
        try
        {
            var cts = new CancellationTokenSource(limit); // not disposed: a download that ran late may still read it
            var check = Task.Run(() => CheckAndDownloadAsync(cts.Token));
            if (!check.Wait(limit))
                return;
            if (!check.Result)
            {
                UpToDateAtStart = true;
                return;
            }
            if (PendingVersion is not { } version)
                return;
            var tried = Path.Combine(AppPaths.DataDir, "update-tried.txt");
            if (File.Exists(tried) && File.ReadAllText(tried).Trim() == version)
            {
                Log.Info($"Update {version} at start: it didn't install last time, so it waits for \"Restart to update\"");
                return;
            }
            File.WriteAllText(tried, version);
            Log.Info($"Installing update {version} at start");
            ApplyAndRestart();
        }
        catch (Exception ex)
        {
            Log.Error("Update at start failed", ex is AggregateException { InnerException: { } inner } ? inner : ex);
        }
    }

    /// <summary>Exits, installs the pending update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (Pending is { } update)
            _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
