using System.Text.Json;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>
/// The ticket field "Product" (a dropdown filled in when a ticket is created): its id and the names of its values,
/// read from Zendesk at most once a week and kept in cache\ticket-product-field.json.
/// </summary>
public sealed record ProductField(long Id, Dictionary<string, string> Names, DateTime ReadAt)
{
    /// <summary>What agents see for a value ("orchestrate__cms_paas" -> "CMS PaaS"); the value itself when unknown.</summary>
    public string NameOf(string value) => Names.GetValueOrDefault(value) ?? value;
}

/// <summary>
/// "My products" (Groups → Products): which products an agent works with, and what that means for a ticket.
/// The ticket's Product field can be wrong and the client's Products field out of date, so a ticket is only held back
/// when it's quite sure it's for another product (<see cref="Fit"/>).
/// </summary>
public static class Products
{
    /// <summary>The field's title in Zendesk.</summary>
    public const string FieldTitle = "Product";

    /// <summary>Read again after this.</summary>
    public static readonly TimeSpan FieldMaxAge = CacheAges.ProductField;

    /// <summary>The account's Product field as last read (null until known: then no ticket has a product).</summary>
    public static ProductField? Field
    {
        get => _field;
        set
        {
            _field = value;
            ResponseSlimming.CustomFieldIds = value is null ? [] : [value.Id]; // only this custom field crosses into the app
        }
    }

    private static ProductField? _field;

    /// <summary>The ticket's product value ("orchestrate__cms_paas"); "" when the field is empty; null when unknown.</summary>
    public static string? Of(Ticket ticket)
    {
        if (Field is not { } field)
            return null;
        if (ticket.CustomFields?.FirstOrDefault(f => f.Id == field.Id) is not { } value)
            return ticket.CustomFields is null ? null : "";
        return value.Value is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
    }

    /// <summary>The ticket's product as agents see it ("CMS PaaS"), or null.</summary>
    public static string? NameOf(Ticket ticket) => Of(ticket) is { Length: > 0 } value ? Field?.NameOf(value) ?? value : null;

    // ---- Areas: products grouped, for the picker and for comparing with the client's products (other names) ----

    public const string Content = "Content & marketing";
    public const string Commerce = "Commerce";
    public const string Experimentation = "Experimentation";
    public const string Data = "Data & platform";
    public const string Other = "Other";

    /// <summary>The areas in the order the picker shows them.</summary>
    public static readonly string[] Areas = [Content, Commerce, Experimentation, Data, Other];

    /// <summary>
    /// The area of a product name, from words in it: works for the ticket field ("CMS PaaS", "Configured Commerce (B2B)")
    /// and the client's Products field ("Content Management PaaS"). Other: not about a product ("Account
    /// Administration", "Not Product Related", "Premium Support"), or not known.
    /// </summary>
    public static string AreaOf(string name)
    {
        var n = name.ToLowerInvariant();
        bool Has(params string[] words) => words.Any(n.Contains);
        if (Has("not product", "account admin", "duplicate", "spam", "internal optimizely", "premium support", "other services", "expert services",
                "onboarding", "education", "i do not know", "none of these", "reseller"))
            return Other;
        if (Has("commerce", "pim", "product information", "product recommendation", "product email", "b2b"))
            return Commerce;
        if (Has("experiment", "personaliz", "contentsquare", "scorecard", "netspring", "analytics", "full stack", "optimizely web"))
            return Experimentation;
        if (Has("odp", "data platform", "ocp", "connect platform", "visitor intelligence", "opal", "opti id", "optiid", "segmentation", "cdp"))
            return Data;
        if (Has("cms", "content", "dxp", "headless", "search", "find", "dam", "digital asset", "imagevault", "ektron", "everweb", "siteseeker",
                "graph", "netlify", "vercel", "campaign", "delivra", "marketing"))
            return Content;
        return Other;
    }

    /// <summary>
    /// Whether a ticket is for one of my products. Off (nothing picked): Mine. Unknown: no product, one that isn't about
    /// a product (<see cref="Other"/>), or one that doesn't fit the client (none of the client's products is in that
    /// area, but one is in an area of mine: the field is probably wrong). The client's products never hold a ticket back.
    /// </summary>
    /// <param name="product">The ticket's product value (<see cref="Of"/>).</param>
    /// <param name="clientProducts">The client's Products field (names), if known.</param>
    public static ProductFit Fit(string? product, IReadOnlyCollection<string> myProducts, ProductField? field, IReadOnlyList<string>? clientProducts)
    {
        if (myProducts.Count == 0)
            return ProductFit.Mine;
        if (string.IsNullOrEmpty(product))
            return ProductFit.Unknown;
        if (myProducts.Contains(product))
            return ProductFit.Mine;
        var area = AreaOf(field?.NameOf(product) ?? product);
        if (area == Other)
            return ProductFit.Unknown;
        if (clientProducts is { Count: > 0 })
        {
            var clientAreas = clientProducts.Select(AreaOf).Where(a => a != Other).ToHashSet();
            var myAreas = myProducts.Select(p => AreaOf(field?.NameOf(p) ?? p)).ToHashSet();
            if (clientAreas.Count > 0 && !clientAreas.Contains(area) && clientAreas.Overlaps(myAreas))
                return ProductFit.Unknown;
        }
        return ProductFit.Other;
    }

