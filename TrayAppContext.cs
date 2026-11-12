using System.Diagnostics;
using Microsoft.Toolkit.Uwp.Notifications;
using ZendeskWatcher.Auth;
using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher;

/// <summary>The tray icon, its menu and the polling loop.</summary>
public sealed class TrayAppContext : ApplicationContext
{
    public static TrayAppContext? Current { get; private set; }

    private readonly Notifier _notifier;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new("Pause notifications") { CheckOnClick = true };
    private readonly SemaphoreSlim _wakeUp = new(0);
    private readonly AppUpdater _updater = new();
    private readonly ToolStripMenuItem _updateItem = new("Check for updates");
    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private bool _checkingForUpdates;
    private string? _announcedVersion; // "Update ready" is shown once per version

    private readonly Icon _iconOk = AppIcon.CreateIcon(AppIcon.NeonGreen);
    private readonly Icon _iconAttention = AppIcon.CreateIcon(AppIcon.NeonOrange);
    private readonly Icon _iconOff = AppIcon.CreateIcon(AppIcon.Dim);

    // "Working" pulse: shown while a check runs (after a short delay, so quick checks don't flicker).
    private readonly Dictionary<Icon, Icon[]> _pulseFrames = [];
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 90 };
    private readonly System.Windows.Forms.Timer _pulseDelay = new() { Interval = 600 };
    private Icon? _stateIcon;   // the icon for the current state; the pulse animates this one
    private int _pulseFrame;
    private int _busy;

    private Settings _settings = Settings.Load();
    private BrowserSession? _browser;
    private CancellationTokenSource? _loopCts;
    private SettingsForm? _settingsForm;
    private (ZendeskClient Client, long MeId)? _taker; // set once the watcher knows who is signed in
    private bool _signInPromptShown;
    /// <summary>The start summary is shown once per app run (not again when Settings restarts checking).</summary>
    private bool _summaryShown;

    public TrayAppContext()
    {
        Current = this;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _notifier = new Notifier(HandleAction) { Settings = _settings, StillNeeded = StillNeededAsync };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Zendesk", null, (_, _) => OpenUrl(_settings.IsConnectionConfigured ? $"{_settings.BaseUrl}/agent" : null));
        menu.Items.Add("Check now", null, (_, _) => _wakeUp.Release());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Open log folder", null, (_, _) => OpenUrl(AppPaths.DataDir));
        menu.Items.Add(_updateItem);
        menu.Items.Add("Send feedback…", null, (_, _) => SendFeedback());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _pauseItem.CheckedChanged += (_, _) =>
        {
            _notifier.Paused = _pauseItem.Checked;
            SetStatus(_pauseItem.Checked ? "Notifications paused" : "Notifications resumed");
        };

        _tray = new NotifyIcon { Icon = _iconOff, Text = "Zendesk Watcher", ContextMenuStrip = menu, Visible = true };
        _stateIcon = _iconOff;
        _pulseDelay.Tick += (_, _) => { _pulseDelay.Stop(); _pulseTimer.Start(); };
        _pulseTimer.Tick += (_, _) => PulseTick();
        _tray.DoubleClick += (_, _) => ShowSettings();

        _updateItem.Click += (_, _) => OnUpdateMenuClicked();
        if (_updater.IsInstalled)
        {
            // Keep "Start with Windows" pointing at the installed exe (e.g. after moving from a hand-copied exe).
            if (StartupRegistration.IsEnabled)
                StartupRegistration.Set(true);

            // Once a couple of minutes after start, then once a day ("Check for updates" works any time).
            _updateTimer.Interval = 120_000;
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = (int)TimeSpan.FromHours(24).TotalMilliseconds;
                await CheckForUpdatesAsync(userInitiated: false);
            };
            _updateTimer.Start();
        }

        // Run once the message loop is up; this may open the Settings window on first start.
        _ui = SynchronizationContext.Current ?? _ui;
        _ui.Post(_ => Start(openSettingsIfNeeded: true), null);
    }

    /// <summary>Called by the toast library on a background thread when a notification is clicked.</summary>
    public void OnToastActivated(ToastArguments args) => _ui.Post(_ =>
    {
        args.TryGetValue("action", out var action);
        long? ticketId = args.TryGetValue("ticketId", out var id) && long.TryParse(id, out var parsed) ? parsed : null;
        HandleAction(action ?? "", ticketId);
    }, null);

    /// <summary>Shows sample notifications with the saved settings once the app is running.</summary>
    public void ShowTestPopups() => _ui.Post(_ => _notifier.ShowTestBurst(_settings), null);

    /// <summary>What a click on a notification (popup or Windows toast) does. Runs on the UI thread.</summary>
    private void HandleAction(string action, long? ticketId)
    {
        switch (action)
        {
            case Notifier.ActionOpenTicket when ticketId is { } id:
                OpenUrl(_settings.TicketUrl(id));
                break;
            case Notifier.ActionOpenZendesk:
                OpenUrl($"{_settings.BaseUrl}/agent");
                break;
            case Notifier.ActionSignIn:
            case Notifier.ActionSettings:
                ShowSettings();
                break;
            case Notifier.ActionTake when ticketId is { } takeId:
                _ = TakeTicketAsync(takeId);
                break;
            case Notifier.ActionUpdate:
                RestartToUpdate();
                break;
        }
    }

    /// <summary>(Re)starts watching with the current settings, or opens Settings if something is missing.</summary>
    private void Start(bool openSettingsIfNeeded)
    {
        _loopCts?.Cancel();
        _loopCts = null;

        if (!_settings.IsConnectionConfigured)
        {
            SetStatus("Not configured – open Settings", _iconOff);
            if (openSettingsIfNeeded && Settings.IsFirstRun)
                _notifier.ShowWelcome();
            if (openSettingsIfNeeded)
                ShowSettings();
            return;
        }

        var session = CreateSession(_settings);
        if (!session.MightBeSignedIn)
        {
            SetStatus("Sign-in required – open Settings", _iconOff);
            if (openSettingsIfNeeded)
                ShowSettings();
            return;
        }

        _notifier.Settings = _settings;
        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(session, _loopCts.Token);
    }

    /// <summary>One shared browser session (one embedded browser profile), pointed at the configured account.</summary>
    /// <summary>
    /// The session for the Settings window. Looking at a different account (a new subdomain, not saved yet) points the
    /// shared hidden browser elsewhere, so checking pauses until it's saved or the window is closed.
    /// </summary>
    private IZendeskSession CreateSessionForSettings(Settings settings)
    {
        if (settings.Subdomain != _settings.Subdomain && _loopCts is not null)
            PauseChecking("Paused while Settings looks at another Zendesk account");
        return CreateSession(settings);
    }

    private void PauseChecking(string status)
    {
        _loopCts?.Cancel();
        _loopCts = null;
        SetStatus(status);
    }

    private IZendeskSession CreateSession(Settings settings)
    {
        _browser ??= new BrowserSession();
        _browser.Configure(settings.BaseUrl);
        return _browser;
    }

    private async Task RunLoopAsync(IZendeskSession session, CancellationToken ct)
    {
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, session), _notifier);
        var initialized = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    SetStatus("Connecting…");
                    BeginBusy();
                    try { await watcher.InitializeAsync(ct); }
                    finally { EndBusy(); }
                    _taker = (new ZendeskClient(_settings, session), watcher.Me!.Id);
                    initialized = true;
                    // Signed in: show it right away; the first check (a few seconds) then sets green/orange.
                    SetStatus("Signed in · checking…", _tray.Icon == _iconOff ? _iconOk : null);
                }

                BeginBusy();
                try { await watcher.PollAsync(ct); }
                finally { EndBusy(); }
                _signInPromptShown = false; // signed in again: a later failure may prompt again

                if (!_summaryShown)
                {
                    _summaryShown = true;
                    if (_settings.ShowStartSummary && !watcher.ShowedAlreadyWaiting)
                        _notifier.ShowSummary(watcher.StartSummary());
                }

                var summary = $"{watcher.UnassignedCount} unassigned · {watcher.MyOpenCount} mine · {DateTime.Now:HH:mm}";
                SetStatus(_notifier.Paused ? $"Paused · {summary}" : summary, watcher.UnassignedCount > 0 ? _iconAttention : _iconOk);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (AuthRequiredException)
            {
                Log.Info("Sign-in required");
                SetStatus("Sign-in required – double-click to sign in", _iconOff);
                PromptSignIn();
                return; // Restarted after signing in via Settings.
            }
            catch (Exception ex)
            {
                Log.Error("Poll failed", ex);
                SetStatus($"Error, retrying: {ex.Message}", _iconOff);
            }

            try
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), ct), _wakeUp.WaitAsync(ct));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The session failed and could not be renewed silently: open the Account page so the user can sign in again.
    /// Only once per failure (closing the window does not make it reopen), and never over a presentation.
    /// </summary>
    private void PromptSignIn()
    {
        if (_signInPromptShown)
            return;
        _signInPromptShown = true;

        if (PopupManager.CanShowPopups())
        {
            _ui.Post(_ => ShowSettings(knownSignedOut: true), null);
            return;
        }

        // Presenting or full screen: notify quietly, open the Account page when that is over.
        _notifier.ShowSignInRequired();
        var wait = new System.Windows.Forms.Timer { Interval = 30_000 };
        wait.Tick += (_, _) =>
        {
            if (!PopupManager.CanShowPopups())
                return;
            wait.Dispose();
            if (_signInPromptShown && _settingsForm is null) // still signed out, window not open
                ShowSettings(knownSignedOut: true);
        };
        wait.Start();
    }

    /// <param name="knownSignedOut">The session was just found to be inactive; the window skips its own check.</param>
    private void ShowSettings(bool knownSignedOut = false)
    {
        if (_settingsForm is not null)
        {
            _settingsForm.Activate();
            return;
        }

        // A normal (not modal) window, so notifications stay clickable while it's open. Checking keeps running with
        // the saved settings; Save applies changes right away. It only pauses while signing in or out, or while the
        // window looks at a different Zendesk account (the hidden browser is shared).
        var form = new SettingsForm(_settings, CreateSessionForSettings, _notifier, knownSignedOut);
        _settingsForm = form;
        form.Saved += saved =>
        {
            _settings = saved;
            Start(openSettingsIfNeeded: false);
        };
        form.SessionChanging += () => PauseChecking("Paused while signing in or out");
        form.SessionChanged += () => Start(openSettingsIfNeeded: false);
        form.CheckForUpdatesClicked += OnUpdateMenuClicked;
        form.SendFeedbackClicked += SendFeedback;
        form.OpenLogFolderClicked += () => OpenUrl(AppPaths.DataDir);
        form.ShowUpdateState(_updateItem.Text ?? "Check for updates", _updateItem.Enabled);
        form.FormClosed += (_, _) =>
        {
            _settingsForm = null;
            _settings = form.Result; // the last saved settings
            MemoryTrim.Soon();       // the window and its controls are gone (closed forms dispose themselves)
            if (_loopCts is null)
                Start(openSettingsIfNeeded: false); // was paused (or never started): resume
        };
        form.Show();
        form.Activate();
    }

    /// <summary>"Take it": assigns the ticket to me (unless someone was faster), then opens it. Runs on the UI thread.</summary>
    private async Task TakeTicketAsync(long ticketId)
    {
        if (_taker is not { } taker)
        {
            OpenUrl(_settings.TicketUrl(ticketId)); // not connected yet: just open it
            return;
        }

        try
        {
            var result = await taker.Client.TakeTicketAsync(ticketId, taker.MeId, CancellationToken.None);
            Log.Info(result.Taken ? $"Took ticket #{ticketId}" : $"Ticket #{ticketId} was already taken by {result.TakenBy}");
            if (!result.Taken)
                _notifier.ShowInfo($"#{ticketId} was already taken", $"{result.TakenBy} got it first. Opening the ticket.", ticketId);
            _wakeUp.Release(); // refresh the unassigned count
        }
        catch (AuthRequiredException)
        {
            _notifier.ShowSignInRequired();
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not take ticket #{ticketId}", ex);
            _notifier.ShowInfo($"Could not take #{ticketId}", $"{ex.Message}\nOpening the ticket instead.", ticketId);
        }

        OpenUrl(_settings.TicketUrl(ticketId));
    }

    private async void OnUpdateMenuClicked()
    {
        if (_updater.Pending is not null)
            RestartToUpdate();
        else
            await CheckForUpdatesAsync(userInitiated: true);
    }

    /// <summary>Checks GitHub and downloads a newer version. Runs on the UI thread.</summary>
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (!_updater.IsInstalled)
        {
            if (userInitiated)
                _notifier.ShowInfo("Updates only work in the installed app", "This copy wasn't installed with Setup.exe, so it can't update itself.");
            return;
        }

        if (_checkingForUpdates)
            return; // the scheduled and a manual check can coincide; one is enough
        _checkingForUpdates = true;
        try
        {
            SetUpdateState("Checking for updates…", enabled: false);
            if (await _updater.CheckAndDownloadAsync())
            {
                SetUpdateState($"Restart to update to {_updater.PendingVersion}");
                _updateTimer.Stop();
                if (_announcedVersion != _updater.PendingVersion)
                {
                    _announcedVersion = _updater.PendingVersion;
                    _notifier.ShowUpdateReady(_updater.PendingVersion!, _updater.PendingHighlights);
                }
            }
            else
            {
                SetUpdateState("Check for updates");
                if (userInitiated)
                    _notifier.ShowInfo("No updates", $"You have the latest version ({_updater.CurrentVersion}).");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
            SetUpdateState("Check for updates");
            if (userInitiated)
                _notifier.ShowInfo("Couldn't check for updates", ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
            SetUpdateState(_updateItem.Text ?? "Check for updates");
        }
    }

    /// <summary>The update item's text and state, in the tray menu and on the Settings General page.</summary>
    private void SetUpdateState(string text, bool enabled = true)
    {
        _updateItem.Text = text;
        _updateItem.Enabled = enabled;
        _settingsForm?.ShowUpdateState(text, enabled);
    }

    private void RestartToUpdate()
    {
        if (_updater.Pending is null)
            return;
        Log.Info($"Restarting to update to {_updater.PendingVersion}");
        _loopCts?.Cancel();
        _tray.Visible = false; // the process exits right away; don't leave a ghost icon behind
        _browser?.Dispose();
        _updater.ApplyAndRestart();
    }

    private void SetStatus(string text, Icon? icon = null) => _ui.Post(_ =>
    {
        _statusItem.Text = text;
        var tooltip = $"Zendesk Watcher\n{text}";
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (icon is not null)
        {
            _stateIcon = icon;
            if (!_pulseTimer.Enabled)
                _tray.Icon = icon;
        }
    }, null);

    /// <summary>Work started (a check against Zendesk): pulse the tray icon if it takes more than a moment.</summary>
    private void BeginBusy()
    {
        if (_busy++ == 0)
            _pulseDelay.Start();
    }

    private void EndBusy()
    {
        if (_busy == 0 || --_busy > 0)
            return;
        _pulseDelay.Stop();
        _pulseTimer.Stop();
        if (_stateIcon is not null)
            _tray.Icon = _stateIcon;
    }

    private void PulseTick()
    {
        var baseIcon = _stateIcon ?? _iconOff;
        if (!_pulseFrames.TryGetValue(baseIcon, out var frames))
        {
            var color = baseIcon == _iconAttention ? AppIcon.NeonOrange : baseIcon == _iconOff ? AppIcon.Dim : AppIcon.NeonGreen;
            _pulseFrames[baseIcon] = frames = AppIcon.CreatePulseFrames(color);
        }
        _pulseFrame = (_pulseFrame + 1) % frames.Length;
        _tray.Icon = frames[_pulseFrame];
    }

    /// <summary>
    /// For snoozed popups: a new unassigned ticket must still be unassigned and open; any other ticket must not be
    /// solved or closed. Not signed in yet: assume it's still needed.
    /// </summary>
    private async Task<bool> StillNeededAsync(NotificationContent content)
    {
        if (_taker is not { } taker || content.TicketId is not { } id)
            return true;
        var ticket = (await taker.Client.GetTicketsAsync([id], CancellationToken.None)).FirstOrDefault();
        if (ticket is null)
            return false; // deleted, or no longer visible to you
        return content.Kind == NotificationKind.NewTicket
            ? ticket.IsOpen && ticket.AssigneeId is null
            : ticket.IsOpen;
    }

    // ------------------------------------------------------------------ feedback

    /// <summary>
    /// Opens a pre-filled feedback email, picking the app automatically: classic Outlook if it's installed (most
    /// colleagues use it, while mailto may go to e.g. Gmail), else the default mail app. If neither opens, the
    /// email is copied to the clipboard. A new email can't carry attachments, so the text points at the log folder.
    /// </summary>
    private void SendFeedback()
    {
        if (ClassicOutlookPath() is { } outlook)
        {
            try
            {
                Process.Start(outlook, ["/c", "ipm.note", "/m", $"{AppUpdater.FeedbackEmail}?{FeedbackQuery()}"]);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Could not open Outlook for feedback; trying the default mail app", ex);
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{AppUpdater.FeedbackEmail}?{FeedbackQuery()}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open a mail app for feedback", ex);
            Clipboard.SetText($"To: {AppUpdater.FeedbackEmail}\r\nSubject: {FeedbackSubject}\r\n\r\n{FeedbackBody}");
            _notifier.ShowInfo("No mail app found", $"The feedback email is copied to the clipboard. Paste it into an email to {AppUpdater.FeedbackEmail}.");
        }
    }

    private string FeedbackSubject => $"Zendesk Watcher feedback ({_updater.CurrentVersion})";

    private string FeedbackBody => string.Join("\r\n",
        "What happened, or what would you like?",
        "",
        "",
        "",
        "---",
        $"Version: {_updater.CurrentVersion}",
        $"Windows: {Environment.OSVersion.Version}",
        $"Log folder: {AppPaths.DataDir}",
        "(If something went wrong, please attach the latest log from that folder.)");

    private string FeedbackQuery() =>
        $"subject={Uri.EscapeDataString(FeedbackSubject)}&body={Uri.EscapeDataString(FeedbackBody)}";

    /// <summary>Classic desktop Outlook (OUTLOOK.EXE), which can open a pre-filled email; null if not installed.</summary>
    private static string? ClassicOutlookPath()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE");
        return key?.GetValue(null) is string path && File.Exists(path) ? path : null;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {url}", ex);
        }
    }

    protected override void ExitThreadCore()
    {
        _loopCts?.Cancel();
        _updateTimer.Dispose();
        _pulseTimer.Dispose();
        _pulseDelay.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _browser?.Dispose();
        base.ExitThreadCore();
    }
}
