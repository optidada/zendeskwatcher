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
    private readonly ToolStripMenuItem _checkNowItem = new("Check now");
    private readonly ToolStripMenuItem _openZendeskItem = new("Open Zendesk");

    /// <summary>Starts the next check now ("Check now", Catch up, after taking a ticket); at most one waits.</summary>
    private void WakeUp()
    {
        if (_wakeUp.CurrentCount == 0)
            _wakeUp.Release();
    }
    private readonly ToolStripMenuItem _catchUpItem = new("Catch up");

    /// <summary>Catch up was asked for: runs right after the next check (which is started at once).</summary>
    private bool _catchUpRequested;

    /// <summary>Done when the requested Catch up has run (or failed), for the spinner on the Account page.</summary>
    private TaskCompletionSource? _catchUpDone;

    private Settings _settings = Settings.Load();
    private BrowserSession? _browser;
    private CancellationTokenSource? _loopCts;
    private SettingsForm? _settingsForm;
    private (ZendeskClient Client, long MeId)? _taker; // set once the watcher knows who is signed in
    private User? _me; // who the watcher is signed in as (Settings shows it without checking again)
    private bool _signInPromptShown;
    /// <summary>The start summary is shown once per app run (not again when Settings restarts checking).</summary>
    private bool _summaryShown;

    public TrayAppContext()
    {
        Current = this;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _notifier = new Notifier(HandleAction) { Settings = _settings, StillNeeded = StillNeededAsync };
        _notifier.ClientChoiceRequested += SetClientChoice;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openZendeskItem);
        _openZendeskItem.Click += (_, _) => OpenUrl(_settings.IsConnectionConfigured ? $"{_settings.BaseUrl}/agent" : null);
        menu.Items.Add(_checkNowItem);
        _checkNowItem.Click += (_, _) => WakeUp();
        menu.Items.Add(_catchUpItem);
        _catchUpItem.Click += (_, _) => _ = RequestCatchUp();
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_updateItem);
        menu.Items.Add("Send feedback…", null, (_, _) => SendFeedback());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        // In the app theme's colors, taken each time it opens (the theme may have changed in Settings).
        menu.Opening += (_, _) =>
        {
            // "Catch up since yesterday 17:00": when you stopped working last time (your hours: Notifications page).
            var since = TicketRules.CatchUpStart(DateTime.Now, _settings.MyWorkdayStartHour, _settings.MyWorkdayEndHour, _settings.MyWorkDays);
            _catchUpItem.Text = "Catch up " + TicketRules.CatchUpSince(since, DateTime.Now);
            // Only what can work right now: checking needs a signed-in session, Zendesk needs an account.
            var checking = _loopCts is not null && _taker is not null;
            _checkNowItem.Enabled = _catchUpItem.Enabled = checking;
            _openZendeskItem.Enabled = _settings.IsConnectionConfigured;
            ThemedMenu.Apply(menu, UiPalette.For(_settings.AppTheme));
        };

        _tray = new TrayIcon(menu, _statusItem, _ui);
        _tray.LeftClick += (_, _) => ShowSettings();

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
        if (Settings.LoadProblem is { } problem)
            _ui.Post(_ => _notifier.ShowSettingsProblem(problem), null);
    }

    /// <summary>Called by the toast library on a background thread when a notification is clicked.</summary>
    public void OnToastActivated(ToastArguments args) => _ui.Post(_ =>
    {
        ChipSounds.Stop(); // acted on: the music can stop
        args.TryGetValue("action", out var action);
        long? ticketId = args.TryGetValue("ticketId", out var id) && long.TryParse(id, out var parsed) ? parsed : null;
        if (action == Notifier.ActionCopy)
        {
            // Counted by Copy. Unknown after a restart: copy at least the link.
            if (!(args.TryGetValue("copyId", out var copyId) && _notifier.CopyFromToast(copyId)) && ticketId is { } linkId)
                _notifier.Copy(new NotificationContent(NotificationKind.MyTicket, "", $"#{linkId}", TicketId: linkId));
            return;
        }
        if (action == Notifier.ActionMuteClient)
        {
            if (args.TryGetValue("clientId", out var clientId) && long.TryParse(clientId, out var orgId))
                _notifier.MuteFromToast(orgId, args.TryGetValue("clientName", out var name) ? name : $"Organization {orgId}",
                    args.TryGetValue("clientGroup", out var group) && long.TryParse(group, out var groupId) ? groupId : 0);
            return;
        }
        if (args.TryGetValue(Notifier.StatArgument, out var kindName) && Enum.TryParse<NotificationKind>(kindName, out var kind) &&
            NotificationStats.ForToastAction(action ?? "") is { } outcome)
            _notifier.CountToastClick(kind, outcome, args.TryGetValue(Notifier.StatClientArgument, out var client) ? client : null);
        HandleAction(action ?? "", ticketId);
    }, null);

    /// <summary>Zendesk Watcher was started again: say it's already running (from any thread).</summary>
    public void ShowAlreadyRunning() => _ui.Post(_ => _notifier.ShowAlreadyRunning(), null);

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
            case Notifier.ActionCatchUp:
                _ = RequestCatchUp();
                break;
        }
    }

    /// <summary>
    /// Catch up: checks now and then shows what still needs attention since you stopped working last time. Done when
    /// it has run (for the spinner on the Account page).
    /// </summary>
    private Task RequestCatchUp()
    {
        if (_loopCts is null || _taker is null)
        {
            _notifier.ShowInfo("Can't catch up yet", "Sign in first: click the Zendesk Watcher icon in the tray.");
            return Task.CompletedTask;
        }
        _catchUpDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _catchUpRequested = true;
        WakeUp();
        return _catchUpDone.Task;
    }

    // ------------------------------------------------------------------ checking

    /// <summary>(Re)starts watching with the current settings, or opens Settings if something is missing.</summary>
    private void Start(bool openSettingsIfNeeded)
    {
        _loopCts?.Cancel();
        _loopCts = null;

        if (!_settings.IsConnectionConfigured)
        {
            _tray.SetStatus("Not set up yet", TrayState.Off, iconHint: "click this icon to open Settings");
            WatchStatus.Stopped("Not checking: no Zendesk account set up yet (Account page).");
            if (openSettingsIfNeeded && Settings.IsFirstRun)
                _notifier.ShowWelcome();
            if (openSettingsIfNeeded)
                ShowSettings();
            return;
        }

        var session = CreateSession(_settings);
        if (!session.MightBeSignedIn)
        {
            _tray.SetStatus("Sign-in required", TrayState.Off, iconHint: "click this icon to sign in");
            WatchStatus.Stopped("Not checking: sign in on the Account page.");
            if (openSettingsIfNeeded && Settings.IsFirstRun)
                _notifier.ShowWelcome(); // the subdomain came pre-filled
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
        WatchStatus.Stopped(status + ".");
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
                    _me = watcher.Me;
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

                WatchStatus.Update(watcher.Snapshot());
                // Asked for, or by itself once when your working day starts (Notifications → Working hours).
                if (_catchUpRequested || watcher.DayStartCatchUpDue())
                {
                    var requested = _catchUpRequested;
                    _catchUpRequested = false;
                    try { await watcher.CatchUpAsync(ct, requestedByUser: requested); }
                    catch (Exception ex) when (ex is not (OperationCanceledException or AuthRequiredException))
                    {
                        Log.Error("Catch up failed", ex);
                        _notifier.ShowInfo("Catch up didn't work", ex.Message);
                    }
                    finally
                    {
                        if (requested)
                        {
                            _catchUpDone?.TrySetResult();
                            _catchUpDone = null;
                        }
                    }
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
                _tray.SetStatus("Sign-in required", TrayState.Off, iconHint: "click this icon to sign in");
                WatchStatus.Stopped("Not checking: your Zendesk session expired. Sign in on the Account page.");
                PromptSignIn();
                return; // Restarted after signing in via Settings.
            }
            catch (Exception ex)
            {
                Log.Error("Poll failed", ex);
                _tray.SetStatus($"Error, retrying: {ex.Message}", TrayState.Off);
                WatchStatus.Failed($"The last check failed and is retried: {ex.Message}");
            }

            try
            {
                // Cancel the losing wait: a wake-up wait left behind would swallow the next "Check now".
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), waiting.Token), _wakeUp.WaitAsync(waiting.Token));
                waiting.Cancel();
                ct.ThrowIfCancellationRequested();
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

    /// <summary>
    /// A choice from a notification's "Mute ▾" menu: saved right away for the client (Clients page). The checks share
    /// this settings object, so the next check already uses it. An open Settings window takes it over too.
    /// </summary>
    private void SetClientChoice(ClientChoice choice)
    {
        _settings.SetClientChoice(choice);
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings after a client choice", ex);
        }
        _settingsForm?.ClientChoiceSaved(choice);
        var (title, body) = TicketRules.ClientChoiceConfirmation(choice, DateTimeOffset.UtcNow);
        _notifier.ShowInfo(title, body);
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
        // Checking works right now: Settings shows who is signed in at once instead of asking Zendesk again.
        var signedInAs = !knownSignedOut && _loopCts is not null && _taker is not null && WatchStatus.Latest is not null &&
            WatchStatus.Problem is null ? _me : null;
        var form = new SettingsForm(_settings, CreateSessionForSettings, _notifier, knownSignedOut, signedInAs);
        _settingsForm = form;
        form.Saved += saved =>
        {
            _settings = saved;
            Start(openSettingsIfNeeded: false);
        };
        form.SessionChanging += () => PauseChecking("Paused while signing in or out");
        form.SessionChanged += () => Start(openSettingsIfNeeded: false);
        form.CheckForUpdatesClicked += _updates.OnClicked;
        form.CatchUp = RequestCatchUp;
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
            WakeUp(); // refresh the unassigned count
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

    /// <summary>For snoozed and long-waiting notifications (see <see cref="TicketWatcher.StillNeededAsync"/>). Not signed in yet: all still needed.</summary>
    private async Task<List<NotificationContent>> StillNeededAsync(IReadOnlyList<NotificationContent> waiting, bool deep)
    {
        if (_taker is not { } taker)
            return waiting.ToList();
        return await TicketWatcher.StillNeededAsync(taker.Client, taker.MeId, waiting, _settings.DropAnswered, deep, CancellationToken.None);
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
