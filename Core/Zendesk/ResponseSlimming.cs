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
    /// with their events) keep only the fields the models have. A ticket's description (the customer's first message)
    /// is dropped from search results (often hundreds of tickets); tickets read by id keep its first
    /// <see cref="DescriptionChars"/> characters for a new ticket's preview. Anything else, or text that isn't JSON,
    /// is returned as it is.
    /// </summary>
    public static string Script { get; } = Build(descriptions: true);

    /// <summary>The same without any description (Look &amp; sound → Show the comment text is off: not even read).</summary>
    public static string ScriptWithoutDescriptions { get; } = Build(descriptions: false);

    public static string ScriptFor(bool descriptions) => descriptions ? Script : ScriptWithoutDescriptions;

    /// <summary>How much of a ticket's description crosses over (a notification shows about 200 characters).</summary>
    public const int DescriptionChars = 300;

    private static string Build(bool descriptions)
    {
        static string Names(IEnumerable<string> names) => JsonSerializer.Serialize(names);
        return "(text => { try { " +
               "const keep = (o, keys) => { if (!o || typeof o !== 'object') return o; const r = {}; for (const k of keys) if (k in o) r[k] = o[k]; return r; }; " +
               $"const T = {Names(FieldsOf<Ticket>())}, A = {Names(FieldsOf<Audit>())}, E = {Names(FieldsOf<AuditEvent>())}; " +
               "const brief = t => { const r = keep(t, T); delete r.description; return r; }; " +
               (descriptions
                   ? "const ticket = t => { const r = keep(t, T); " +
                     $"if (typeof r.description === 'string' && r.description.length > {DescriptionChars}) r.description = r.description.slice(0, {DescriptionChars}); return r; }}; "
                   : "const ticket = brief; ") +
               "const audit = a => { const r = keep(a, A); if (a && Array.isArray(a.events)) r.events = a.events.map(e => keep(e, E)); return r; }; " +
               "const d = JSON.parse(text); " +
               "if (Array.isArray(d.tickets)) d.tickets = d.tickets.map(ticket); " +
               "if (Array.isArray(d.results)) d.results = d.results.map(brief); " +
               "if (d.ticket && typeof d.ticket === 'object') d.ticket = ticket(d.ticket); " +
               "if (Array.isArray(d.audits)) d.audits = d.audits.map(audit); " +
               "return JSON.stringify(d); " +
               "} catch (e) { return text; } })";
    }
}
