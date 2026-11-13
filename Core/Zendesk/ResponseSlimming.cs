using System.Reflection;
using System.Text.Json;

namespace ZendeskWatcher.Zendesk;

/// <summary>
/// Zendesk returns complete objects (a ticket with its whole first message, custom fields, tags; an audit with
/// metadata and the HTML of each comment), while the app keeps only a few fields. The browser session runs
/// <see cref="Script"/> on each response before handing it over, so only those fields cross into the app: much less
/// text to copy and parse, and less memory. The field lists come from the models, so they always match.
/// </summary>
public static class ResponseSlimming
{
    /// <summary>The JSON names of the fields the app reads (snake_case, as Zendesk sends them).</summary>
    public static IReadOnlyList<string> FieldsOf<T>() => typeof(T)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanWrite)
        .Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name))
        .ToList();

    /// <summary>
    /// A JavaScript function <c>text =&gt; text</c>: tickets (in "tickets", "results" or "ticket") and audits ("audits",
    /// with their events) keep only the fields the models have. Anything else, or text that isn't JSON, is returned
    /// as it is.
    /// </summary>
    public static string Script { get; } = Build();

    private static string Build()
    {
        static string Names(IEnumerable<string> names) => JsonSerializer.Serialize(names);
        return "(text => { try { " +
               "const keep = (o, keys) => { if (!o || typeof o !== 'object') return o; const r = {}; for (const k of keys) if (k in o) r[k] = o[k]; return r; }; " +
               $"const T = {Names(FieldsOf<Ticket>())}, A = {Names(FieldsOf<Audit>())}, E = {Names(FieldsOf<AuditEvent>())}; " +
               "const ticket = t => keep(t, T); " +
               "const audit = a => { const r = keep(a, A); if (a && Array.isArray(a.events)) r.events = a.events.map(e => keep(e, E)); return r; }; " +
               "const d = JSON.parse(text); " +
               "if (Array.isArray(d.tickets)) d.tickets = d.tickets.map(ticket); " +
               "if (Array.isArray(d.results)) d.results = d.results.map(ticket); " +
               "if (d.ticket && typeof d.ticket === 'object') d.ticket = ticket(d.ticket); " +
               "if (Array.isArray(d.audits)) d.audits = d.audits.map(audit); " +
               "return JSON.stringify(d); " +
               "} catch (e) { return text; } })";
    }
}
