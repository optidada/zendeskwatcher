using System.Text.Json;
using AppKit;
using CoreGraphics;
using Foundation;
using WebKit;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Mac;

/// <summary>
/// The Mac counterpart of the Windows BrowserSession: the agent's normal Zendesk login (Okta) in a WKWebView, and API
/// calls as same-origin fetch() inside a page on the Zendesk domain. Cookies stay in the app's own WebKit store
/// (~/Library/WebKit). The window is only shown for an interactive sign-in. Use on the main thread only.
/// </summary>
public sealed class WebSession : NSObject, IZendeskSession, IWKNavigationDelegate
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SilentSignInTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SettledOnLoginPage = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private static string SignedOutMarker => Path.Combine(AppPaths.DataDir, "signed-out");

    private readonly NSWindow _window;
    private readonly WKWebView _web;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _baseUrl = "";
    private DateTime _lastNavigation;
    private TaskCompletionSource? _navigation;

    public WebSession()
    {
        var config = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.DefaultDataStore };
        _web = new WKWebView(new CGRect(0, 0, 1000, 760), config) { NavigationDelegate = this };
        _window = new NSWindow(new CGRect(0, 0, 1000, 760),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable, NSBackingStore.Buffered, false)
        {
            Title = "Sign in to Zendesk",
            ContentView = _web,
            ReleasedWhenClosed = false
        };
        _window.Center();
    }

    public bool MightBeSignedIn => !File.Exists(SignedOutMarker);

    public void Configure(string baseUrl) => _baseUrl = baseUrl.TrimEnd('/');

    private string MeUrl => $"{_baseUrl}/api/v2/users/me.json";
    private string HomeUrl => $"{_baseUrl}/robots.txt";
    private string LoginUrl => $"{_baseUrl}/access/login?return_to={Uri.EscapeDataString(MeUrl)}";

    public Task<ApiResponse> GetAsync(string url, CancellationToken ct) => RequestAsync(url, null, null, ct);

    public Task<ApiResponse> SendAsync(string method, string url, string jsonBody, CancellationToken ct) =>
        RequestAsync(url, method, jsonBody, ct);

    private async Task<ApiResponse> RequestAsync(string url, string? method, string? jsonBody, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsOnZendesk())
                await NavigateAsync(HomeUrl, ct);
            return await FetchAsync(url, method, jsonBody).WaitAsync(RequestTimeout, ct);
        }
        catch (TimeoutException)
        {
            Log.Error("Web session: the hidden browser did not respond");
            _web.LoadRequest(new NSUrlRequest(new NSUrl("about:blank")));
            throw new HttpRequestException("The embedded browser did not respond.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryRecoverAsync(CancellationToken ct)
    {
        if (!MightBeSignedIn)
            return false;

        await _gate.WaitAsync(ct);
        try
        {
            Log.Info("Web session: trying silent sign-in");
            Load(LoginUrl);
            var deadline = DateTime.UtcNow + SilentSignInTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000, ct);
                if (await IsSignedInSafeAsync())
                {
                    Log.Info("Web session: silent sign-in succeeded");
                    await NavigateAsync(HomeUrl, ct);
                    return true;
                }
                if (!IsOnZendesk() && DateTime.UtcNow - _lastNavigation > SettledOnLoginPage)
                    break;
            }
            Log.Info("Web session: silent sign-in needs user interaction");
            await NavigateAsync(HomeUrl, ct);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignInAsync(object? owner, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Load(LoginUrl);
            _window.MakeKeyAndOrderFront(null);
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);

            while (true)
            {
                await Task.Delay(1000, ct);
                if (!_window.IsVisible)
                    throw new OperationCanceledException("The sign-in window was closed.");
                if (await IsSignedInSafeAsync())
                    break;
            }

            _window.OrderOut(null);
            Log.Info("Web session: signed in");
            File.Delete(SignedOutMarker);
            await NavigateAsync(HomeUrl, CancellationToken.None);
        }
        catch
        {
            _window.OrderOut(null);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SignOut()
    {
        File.WriteAllText(SignedOutMarker, DateTime.Now.ToString("O"));
        var store = WKWebsiteDataStore.DefaultDataStore;
        store.RemoveDataOfTypes(WKWebsiteDataStore.AllWebsiteDataTypes, NSDate.DistantPast, () => Log.Info("Web session: cookies cleared"));
        Load("about:blank");
    }

    private void Load(string url)
    {
        _lastNavigation = DateTime.UtcNow;
        _web.LoadRequest(new NSUrlRequest(new NSUrl(url)));
    }

    private async Task NavigateAsync(string url, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        _navigation = done;
        Load(url);
        try
        {
            await done.Task.WaitAsync(NavigationTimeout, ct);
        }
        finally
        {
            if (_navigation == done)
                _navigation = null;
        }
    }

    [Export("webView:didFinishNavigation:")]
    public void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
    {
        _lastNavigation = DateTime.UtcNow;
        _navigation?.TrySetResult();
    }

    [Export("webView:didFailNavigation:withError:")]
    public void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
    {
        _lastNavigation = DateTime.UtcNow;
        _navigation?.TrySetResult(); // the caller checks where it ended up
    }

    private bool IsOnZendesk() =>
        _web.Url?.Host is { } host &&
        Uri.TryCreate(_baseUrl, UriKind.Absolute, out var expected) &&
        string.Equals(host, expected.Host, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsSignedInSafeAsync()
    {
        try
        {
            return IsOnZendesk() && (await FetchAsync(MeUrl, null, null)).IsSuccess;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs fetch() in the page with callAsyncJavaScript, which waits for the promise. Changes (PUT/POST) need
    /// Zendesk's CSRF token, which users/me returns for a cookie session.
    /// </summary>
    private Task<ApiResponse> FetchAsync(string url, string? method, string? jsonBody)
    {
        const string ToResult = ".then(async r => JSON.stringify({ status: r.status, retryAfter: r.headers.get('Retry-After'), body: await r.text() }))";
        var script = "return " + (method is null
            ? $"fetch({JsonSerializer.Serialize(url)}, {{ credentials: 'same-origin', cache: 'no-store', headers: {{ 'Accept': 'application/json' }} }})" + ToResult
            : $"fetch({JsonSerializer.Serialize(MeUrl)}, {{ credentials: 'same-origin', cache: 'no-store', headers: {{ 'Accept': 'application/json' }} }})" +
              ".then(r => r.json()).then(me => " +
              $"fetch({JsonSerializer.Serialize(url)}, {{ method: {JsonSerializer.Serialize(method)}, credentials: 'same-origin', cache: 'no-store', " +
              "headers: { 'Accept': 'application/json', 'Content-Type': 'application/json', 'X-CSRF-Token': (me.user && me.user.authenticity_token) || '' }, " +
              $"body: {JsonSerializer.Serialize(jsonBody ?? "")} }}))" + ToResult) + ";";

        var result = new TaskCompletionSource<ApiResponse>();
        _web.CallAsyncJavaScript(script, null, null, WKContentWorld.Page, (value, error) =>
        {
            if (error is not null)
            {
                result.TrySetException(new HttpRequestException($"Request failed in the browser: {error.LocalizedDescription}"));
                return;
            }
            try
            {
                result.TrySetResult(Parse(url, value?.ToString() ?? ""));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        return result.Task;
    }

    private static ApiResponse Parse(string url, string json)
    {
        using var response = JsonDocument.Parse(json);
        var status = response.RootElement.GetProperty("status").GetInt32();
        var body = response.RootElement.GetProperty("body").GetString() ?? "";
        TimeSpan? retryAfter = int.TryParse(response.RootElement.GetProperty("retryAfter").GetString(), out var seconds)
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
}
