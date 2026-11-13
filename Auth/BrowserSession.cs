using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Auth;

/// <summary>
/// Uses the agent's normal Zendesk login (Okta/SAML or password) in an embedded Edge WebView2.
/// API calls run as same-origin <c>fetch</c> requests inside a page on the Zendesk domain, exactly like the
/// Zendesk web app does, so they have the agent's own permissions. Cookies are kept in a private browser
/// profile under %LOCALAPPDATA%\ZendeskWatcher\WebView2.
/// The window stays hidden except during an interactive sign-in. All members must be used on the UI thread.
/// </summary>
public sealed class BrowserSession : IZendeskSession, IDisposable
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SilentSignInTimeout = TimeSpan.FromSeconds(25);

    /// <summary>If the page sits on the (Okta) login page this long without moving on, it wants a password.</summary>
    private static readonly TimeSpan SettledOnLoginPage = TimeSpan.FromSeconds(6);

    /// <summary>Exists after the user signed out, until they sign in again: no silent sign-in in between.</summary>
    private static string SignedOutMarker => Path.Combine(AppPaths.DataDir, "signed-out");

    private DateTime _lastNavigation;

    private readonly Form _window;
    private WebView2 _web;

    /// <summary>A request the hidden browser doesn't answer within this time restarts the browser.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    /// <summary>Held for anything that navigates or sleeps the browser (sign-in, recovery, suspend): nothing else runs then.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Requests running at once in the hidden page (a check sends several side by side).</summary>
    private const int ParallelRequests = 4;
    private readonly SemaphoreSlim _slots = new(ParallelRequests, ParallelRequests);

    /// <summary>When a silent sign-in last worked: requests that failed at the same moment don't each sign in again.</summary>
    private DateTime _recoveredAt;
    private DateTime _recoveryFailedAt;

    /// <summary>Waits until no request is running (for navigating or suspending); give the slots back with <see cref="ReleaseAll"/>.</summary>
    private async Task WaitForRequestsAsync(CancellationToken ct)
    {
        var taken = 0;
        try
        {
            for (; taken < ParallelRequests; taken++)
                await _slots.WaitAsync(ct);
        }
        catch
        {
            if (taken > 0)
                _slots.Release(taken); // cancelled halfway: give back what was taken
            throw;
        }
    }

    private void ReleaseAll() => _slots.Release(ParallelRequests);
    private Task? _initialization;
    private string _baseUrl = "";

    public BrowserSession()
    {
        _window = new Form
        {
            Text = "Sign in to Zendesk",
            Size = new Size(1000, 780),
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = true,
            MinimizeBox = false,
            Icon = ZendeskWatcher.UI.AppIcon.CreateIcon(ZendeskWatcher.UI.AppIcon.NeonGreen, 32)
        };
        // Create the window handles without showing the window, so WebView2 can run hidden.
        _ = _window.Handle;
        _web = CreateBrowserControl();
    }

    private WebView2 CreateBrowserControl()
    {
        var web = new WebView2 { Dock = DockStyle.Fill };
        _window.Controls.Add(web);
        _ = web.Handle;
        return web;
    }

    /// <summary>Throws the hidden browser away and starts a fresh one on the next request (the login is kept).</summary>
    private void ResetBrowser()
    {
        _suspendTimer?.Stop();
        _window.Controls.Remove(_web);
        _web.Dispose();
        _web = CreateBrowserControl();
        _initialization = null;
    }

    /// <summary>
    /// Cookies or the Okta session may still be valid, so try silently first; unless the user signed out on purpose.
    /// </summary>
    public bool MightBeSignedIn => !File.Exists(SignedOutMarker);

    /// <summary>Points the session at a Zendesk account, e.g. https://yourcompany.zendesk.com.</summary>
    public void Configure(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    private string MeUrl => $"{_baseUrl}/api/v2/users/me.json";

    /// <summary>
    /// Where the hidden browser waits between requests: any page on the Zendesk domain allows same-origin fetch.
    /// A public, content-free page, so no personal data ends up in the browser's page cache.
    /// </summary>
    private string HomeUrl => $"{_baseUrl}/robots.txt";
    private string LoginUrl => $"{_baseUrl}/access/login?return_to={Uri.EscapeDataString(MeUrl)}";

    public Task<ApiResponse> GetAsync(string url, CancellationToken ct) => RequestAsync(url, null, null, ct);

    public Task<ApiResponse> SendAsync(string method, string url, string jsonBody, CancellationToken ct) =>
        RequestAsync(url, method, jsonBody, ct);

    /// <summary>
    /// Several requests may run at once (up to <see cref="ParallelRequests"/>): fetch() calls in one page don't get in
    /// each other's way. Getting the page ready (start, wake, onto the Zendesk domain) happens one at a time, and
    /// never while a sign-in or recovery navigates the page.
    /// </summary>
    private async Task<ApiResponse> RequestAsync(string url, string? method, string? jsonBody, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var slot = false;
        try
        {
            await EnsureInitializedAsync();
            Wake();
            if (!IsOnZendesk())
            {
                await WaitForRequestsAsync(ct); // navigating would break requests that are still running
                try { await EnsureOnZendeskAsync(ct); }
                finally { ReleaseAll(); }
            }
            await _slots.WaitAsync(ct);
            slot = true;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await FetchAsync(url, ct, method, jsonBody).WaitAsync(RequestTimeout, ct);
        }
        catch (TimeoutException)
        {
            // The hidden browser stopped answering (e.g. stuck while waking up): start over with a fresh one.
            Log.Error("Browser session: the hidden browser did not respond; restarting it");
            await _gate.WaitAsync(CancellationToken.None);
            try { ResetBrowser(); }
            finally { _gate.Release(); }
            throw new HttpRequestException("The embedded browser did not respond and was restarted.");
        }
        finally
        {
            if (slot)
                _slots.Release();
            SuspendSoon();
        }
    }

    /// <summary>
    /// Opens the login page hidden. With a valid Okta session the SSO round trip completes on its own;
    /// if Okta asks for credentials it times out and the user has to sign in interactively.
    /// </summary>
    public async Task<bool> TryRecoverAsync(CancellationToken ct)
    {
        if (!MightBeSignedIn)
            return false; // signed out on purpose: wait for an explicit sign-in

        await _gate.WaitAsync(ct);
        // Requests run side by side: several can find the session expired at once. One silent sign-in serves them all.
        if (DateTime.UtcNow - _recoveredAt < TimeSpan.FromSeconds(30) || DateTime.UtcNow - _recoveryFailedAt < TimeSpan.FromSeconds(30))
        {
            _gate.Release();
            return _recoveredAt > _recoveryFailedAt; // the answer the last attempt just gave
        }
        try { await WaitForRequestsAsync(ct); } // the page navigates: no request may be running
        catch { _gate.Release(); throw; }
        try
        {
            await EnsureInitializedAsync();
            Wake();
            Log.Info("Browser session: trying silent sign-in");
            _lastNavigation = DateTime.UtcNow;
            _web.CoreWebView2.Navigate(LoginUrl);

            var deadline = DateTime.UtcNow + SilentSignInTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, ct);
                if (await IsSignedInSafeAsync(ct))
                {
                    Log.Info("Browser session: silent sign-in succeeded");
                    await NavigateAsync(HomeUrl, ct);
                    await ClearCacheAsync(); // the sign-in redirect ended on users/me
                    _recoveredAt = DateTime.UtcNow;
                    return true;
                }

                // Okta's login form has loaded and nothing redirects onwards: it wants a password, stop waiting.
                if (!IsOnZendesk() && DateTime.UtcNow - _lastNavigation > SettledOnLoginPage)
                    break;
            }

            Log.Info("Browser session: silent sign-in needs user interaction");
            await NavigateAsync(HomeUrl, ct);
            _recoveryFailedAt = DateTime.UtcNow;
            return false;
        }
        finally
        {
            ReleaseAll();
            _gate.Release();
            SuspendSoon();
        }
    }

    /// <summary>Shows the browser window with the Zendesk login (redirects to Okta) and waits until signed in.</summary>
    public async Task SignInAsync(object? owner, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await WaitForRequestsAsync(ct); } // the sign-in navigates: no request may be running
        catch { _gate.Release(); throw; }
        try
        {
            await EnsureInitializedAsync();

            var checking = false;
            using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += async (_, _) =>
            {
                if (checking)
                    return;
                checking = true;
                try
                {
                    if (await IsSignedInSafeAsync(CancellationToken.None))
                    {
                        timer.Stop();
                        _window.DialogResult = DialogResult.OK; // closes the modal window
                    }
                }
                finally
                {
                    checking = false;
                }
            };

            using var registration = ct.Register(() => _window.DialogResult = DialogResult.Cancel);

            Wake();
            UseLowMemory(false); // visible: render the sign-in pages normally
            // A cover in the app's colors until the sign-in page has loaded. A hidden browser doesn't paint, so when
            // the window appears it can briefly show the last thing it drew (the robots.txt it rests on).
            using var cover = CreateCover();
            _window.Controls.Add(cover);
            cover.BringToFront();
            void Uncover(object? sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                _web.CoreWebView2.NavigationCompleted -= Uncover;
                // A moment for the new page to paint before the cover goes.
                var later = new System.Windows.Forms.Timer { Interval = 250 };
                later.Tick += (_, _) => { later.Dispose(); cover.Visible = false; };
                later.Start();
            }
            _web.CoreWebView2.NavigationCompleted += Uncover;
            _web.CoreWebView2.Navigate(LoginUrl);
            timer.Start();
            DialogResult result;
            try
            {
                result = _window.ShowDialog(owner as IWin32Window); // closing a modal form only hides it; the WebView2 stays alive
            }
            finally
            {
                timer.Stop();
                _web.CoreWebView2.NavigationCompleted -= Uncover;
                _window.Controls.Remove(cover);
            }
            UseLowMemory(true);

            if (result != DialogResult.OK)
                throw new OperationCanceledException("The sign-in window was closed.");

            Log.Info("Browser session: signed in");
            _recoveredAt = DateTime.UtcNow;
            _recoveryFailedAt = default;
            File.Delete(SignedOutMarker);
            await NavigateAsync(HomeUrl, CancellationToken.None);
            await ClearCacheAsync(); // the sign-in redirect ended on users/me
        }
        finally
        {
            ReleaseAll();
            _gate.Release();
            SuspendSoon();
        }
    }

    public void SignOut()
    {
        File.WriteAllText(SignedOutMarker, DateTime.Now.ToString("O")); // no silent sign-in until the user signs in again
        if (_web.CoreWebView2 is null)
            return;

        _web.CoreWebView2.CookieManager.DeleteAllCookies();
        _web.CoreWebView2.Navigate("about:blank");
        Log.Info("Browser session: cookies cleared");
    }

    private Task EnsureInitializedAsync() => _initialization ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        // As light as it can be: no GPU (the hidden page only runs fetch(), the sign-in renders fine in software), and
        // none of the browser's background work (component and safe-browsing updates, sync, extensions).
        var options = new CoreWebView2EnvironmentOptions(
            "--disable-gpu --disable-background-networking --disable-component-update --disable-sync --disable-extensions");
        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "WebView2"), options);
        await _web.EnsureCoreWebView2Async(environment);
        _web.CoreWebView2.NavigationCompleted += (_, _) => _lastNavigation = DateTime.UtcNow;

        // API responses are fetched with cache: 'no-store'; also remove anything cached before (older versions, or
        // pages of the sign-in flow). Cookies (the login) are kept.
        await ClearCacheAsync();
        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        // Only Zendesk and Okta pages: no SmartScreen lookups, no form autofill.
        _web.CoreWebView2.Settings.IsReputationCheckingRequired = false;
        _web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
        _web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        UseLowMemory(true);
    }

    /// <summary>Empties the browser cache (not the cookies, so the login stays).</summary>
    private async Task ClearCacheAsync()
    {
        try
        {
            await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
        }
        catch (Exception ex)
        {
            Log.Error("Browser session: could not clear the cache", ex);
        }
    }

    // ------------------------------------------------------------------ keeping the hidden browser small

    private System.Windows.Forms.Timer? _suspendTimer;

    /// <summary>While hidden, ask the browser to use as little memory as possible.</summary>
    private void UseLowMemory(bool low)
    {
        if (_web.CoreWebView2 is { } core)
            core.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
    }

    /// <summary>Wakes a suspended browser before it's used.</summary>
    private void Wake()
    {
        _suspendTimer?.Stop();
        if (_web.CoreWebView2 is { IsSuspended: true } core)
            core.Resume();
    }

    /// <summary>
    /// Suspends the hidden browser a few seconds after the last request, so it uses (almost) no CPU and less memory
    /// until the next check. Not while the sign-in window is open.
    /// </summary>
    private void SuspendSoon()
    {
        if (_suspendTimer is null)
        {
            _suspendTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _suspendTimer.Tick += async (_, _) =>
            {
                _suspendTimer.Stop();
                // Hold the gate while suspending, so a request can't start while the browser is going to sleep.
                if (_window.Visible || !_gate.Wait(0))
                    return;
                if (_slots.CurrentCount < ParallelRequests)
                {
                    _gate.Release(); // a request is still running; it schedules the next attempt when it ends
                    return;
                }
                try
                {
                    if (_web.CoreWebView2 is { IsSuspended: false } core)
                        await core.TrySuspendAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex)
                {
                    Log.Error("Browser session: could not suspend the hidden browser", ex);
                }
                finally
                {
                    _gate.Release();
                }
            };
        }
        _suspendTimer.Stop();
        _suspendTimer.Start();
    }

    /// <summary>Fetch only works same-origin, so the hidden page must be on the Zendesk domain.</summary>
    private async Task EnsureOnZendeskAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync();
        if (!IsOnZendesk())
            await NavigateAsync(HomeUrl, ct);
    }

    private bool IsOnZendesk() =>
        Uri.TryCreate(_web.CoreWebView2.Source, UriKind.Absolute, out var current) &&
        Uri.TryCreate(_baseUrl, UriKind.Absolute, out var expected) &&
        string.Equals(current.Host, expected.Host, StringComparison.OrdinalIgnoreCase);

    private async Task NavigateAsync(string url, CancellationToken ct)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs e) => completed.TrySetResult();

        _web.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            _web.CoreWebView2.Navigate(url);
            await completed.Task.WaitAsync(NavigationTimeout, ct);
        }
        finally
        {
            _web.CoreWebView2.NavigationCompleted -= Handler;
        }
    }

    /// <summary>"Opening the Zendesk sign-in…" in the app's colors, over the browser until the sign-in page has loaded.</summary>
    private static Label CreateCover() => new()
    {
        Text = "Opening the Zendesk sign-in\u2026",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = ZendeskWatcher.UI.Theme.Body,
        ForeColor = ZendeskWatcher.UI.Theme.TextMuted,
        BackColor = ZendeskWatcher.UI.Theme.Window
    };

    private async Task<bool> IsSignedInSafeAsync(CancellationToken ct)
    {
        try
        {
            if (!IsOnZendesk())
                return false; // still on the Okta pages

            var response = await FetchAsync(MeUrl, ct);
            return response.IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // e.g. the page navigated away during the request
        }
    }

    /// <summary>
    /// Runs fetch() in the page via the DevTools protocol, which can await the returned promise.
    /// Requests that change something need Zendesk's CSRF token, which users/me returns for a cookie session.
    /// </summary>
    private async Task<ApiResponse> FetchAsync(string url, CancellationToken ct, string? method = null, string? jsonBody = null)
    {
        ct.ThrowIfCancellationRequested();

        // The response as an object (one copy of the text, not JSON inside JSON), trimmed to the fields the app reads.
        var toResult = ".then(async r => { let body = await r.text(); if (r.ok) body = " + ResponseSlimming.Script + "(body); " +
                       "return { status: r.status, retryAfter: r.headers.get('Retry-After'), body: body }; })";
        var script = method is null
            ? $"fetch({JsonSerializer.Serialize(url)}, {{ credentials: 'same-origin', cache: 'no-store', headers: {{ 'Accept': 'application/json' }} }})" + toResult
            : $"fetch({JsonSerializer.Serialize(MeUrl)}, {{ credentials: 'same-origin', cache: 'no-store', headers: {{ 'Accept': 'application/json' }} }})" +
              ".then(r => r.json()).then(me => " +
              $"fetch({JsonSerializer.Serialize(url)}, {{ method: {JsonSerializer.Serialize(method)}, credentials: 'same-origin', cache: 'no-store', " +
              "headers: { 'Accept': 'application/json', 'Content-Type': 'application/json', 'X-CSRF-Token': (me.user && me.user.authenticity_token) || '' }, " +
              $"body: {JsonSerializer.Serialize(jsonBody ?? "")} }}))" + toResult;

        var parameters = JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true });
        var resultJson = await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", parameters);

        using var result = JsonDocument.Parse(resultJson);
        if (result.RootElement.TryGetProperty("exceptionDetails", out var exception))
            throw new HttpRequestException($"Request failed in the browser: {exception.GetRawText()}");

        var value = result.RootElement.GetProperty("result").GetProperty("value");
        var status = value.GetProperty("status").GetInt32();
        var body = value.GetProperty("body").GetString() ?? "";
        TimeSpan? retryAfter = value.GetProperty("retryAfter").ValueKind == JsonValueKind.String &&
                               int.TryParse(value.GetProperty("retryAfter").GetString(), out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

        // Without a session Zendesk answers users/me with an anonymous user instead of 401.
        if (status == 200 && url.Contains("/api/v2/users/me.json", StringComparison.Ordinal) && !IsAgent(body))
            status = 401;

        return new ApiResponse(status, body, retryAfter);
    }

    private static bool IsAgent(string meJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(meJson);
            var user = doc.RootElement.GetProperty("user");
            var hasId = user.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number;
            var role = user.TryGetProperty("role", out var r) ? r.GetString() : null;
            return hasId && role is "agent" or "admin";
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _web.Dispose();
        _window.Dispose();
    }
}
