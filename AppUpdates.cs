namespace ZendeskWatcher;

/// <summary>
/// Update checks for the installed app: a couple of minutes after start, then daily, and on request. A downloaded
/// update is announced once and installed by "Restart to update". Runs on the UI thread.
/// </summary>
public sealed class AppUpdates : IDisposable
{
    private readonly AppUpdater _updater = new();
    private readonly Notifier _notifier;
    private readonly System.Windows.Forms.Timer _timer = new();
    private bool _checking;
    private string? _announcedVersion; // "Update ready" is shown once per version

    /// <summary>What the update button says ("Check for updates", "Checking for updates…", "Restart to update to …").</summary>
    public string Text { get; private set; } = "Check for updates";

    public bool Enabled { get; private set; } = true;

    /// <summary><see cref="Text"/> or <see cref="Enabled"/> changed.</summary>
    public event Action? StateChanged;

    /// <summary>Called right before the app exits to install an update (stop work, remove the tray icon).</summary>
    public Action BeforeRestart { get; set; } = () => { };

    public bool IsInstalled => _updater.IsInstalled;
    public string CurrentVersion => _updater.CurrentVersion;

    public AppUpdates(Notifier notifier)
    {
        _notifier = notifier;
        if (!_updater.IsInstalled)
            return;

        // Once a couple of minutes after start, then once a day ("Check for updates" works any time).
        _timer.Interval = 120_000;
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds;
            await CheckAsync(userInitiated: false);
        };
        _timer.Start();
    }

    /// <summary>The update button: installs a downloaded update, otherwise checks now.</summary>
    public async void OnClicked()
    {
        if (_updater.Pending is not null)
            RestartToUpdate();
        else
            await CheckAsync(userInitiated: true);
    }

    /// <summary>Checks GitHub and downloads a newer version.</summary>
    private async Task CheckAsync(bool userInitiated)
    {
        if (!_updater.IsInstalled)
        {
            if (userInitiated)
                _notifier.ShowInfo("Updates only work in the installed app", "This copy wasn't installed with Setup.exe, so it can't update itself.");
            return;
        }

        if (_checking)
            return; // the scheduled and a manual check can coincide; one is enough
        _checking = true;
        try
        {
            SetState("Checking for updates…", enabled: false);
            if (await _updater.CheckAndDownloadAsync())
            {
                SetState($"Restart to update to {_updater.PendingVersion}");
                _timer.Stop();
                if (_announcedVersion != _updater.PendingVersion)
                {
                    _announcedVersion = _updater.PendingVersion;
                    _notifier.ShowUpdateReady(_updater.PendingVersion!, _updater.PendingHighlights);
                }
            }
            else
            {
                SetState("Check for updates");
                if (userInitiated)
                    _notifier.ShowInfo("No updates", $"You have the latest version ({_updater.CurrentVersion}).");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            SetState("Check for updates");
            if (userInitiated)
                _notifier.ShowInfo("Couldn't check for updates", ex.Message);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Installs the downloaded update and restarts the app (the process exits right away).</summary>
    public void RestartToUpdate()
    {
        if (_updater.Pending is null)
            return;
        Log.Info($"Restarting to update to {_updater.PendingVersion}");
        BeforeRestart();
        _updater.ApplyAndRestart();
    }

    private void SetState(string text, bool enabled = true)
    {
        Text = text;
        Enabled = enabled;
        StateChanged?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
