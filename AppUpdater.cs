using Velopack;
using Velopack.Sources;

namespace ZendeskWatcher;

/// <summary>
/// Updates from GitHub Releases via Velopack. Only works in the installed app (Setup.exe);
/// when running from bin\ during development, <see cref="IsInstalled"/> is false and nothing happens.
/// </summary>
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/optidada/zendeskwatcher";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The downloaded update waiting for a restart, if any.</summary>
    public UpdateInfo? Pending { get; private set; }

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? typeof(AppUpdater).Assembly.GetName().Version?.ToString(3) ?? "?";

    public string? PendingVersion => Pending?.TargetFullRelease.Version.ToString();

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

    /// <summary>Exits, installs the pending update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (Pending is { } update)
            _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
