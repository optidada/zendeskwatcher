using ZendeskWatcher.Auth;

namespace ZendeskWatcher.Tests;

/// <summary>Take it / assign: the CSRF token is fetched once and kept in the page, fetched again only when refused.</summary>
public class CsrfScriptTests
{
    [Fact]
    public void Change_script_keeps_the_token_and_retries_once_with_a_fresh_one()
    {
        var script = BrowserSession.ChangeScript("https://x/api/v2/users/me.json", "https://x/api/v2/tickets/1.json", "PUT", "{\"ticket\":{}}");
        Assert.Contains("window.__zwCsrf ? Promise.resolve(window.__zwCsrf)", script.Replace("(!fresh && window.__zwCsrf)", "window.__zwCsrf"));
        Assert.Contains("token(false).then(change)", script);
        Assert.Contains("token(true).then(change)", script); // refused: once more with a fresh token
        Assert.Contains("\"PUT\"", script);
        Assert.Contains("'X-CSRF-Token': t", script);
        if (Environment.GetEnvironmentVariable("ZW_DUMP_CSRF_SCRIPT") is { Length: > 0 } file)
            File.WriteAllText(file, script); // for checking it in a real JavaScript engine
    }
}
