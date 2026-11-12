using System.Diagnostics;
using Microsoft.Toolkit.Uwp.Notifications;
using ZendeskWatcher.Auth;
using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher;

/// <summary>
/// The app while it runs: the tray menu, the checking loop, the Settings window and what notification clicks do.
/// The tray icon (<see cref="TrayIcon"/>), updates (<see cref="AppUpdates"/>) and feedback (<see cref="FeedbackMail"/>)
/// are separate classes.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    public static TrayAppContext? Current { get; private set; }

    private readonly SynchronizationContext _ui;
    private readonly Notifier _notifier;
    private readonly TrayIcon _tray;
    private readonly AppUpdates _updates;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new("Pause notifications") { CheckOnClick = true };
    private readonly ToolStripMenuItem _updateItem = new("Check for updates");
    private readonly SemaphoreSlim _wakeUp = new(0);

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

        _tray = new TrayIcon(menu, _statusItem, _ui);
        _tray.DoubleClick += (_, _) => ShowSettings();

        _pauseItem.CheckedChanged += (_, _) =>
        {
            _notifier.Paused = _pauseItem.Checked;
            _tray.SetStatus(_pauseItem.Checked ? "Notifications paused" : "Notifications resumed");
        };

        _updates = new AppUpdates(_notifier)
        {
            BeforeRestart = () =>
            {
                _loopCts?.Cancel();
                _tray.Hide(); // the process exits right away; don't leave a ghost icon behind
                _browser?.Dispose();
            }
        };
        _updateItem.Click += (_, _) => _updates.OnClicked();
        _updates.StateChanged += () =>
        {
            _updateItem.Text = _updates.Text;
            _updateItem.Enabled = _updates.Enabled;
            _settingsForm?.ShowUpdateState(_updates.Text, _updates.Enabled);
        };

        // Keep "Start with Windows" pointing at the installed exe (e.g. after moving from a hand-copied exe).
        if (_updates.IsInstalled && StartupRegistration.IsEnabled)
            StartupRegistration.Set(true);

        // Run once the message loop is up; this may open the Settings window on first start.
        _ui = SynchronizationContext.Current ?? _ui;
        _ui.Post(_ => Start(openSettingsIfNeeded: true), null);
    }

    /// <summary>Called by the toast library on a background thread when a notification is clicked.</summary>
    public void OnToastActivated(ToastArguments args) => _ui.Post(_ =>
    {
        args.TryGetValue("action", out var action);
        long? ticketId = args.TryGetValue("ticketId", out var id) && long.TryParse(id, out var parsed) ? parsed : null;
        if (args.TryGetValue(Notifier.StatArgument, out var kindName) && Enum.TryParse<NotificationKind>(kindName, out var kind) &&
            NotificationStats.ForToastAction(action ?? "") is { } outcome)
            _notifier.Stats.Record(kind, outcome);
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
                _updates.RestartToUpdate();
                break;
        }
    }

    // ------------------------------------------------------------------ checking

    /// <summary>(Re)starts watching with the current settings, or opens Settings if something is missing.</summary>
    private void Start(bool openSettingsIfNeeded)
    {
        _loopCts?.Cancel();
        _loopCts = null;

        if (!_settings.IsConnectionConfigured)
        {
            _tray.SetStatus("Not configured – open Settings", TrayState.Off);
            if (openSettingsIfNeeded && Settings.IsFirstRun)
                _notifier.ShowWelcome();
            if (openSettingsIfNeeded)
                ShowSettings();
            return;
        }

        var session = CreateSession(_settings);
        if (!session.MightBeSignedIn)
        {
            _tray.SetStatus("Sign-in required – open Settings", TrayState.Off);
            if (openSettingsIfNeeded)
                ShowSettings();
            return;
        }

        _notifier.Settings = _settings;
        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(session, _loopCts.Token);
    }

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
        _tray.SetStatus(status);
    }

    /// <summary>One shared browser session (one embedded browser profile), pointed at the given account.</summary>
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
                    _tray.SetStatus("Connecting…");
                    _tray.BeginBusy();
                    try { await watcher.InitializeAsync(ct); }
                    finally { _tray.EndBusy(); }
                    _taker = (new ZendeskClient(_settings, session), watcher.Me!.Id);
                    initialized = true;
                    // Signed in: show it right away; the first check (a few seconds) then sets green/orange.
                    _tray.SetStatus("Signed in · checking…", _tray.State == TrayState.Off ? TrayState.Ok : null);
                }

                _tray.BeginBusy();
                try { await watcher.PollAsync(ct); }
                finally { _tray.EndBusy(); }
                _signInPromptShown = false; // signed in again: a later failure may prompt again

                if (!_summaryShown)
                {
                    _summaryShown = true;
                    if (_settings.ShowStartSummary && !watcher.ShowedAlreadyWaiting)
                        _notifier.ShowSummary(watcher.StartSummary());
                }

                var summary = $"{watcher.UnassignedCount} unassigned · {watcher.MyOpenCount} mine · {DateTime.Now:HH:mm}";
                _tray.SetStatus(_notifier.Paused ? $"Paused · {summary}" : summary,
                    watcher.UnassignedCount > 0 ? TrayState.Attention : TrayState.Ok);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (AuthRequiredException)
            {
                Log.Info("Sign-in required");
                _tray.SetStatus("Sign-in required – double-click to sign in", TrayState.Off);
                PromptSignIn();
                return; // Restarted after signing in via Settings.
            }
            catch (Exception ex)
            {
                Log.Error("Poll failed", ex);
                _tray.SetStatus($"Error, retrying: {ex.Message}", TrayState.Off);
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

    // ------------------------------------------------------------------ settings window

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
        form.CheckForUpdatesClicked += _updates.OnClicked;
        form.SendFeedbackClicked += SendFeedback;
        form.OpenLogFolderClicked += () => OpenUrl(AppPaths.DataDir);
        form.ShowUpdateState(_updates.Text, _updates.Enabled);
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

    // ------------------------------------------------------------------ ticket actions

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

    /// <summary>For snoozed popups (see <see cref="TicketRules.StillNeedsAttention"/>). Not signed in yet: assume it's still needed.</summary>
    private async Task<bool> StillNeededAsync(NotificationContent content)
    {
        if (_taker is not { } taker || content.TicketId is not { } id)
            return true;
        var ticket = (await taker.Client.GetTicketsAsync([id], CancellationToken.None)).FirstOrDefault();
        return TicketRules.StillNeedsAttention(content.Kind, ticket);
    }

    private void SendFeedback() => FeedbackMail.Send(_updates.CurrentVersion, _notifier);

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
        _updates.Dispose();
        _tray.Dispose();
        _browser?.Dispose();
        base.ExitThreadCore();
    }
}
