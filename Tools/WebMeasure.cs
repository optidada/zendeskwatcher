using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ZendeskWatcher.Tools;

/// <summary>
/// Developer helper (--measure-zendesk-web): opens the Zendesk Agent Workspace with the app's login, waits a minute for
/// it to load, then counts every request the page makes for a few minutes, by kind. Gives a fair comparison between
/// "Zendesk open in a browser tab" and this app. WebSocket messages are not counted (only connections).
/// </summary>
public static class WebMeasure
{
    public static void Run(string subdomain, int minutes, string outFile)
    {
        var host = $"{subdomain}.zendesk.com";
        var warmUp = TimeSpan.FromMinutes(1);
        var counts = new SortedDictionary<string, int>();
        var counting = false;
        DateTime? started = null;

        var form = new Form { Text = $"Measuring Zendesk in the browser – please leave this window alone ({minutes + 1} min)", Size = new Size(1200, 850), StartPosition = FormStartPosition.CenterScreen };
        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        void Count(string uri)
        {
            if (!counting || !Uri.TryCreate(uri, UriKind.Absolute, out var u))
                return;
            var kind = !u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ? $"other host: {u.Host}"
                : u.AbsolutePath.Contains("graphql", StringComparison.OrdinalIgnoreCase) ? "Zendesk GraphQL"
                : u.AbsolutePath.StartsWith("/api/v2/", StringComparison.OrdinalIgnoreCase) ? "Zendesk REST API (/api/v2)"
                : u.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ? "Zendesk other API (/api/...)"
                : "Zendesk page/assets";
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }

        form.Shown += async (_, _) =>
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "WebView2"),
                new CoreWebView2EnvironmentOptions("--disable-gpu"));
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            web.CoreWebView2.WebResourceRequested += (_, e) => Count(e.Request.Uri);
            web.CoreWebView2.Navigate($"https://{host}/agent/dashboard");

            await Task.Delay(warmUp);
            counting = true;
            started = DateTime.Now;
            await Task.Delay(TimeSpan.FromMinutes(minutes));
            Save();
            form.Close();
        };

        // Closed early: still save what was counted so far.
        form.FormClosing += (_, _) => Save();

        void Save()
        {
            if (!counting || started is null)
                return;
            counting = false;
            minutes = Math.Max(1, (int)Math.Round((DateTime.Now - started.Value).TotalMinutes));

            var total = counts.Values.Sum();
            var zendesk = counts.Where(kv => !kv.Key.StartsWith("other host")).Sum(kv => kv.Value);
            var lines = new List<string>
            {
                $"Zendesk Agent Workspace (/agent/dashboard), idle, measured {started:yyyy-MM-dd HH:mm} for {minutes} min after a 1 min warm-up",
                $"All requests: {total} ({total / (double)minutes:0.#} per minute)",
                $"To {host}: {zendesk} ({zendesk / (double)minutes:0.#} per minute)",
                ""
            };
            lines.AddRange(counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value,6}  ({kv.Value / (double)minutes,5:0.#}/min)  {kv.Key}"));
            File.WriteAllLines(outFile, lines);
        }

        Application.Run(form);
    }
}
