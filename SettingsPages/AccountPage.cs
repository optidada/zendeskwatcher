using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Account: the Zendesk subdomain, the session status, sign in and out. Loads the groups once signed in.</summary>
internal sealed class AccountPage : SettingsPage
{
    private enum AccountState { SignedOut, Checking, SignedIn, Error }

    private readonly InputBox _subdomain = new(220, "e.g. yourcompany");
    private readonly StatusDot _dot = new();
    private readonly Label _status = Theme.Label("Not signed in", maxWidth: Kit.CardWidth - 70);
    private readonly Button _signIn = Theme.PrimaryButton("Sign in");
    private readonly Button _signOut = Theme.SecondaryButton("Sign out");
    private readonly Button _catchUp = Theme.SecondaryButton("Catch up");
    private Card _catchUpCard = null!;

    /// <summary>Starts Catch up in the app (set by the app); done when it has run.</summary>
    public Func<Task>? CatchUp { get; set; }
    private readonly GroupsPage _groups;

    private bool _signingIn;
    private string? _loadedFor;

    /// <summary>A sign-in or sign-out is about to start (the app pauses checking).</summary>
    public event Action? SessionChanging;

    /// <summary>The sign-in or sign-out has finished.</summary>
    public event Action? SessionChanged;

    /// <summary>Signed in successfully: the window saves the subdomain right away (before <see cref="SessionChanged"/>).</summary>
    public event Action? SignedIn;

    public AccountPage(SettingsContext context, GroupsPage groups)
        : base(context, "Account", "Connect to Zendesk with your own agent account.")
    {
        _groups = groups;

        var account = new Card("Zendesk account",
            "The address you use to open Zendesk. You sign in as usual (Okta) in a built-in browser window; " +
            "the app then reads tickets with your own agent permissions.", Kit.CardWidth);
        account.Body.Controls.Add(Kit.Row(_subdomain, Theme.Label(".zendesk.com", color: Theme.TextMuted)));
        Panel.Controls.Add(account);

        var status = new Card("Status", null, Kit.CardWidth);
        status.Body.Controls.Add(Kit.Row(_dot, _status));
        var buttons = Kit.Row(_signIn, _signOut);
        buttons.Margin = new Padding(0, 12, 0, 0);
        status.Body.Controls.Add(buttons);
        Panel.Controls.Add(status);

        // Its own card, shown once signed in (it needs the app to be checking with this account).
        _catchUpCard = new Card("Catch up",
            "What still needs you since you stopped working last time: unassigned tickets in your groups and customers " +
            "waiting for an answer. Also in the tray menu, and by itself when your working day starts.", Kit.CardWidth);
        _catchUpCard.Body.Controls.Add(_catchUp);
        _catchUpCard.Visible = false;
        Panel.Controls.Add(_catchUpCard);

        _subdomain.Leave += async (_, _) => await RefreshAsync();
        _signIn.Click += async (_, _) => await SignInAsync();
        _signOut.Click += (_, _) => SignOut();
        _catchUp.Click += async (_, _) => await CatchUpAsync();
        _groups.ReloadRequested += async () => await RefreshAsync(force: true);
        TrackChanges(_subdomain);
    }

    /// <summary>The subdomain as typed (normalized), saved or not.</summary>
    public string Subdomain => Settings.NormalizeSubdomain(_subdomain.Text);

    public override void Load(Settings settings)
    {
        _subdomain.Text = settings.Subdomain;
        SetStatus("Not signed in", AccountState.SignedOut);
    }

    public override void Apply(Settings settings) => settings.Subdomain = Subdomain;

    /// <summary>The window was shown: check the session (or, when the app just found none, say so right away).</summary>
    public async Task OnWindowShownAsync(bool knownSignedOut)
    {
        if (knownSignedOut && Context.Current() is { IsConnectionConfigured: true } connection)
        {
            // The app just found no active session; don't make the user wait for another check.
            _loadedFor = connection.Subdomain;
            SetStatus($"Not signed in to {connection.Subdomain}.zendesk.com. Click Sign in.", AccountState.SignedOut);
            _signIn.Focus();
            return;
        }

        // Windows focuses the first field and selects its text. Only put the cursor there when it's empty
        // (first start); otherwise focus nothing, so the saved subdomain isn't highlighted.
        if (_subdomain.Text.Length == 0)
            _subdomain.Box.Focus();
        else
            Context.Window.ActiveControl = null;

        await RefreshAsync();
    }

