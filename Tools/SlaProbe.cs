using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ZendeskWatcher.Tools;

/// <summary>
/// Developer helper (--probe-sla &lt;file&gt;): with the app's login, finds out what SLA data Zendesk has: SLA policies
/// (admins only, usually), the SLA timers on a few open Urgent/High tickets (include=slas), and ticket and
/// organization fields that sound like an SLA or support plan. Writes names, timer kinds and minutes only, never
/// ticket subjects, comments or people. The app must not be running (they share the browser profile).
/// </summary>
public static class SlaProbe
{
    public static void Run(string subdomain, string outFile)
    {
        var host = $"https://{subdomain}.zendesk.com";
        var lines = new List<string> { $"SLA probe {DateTime.Now:yyyy-MM-dd HH:mm}", "" }; // no subdomain: the file may be shared
        var form = new Form { Text = "Checking Zendesk SLA data…", Size = new Size(500, 200), StartPosition = FormStartPosition.CenterScreen };
        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);

        async Task<(int Status, JsonElement? Json)> Get(string path)
        {
            var script = $"fetch({JsonSerializer.Serialize(host + path)}, {{ credentials: 'same-origin', headers: {{ 'Accept': 'application/json' }} }})" +
                         ".then(async r => ({ status: r.status, body: await r.text() }))";
            var result = await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                JsonSerializer.Serialize(new { expression = script, awaitPromise = true, returnByValue = true }));
            using var doc = JsonDocument.Parse(result);
            var value = doc.RootElement.GetProperty("result").GetProperty("value");
            var status = value.GetProperty("status").GetInt32();
            try { return (status, JsonDocument.Parse(value.GetProperty("body").GetString() ?? "").RootElement.Clone()); }
            catch (JsonException) { return (status, null); }
        }

        static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";

        static bool Interesting(string text) =>
            new[] { "sla", "support", "plan", "tier", "entitle", "contract", "level", "premium", "service" }
                .Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

        async Task Probe()
        {
            var (meStatus, me) = await Get("/api/v2/users/me.json");
            lines.Add($"Signed in: {(meStatus == 200 && me?.GetProperty("user").GetProperty("id").GetInt64() > 0 ? "yes, role " + Str(me!.Value.GetProperty("user"), "role") : $"no ({meStatus})")}");

            var (polStatus, policies) = await Get("/api/v2/slas/policies.json");
            lines.Add($"SLA policies (/api/v2/slas/policies): HTTP {polStatus}");
            if (polStatus == 200 && policies?.TryGetProperty("sla_policies", out var list) == true)
                foreach (var p in list.EnumerateArray())
                {
                    lines.Add($"  Policy: {Str(p, "title")}");
                    if (p.TryGetProperty("policy_metrics", out var metrics))
                        foreach (var m in metrics.EnumerateArray())
                            lines.Add($"    {Str(m, "priority"),-7} {Str(m, "metric"),-22} {Str(m, "target"),6} min  business hours: {Str(m, "business_hours")}");
                }

            var query = Uri.EscapeDataString("type:ticket status<solved priority>normal");
            var (searchStatus, search) = await Get($"/api/v2/search.json?query={query}&per_page=15");
            var ids = search?.TryGetProperty("results", out var results) == true
                ? results.EnumerateArray().Select(t => t.GetProperty("id").GetInt64()).ToList() : [];
            lines.Add("");
            lines.Add($"Open Urgent/High tickets found: {ids.Count} (search HTTP {searchStatus})");
            long? orgId = null;
            if (ids.Count > 0)
            {
                var (showStatus, shown) = await Get($"/api/v2/tickets/show_many.json?ids={string.Join(",", ids)}&include=slas");
                lines.Add($"SLA timers on them (include=slas): HTTP {showStatus}");
                if (shown?.TryGetProperty("tickets", out var tickets) == true)
                    foreach (var t in tickets.EnumerateArray())
                    {
                        orgId ??= t.TryGetProperty("organization_id", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : null;
                        var timers = t.TryGetProperty("slas", out var slas) && slas.ValueKind == JsonValueKind.Object &&
                                     slas.TryGetProperty("policy_metrics", out var pm)
                            ? string.Join("; ", pm.EnumerateArray().Select(m =>
                                $"{Str(m, "metric")} {Str(m, "stage")}{(Str(m, "breach_at") is { Length: > 0 } b ? $", breach in {(int)(DateTimeOffset.Parse(b) - DateTimeOffset.UtcNow).TotalMinutes} min" : "")}"))
                            : "none";
                        lines.Add($"  #{Str(t, "id")} {Str(t, "priority"),-7} {timers}");
                    }

                // The targets themselves: "apply_sla" metric events carry the policy, target minutes and business hours.
                lines.Add("");
                var (incStatus, _) = await Get($"/api/v2/incremental/ticket_metric_events.json?start_time={DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds()}");
                lines.Add($"Incremental metric events (all tickets): HTTP {incStatus}");
                lines.Add("Applied SLA targets per ticket (include=metric_events), with the client's SLA Type:");
                foreach (var ticketId in ids)
                {
                    var (evStatus, ev) = await Get($"/api/v2/tickets/{ticketId}.json?include=metric_events");
                    if (ev is not { } doc || evStatus != 200)
                    {
                        lines.Add($"  #{ticketId}: HTTP {evStatus}");
                        continue;
                    }
                    var t = doc.GetProperty("ticket");
                    var slaType = "";
                    if (t.TryGetProperty("organization_id", out var o) && o.ValueKind == JsonValueKind.Number)
                    {
                        var (_, org) = await Get($"/api/v2/organizations/{o.GetInt64()}.json");
                        if (org?.GetProperty("organization").TryGetProperty("organization_fields", out var of2) == true &&
                            of2.TryGetProperty("sla_type", out var st))
                            slaType = st.ToString();
                    }
                    var applied = new List<string>();
                    if (doc.TryGetProperty("metric_events", out var me2) && me2.ValueKind == JsonValueKind.Object)
                        foreach (var metric in me2.EnumerateObject())
                            foreach (var e in metric.Value.EnumerateArray().Where(e => Str(e, "type") == "apply_sla"))
                                if (e.TryGetProperty("sla", out var sla))
                                    applied.Add($"{metric.Name}: {Str(sla, "target")} min" +
                                                $"{(Str(sla, "business_hours") == "True" ? " (business hours)" : "")}" +
                                                $"{(sla.TryGetProperty("policy", out var pol) ? $" [{Str(pol, "title")}]" : "")}");
                    lines.Add($"  #{ticketId} {Str(t, "priority"),-7} type {(slaType.Length > 0 ? slaType : "-"),-17} " +
                              (applied.Count > 0 ? string.Join("; ", applied.Distinct()) : "no SLA applied"));
                }
            }

            var (fieldsStatus, fields) = await Get("/api/v2/ticket_fields.json?per_page=100");
            lines.Add("");
            lines.Add($"Ticket fields that sound like SLA / plan (HTTP {fieldsStatus}):");
            if (fields?.TryGetProperty("ticket_fields", out var tf) == true)
                foreach (var f in tf.EnumerateArray().Where(f => Interesting(Str(f, "title")) || Interesting(Str(f, "title_in_portal"))))
                {
                    var options = f.TryGetProperty("custom_field_options", out var co) && co.ValueKind == JsonValueKind.Array
                        ? " options: " + string.Join(", ", co.EnumerateArray().Select(x => Str(x, "name"))) : "";
                    lines.Add($"  {Str(f, "title")} ({Str(f, "type")}, active {Str(f, "active")}){options}");
                }

            var (orgFieldsStatus, orgFields) = await Get("/api/v2/organization_fields.json");
            lines.Add("");
            lines.Add($"Organization fields (HTTP {orgFieldsStatus}):");
            if (orgFields?.TryGetProperty("organization_fields", out var of) == true)
                foreach (var f in of.EnumerateArray())
                {
                    lines.Add($"  {Str(f, "title")} (key {Str(f, "key")}, {Str(f, "type")})");
                    // The choices of plan-like drop-downs (e.g. SLA Type): names and values, no customer data.
                    if (Interesting(Str(f, "key")) && f.TryGetProperty("custom_field_options", out var choices) && choices.ValueKind == JsonValueKind.Array)
                        foreach (var c in choices.EnumerateArray())
                            lines.Add($"      option: {Str(c, "name")} = {Str(c, "value")}");
                }
            if (orgId is { } id)
            {
                var (orgStatus, org) = await Get($"/api/v2/organizations/{id}.json");
                lines.Add($"One client's organization fields (HTTP {orgStatus}), names and plan-like values only:");
                if (org?.GetProperty("organization").TryGetProperty("organization_fields", out var values) == true)
                    foreach (var v in values.EnumerateObject())
                        lines.Add($"  {v.Name}{(Interesting(v.Name) ? " = " + v.Value : "")}");
            }
        }

        form.Shown += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "WebView2"),
                    new CoreWebView2EnvironmentOptions("--disable-gpu"));
                await web.EnsureCoreWebView2Async(env);
                var loaded = new TaskCompletionSource();
                web.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
                web.CoreWebView2.Navigate($"{host}/robots.txt");
                await loaded.Task;
                await Probe();
            }
            catch (Exception ex)
            {
                lines.Add($"Failed: {ex.GetType().Name}: {ex.Message}");
            }
            File.WriteAllLines(outFile, lines);
            form.Close();
        };
        Application.Run(form);
    }
}