    /// <summary>Whether a ticket for another product may still notify: its priority is one chosen for other products.</summary>
    public static bool OtherProductNotifies(string? priority, IReadOnlyCollection<string> otherProductPriorities) =>
        otherProductPriorities.Count > 0 && TicketRules.MatchesPriority(priority, [.. otherProductPriorities]);

    /// <summary>What's kept of a ticket held back for another product: "product|priority".</summary>
    public static string Quiet(string? product, string? priority) => $"{product}|{priority ?? "normal"}";

    /// <summary>
    /// A ticket held back for another product (<paramref name="quiet"/>, see <see cref="Quiet"/>) that may notify now:
    /// "Now for CMS PaaS" when its product changed, "Priority raised" when its priority went up; null when neither
    /// (it's wanted because My products changed: no notification for old tickets then).
    /// </summary>
    public static string? ComeBackHeading(string quiet, string? product, string? priority, ProductField? field)
    {
        var bar = quiet.LastIndexOf('|');
        var (wasProduct, wasPriority) = bar < 0 ? (quiet, "normal") : (quiet[..bar], quiet[(bar + 1)..]);
        if (!string.IsNullOrEmpty(product) && product != wasProduct)
            return $"Now for {field?.NameOf(product) ?? product}";
        return TicketRules.PriorityRank(priority) > TicketRules.PriorityRank(wasPriority) ? "Priority raised" : null;
    }

    // ---- The field, cached ----

    private sealed class Entry
    {
        public string Subdomain { get; set; } = "";
        public ProductField? Value { get; set; }
    }

    /// <summary>When Zendesk last said there's no such field (this run).</summary>
    private static (string Subdomain, DateTime At)? _missing;

    private static string CacheFile => Path.Combine(AppPaths.DataDir, "cache", "ticket-product-field.json");

    /// <summary>The field as last read for this account, if any (any age).</summary>
    public static ProductField? LoadCached(string subdomain)
    {
        try
        {
            if (!File.Exists(CacheFile))
                return null;
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(CacheFile));
            return entry?.Subdomain == subdomain ? entry.Value : null;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the saved Product field", ex);
            return null;
        }
    }

    private static void Save(string subdomain, ProductField field)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(new Entry { Subdomain = subdomain, Value = field }));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the Product field", ex);
        }
    }

    /// <summary>
    /// Makes the field known (<see cref="Field"/>): the saved one, read again from Zendesk when over a week old (one
    /// request per 100 fields). Kept as it was when Zendesk can't be asked.
    /// </summary>
    public static async Task EnsureFieldAsync(ZendeskClient zendesk, string subdomain, CancellationToken ct)
    {
        var cached = Field ?? LoadCached(subdomain);
        if (cached is not null && DateTime.UtcNow - cached.ReadAt < FieldMaxAge)
        {
            Field = cached;
            return;
        }
        // An account without the field: asked again after a week, not at every start or Settings visit.
        if (cached is null && _missing is { } missing && missing.Subdomain == subdomain && DateTime.UtcNow - missing.At < FieldMaxAge)
            return;
        try
        {
            var read = await zendesk.GetTicketFieldAsync(FieldTitle, ct);
            Field = read is null ? null : new ProductField(read.Value.Id, read.Value.Names, DateTime.UtcNow);
            if (Field is not null)
                Save(subdomain, Field);
            else
                _missing = (subdomain, DateTime.UtcNow);
            Log.Info(Field is null ? "No ticket field \"Product\": My products can't be used" : $"Ticket field \"Product\": {Field.Names.Count} products");
        }
        catch (HttpRequestException ex)
        {
            if (cached is not null)
                Field = cached; // the week-old one is still better than none
            Log.Error("Could not read the ticket field \"Product\"", ex);
        }
    }
}

public enum ProductFit
{
    /// <summary>One of my products (or nothing picked).</summary>
    Mine,
    /// <summary>Not sure: notifies as usual.</summary>
    Unknown,
    /// <summary>Another product: notifies only for the priorities chosen for other products.</summary>
    Other
}