    /// <summary>Checks the session for the subdomain and loads the groups. Skipped if nothing changed, unless forced.</summary>
    private async Task RefreshAsync(bool force = false)
    {
        if (_signingIn)
            return;

        var connection = Context.Current();
        var key = connection.Subdomain;
        if (!force && key == _loadedFor)
            return;

        _loadedFor = key;
        _groups.SetReloadEnabled(false);

        if (!connection.IsConnectionConfigured)
        {
            SetStatus("Enter your Zendesk subdomain, then sign in.", AccountState.SignedOut);
            return;
        }

        SetStatus("Checking your Zendesk session…", AccountState.Checking);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var client = new ZendeskClient(connection, Context.SessionFactory(connection));
            var me = await client.GetMeAsync(CancellationToken.None);
            if (key != _loadedFor)
                return; // the subdomain changed meanwhile

            SetStatus($"Signed in to {connection.Subdomain}.zendesk.com as {me.Name} ({me.Email}) · {me.Role}", AccountState.SignedIn);
            Context.SetSession(client, me, reload: force);
            Log.Info($"Settings: session {clock.ElapsedMilliseconds} ms");

            await _groups.LoadAsync(client, me.Id, key, force, stale: () => key != _loadedFor);
        }
        catch (AuthRequiredException)
        {
            SetStatus($"Not signed in to {connection.Subdomain}.zendesk.com. Click Sign in.", AccountState.SignedOut);
        }
        catch (Exception ex)
        {
            Log.Error("Settings: could not load account", ex);
            SetStatus($"Could not connect: {ex.Message}", AccountState.Error);
            _groups.SetReloadEnabled(true);
        }
        finally
        {
            _groups.SetLoading(false);
        }
    }

    /// <summary>
    /// "Catch up since yesterday 17:00": what still needs you since you stopped working last time (your hours as set
    /// on the Notifications page, even before saving). Runs in the app with the saved settings; the button spins until
    /// it's done.
    /// </summary>
    private async Task CatchUpAsync()
    {
        if (CatchUp is null)
            return;
        _catchUp.SetBusy(true, "Catching up\u2026");
        try
        {
            await CatchUp().WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            Log.Info("Settings: Catch up is taking long; the button stops waiting");
        }
        finally
        {
            _catchUp.SetBusy(false);
        }
    }

    /// <summary>The Catch up button's text and hint, with the time it starts from.</summary>
    private void UpdateCatchUp()
    {
        var current = Context.Current();
        var since = Tracking.TicketRules.CatchUpStart(DateTime.Now, current.MyWorkdayStartHour, current.MyWorkdayEndHour, current.MyWorkDays);
        var when = Tracking.TicketRules.CatchUpSince(since, DateTime.Now);
        if (!((RoundButton)_catchUp).IsBusy)
            _catchUp.Text = $"Catch up {when}";
    }

    public override void OnShowing() => UpdateCatchUp();

    private async Task SignInAsync()
    {
        var connection = Context.Current();
        if (!connection.IsConnectionConfigured)
        {
            ThemedDialog.Inform(Context.Window, "Enter your Zendesk subdomain",
                "It's the part before .zendesk.com in the address you use for Zendesk.");
            return;
        }

        Log.Info($"Sign-in started: {connection.BaseUrl}");
        _signingIn = true;
        _signIn.SetBusy(true, "Signing in\u2026");
        SetStatus("Sign in in the window that opened…", AccountState.Checking);

        SessionChanging?.Invoke();
        try
        {
            await Context.SessionFactory(connection).SignInAsync(Context.Window, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Sign-in was cancelled.", AccountState.SignedOut);
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in failed", ex);
            SetStatus("Sign-in failed.", AccountState.SignedOut);
            ThemedDialog.Inform(Context.Window, "Sign-in failed", ex.Message, ThemedDialog.Kind.Error);
            return;
        }
        finally
        {
            _signingIn = false;
            _signIn.SetBusy(false);
            Context.Window.Activate();
        }

        SignedIn?.Invoke(); // remember which Zendesk this is, without saving other pending changes
        SessionChanged?.Invoke();
        await RefreshAsync(force: true);
    }

    private void SignOut()
    {
        SessionChanging?.Invoke();
        Context.SessionFactory(Context.Current()).SignOut();
        _groups.SetReloadEnabled(false);
        SetStatus("Signed out.", AccountState.SignedOut);
        SessionChanged?.Invoke();
    }

    /// <summary>Shows the account status; only the buttons that make sense for the state are visible.</summary>
    private void SetStatus(string text, AccountState state)
    {
        if (state != AccountState.SignedIn)
            Context.SetSession(null, null);
        _status.Text = text;
        _dot.DotColor = state switch
        {
            AccountState.SignedIn => Theme.Success,
            AccountState.Checking => Theme.Warning,
            AccountState.Error => Theme.Danger,
            _ => Theme.Neutral
        };

        // While checking it isn't known yet whether you're signed in, so neither button is offered.
        _signIn.Visible = state is AccountState.SignedOut or AccountState.Error;
        _signIn.Enabled = true;
        _signOut.Visible = state is AccountState.SignedIn or AccountState.Error;
        // Catch up needs the app to be checking with this account: only offered once signed in.
        _catchUpCard.Visible = state is AccountState.SignedIn && CatchUp is not null;
        if (_catchUpCard.Visible)
            UpdateCatchUp();
    }
}
