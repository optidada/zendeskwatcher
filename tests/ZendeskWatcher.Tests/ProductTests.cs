using System.Text.Json;

namespace ZendeskWatcher.Tests;

[CollectionDefinition(nameof(ProductTests), DisableParallelization = true)]
public sealed class ProductTestsCollection;

/// <summary>
/// My products: tickets for other products stay quiet (except chosen priorities), but only when the app is fairly sure;
/// the ticket's Product field can be wrong and the client's Products field out of date.
/// </summary>
[Collection(nameof(ProductTests))] // the Product field is shared by the whole app: not side by side with other tests
public class ProductTests : IDisposable
{
    public void Dispose() => Products.Field = null; // the other tests run without one
    /// <summary>The same field in every test (it's shared by the whole app).</summary>
    private static readonly ProductField Field = new(14807, new()
    {
        ["orchestrate__cms_paas"] = "CMS PaaS",
        ["orchestrate__content_management__dxp_cloud_platform"] = "DxP Cloud Platform",
        ["experiment__web_experimentation"] = "Web Experimentation",
        ["monetize__configured_commerce___configured_commerce"] = "Configured Commerce (B2B)",
        ["p_opal"] = "Opal",
        ["other__account_administration"] = "Account Administration",
        ["other__not_product_related"] = "Not Product Related"
    }, DateTime.UtcNow);

    private static readonly string[] Cms = ["orchestrate__cms_paas", "orchestrate__content_management__dxp_cloud_platform"];

    [Theory]
    [InlineData("CMS PaaS", Products.Content)]
    [InlineData("DxP Cloud Platform", Products.Content)]
    [InlineData("Content Management PaaS", Products.Content)] // the client's Products field says it this way
    [InlineData("Search & Navigation Standalone", Products.Content)]
    [InlineData("Configured Commerce (B2B)", Products.Commerce)]
    [InlineData("Commerce Analytics", Products.Commerce)]
    [InlineData("Product Information Management (PIM)", Products.Commerce)]
    [InlineData("Web Experimentation", Products.Experimentation)]
    [InlineData("Experimentation – HIPAA", Products.Experimentation)]
    [InlineData("Data Platform (ODP)", Products.Data)]
    [InlineData("Opal", Products.Data)]
    [InlineData("Account Administration", Products.Other)]
    [InlineData("Not Product Related", Products.Other)]
    [InlineData("Premium Support (NTA/CSLM)", Products.Other)]
    public void Products_fall_into_areas(string name, string area) => Assert.Equal(area, Products.AreaOf(name));

    [Fact]
    public void Only_a_clear_other_product_is_held_back()
    {
        ProductFit Fit(string? product, string[]? client = null, string[]? mine = null) => Products.Fit(product, mine ?? Cms, Field, client);

        Assert.Equal(ProductFit.Mine, Fit("experiment__web_experimentation", mine: [])); // nothing picked: all products
        Assert.Equal(ProductFit.Mine, Fit("orchestrate__cms_paas"));
        Assert.Equal(ProductFit.Unknown, Fit(null)); // not known
        Assert.Equal(ProductFit.Unknown, Fit("")); // empty field
        Assert.Equal(ProductFit.Unknown, Fit("other__account_administration")); // not about a product
        Assert.Equal(ProductFit.Other, Fit("experiment__web_experimentation"));
        Assert.Equal(ProductFit.Other, Fit("experiment__web_experimentation", client: [])); // client's products unknown
        Assert.Equal(ProductFit.Other, Fit("experiment__web_experimentation", client: ["Content Management PaaS", "Web Experimentation"])); // has both: trust the ticket
        Assert.Equal(ProductFit.Other, Fit("experiment__web_experimentation", client: ["Configured Commerce"])); // fits none of mine either
        // The client has only CMS: an Experimentation ticket from them is probably mislabeled.
        Assert.Equal(ProductFit.Unknown, Fit("experiment__web_experimentation", client: ["Content Management PaaS"]));
    }

    [Fact]
    public void A_quiet_ticket_comes_back_only_when_its_product_changed_or_it_was_raised()
    {
        var quiet = Products.Quiet("experiment__web_experimentation", "normal");
        Assert.Equal("Now for CMS PaaS", Products.ComeBackHeading(quiet, "orchestrate__cms_paas", "normal", Field));
        Assert.Equal("Priority raised", Products.ComeBackHeading(quiet, "experiment__web_experimentation", "high", Field));
        Assert.Null(Products.ComeBackHeading(quiet, "experiment__web_experimentation", "normal", Field)); // My products changed: no notification
        Assert.Null(Products.ComeBackHeading(Products.Quiet("experiment__web_experimentation", null), "experiment__web_experimentation", null, Field));
    }

    [Fact]
    public void Suggestions_are_the_products_of_most_of_my_tickets()
    {
        var mine = new Dictionary<string, int>
        {
            ["orchestrate__content_management__dxp_cloud_platform"] = 30, ["orchestrate__cms_paas"] = 8, ["p_opal"] = 1,
            ["other__account_administration"] = 12 // not a product: never suggested
        };
        Assert.Equal(Cms.Reverse(), SettingsPages.ProductsPage.Suggest(mine, Field));
        Assert.Equal(["p_opal"], SettingsPages.ProductsPage.Suggest(new Dictionary<string, int> { ["p_opal"] = 1 }, Field)); // few tickets: any
    }

    [Fact]
    public void The_browser_keeps_only_the_product_field()
    {
        Products.Field = Field;
        Assert.Contains("const F = [14807]", ResponseSlimming.Script);
        Assert.Contains("r.custom_fields.filter", ResponseSlimming.ScriptWithoutDescriptions);
        Assert.Contains("custom_fields", ResponseSlimming.FieldsOf<Ticket>());

        var ticket = new Ticket { CustomFields = [Value(14807, "orchestrate__cms_paas")] };
        Assert.Equal("orchestrate__cms_paas", Products.Of(ticket));
        Assert.Equal("CMS PaaS", Products.NameOf(ticket));
        Assert.Equal("", Products.Of(new Ticket { CustomFields = [Value(14807, null)] })); // empty
        Assert.Null(Products.Of(new Ticket())); // the response had no custom fields: not known
    }

    [Fact]
    public async Task Saved_tickets_without_products_are_read_again_once_but_a_few_never_force_full_reads()
    {
        Products.Field = Field;
        var now = DateTimeOffset.UtcNow;
        var client = new ZendeskClient(_settings, _zendesk);

        // Saved before the field was known: all read again (once).
        var old = new GroupDigest(now.AddHours(-1), [new(1, null, null, now.AddDays(-3)), new(2, null, null, now.AddDays(-2))]);
        var (_, _, incremental) = await client.ReadGroupDigestAsync(10, old, CancellationToken.None);
        Assert.False(incremental);

        // Nearly all have one: only what changed, as usual.
        var fresh = new GroupDigest(now.AddHours(-1), [new(1, null, null, now.AddDays(-3), "orchestrate__cms_paas"),
            new(2, null, null, now.AddDays(-2), ""), new(3, null, null, now.AddDays(-1))]);
        (_, _, incremental) = await client.ReadGroupDigestAsync(10, fresh, CancellationToken.None);
        Assert.True(incremental);
    }

    private static CustomFieldValue Value(long id, string? value) =>
        new() { Id = id, Value = JsonSerializer.SerializeToElement(value) };

    // ------------------------------------------------------------------ end to end

    private readonly FakeZendesk _zendesk = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly Settings _settings = new()
    {
        Subdomain = "test",
        WatchedGroups = [new WatchedGroup { Id = 10, Name = "Tier 2" }],
        TrackMyTickets = false,
        MyProducts = [.. Cms]
    };

    private Ticket Add(long id, string product, string priority = "normal")
    {
        var ticket = _zendesk.AddUnassigned(id);
        ticket.Priority = priority;
        ticket.CustomFields = [Value(Field.Id, product)];
        return ticket;
    }

    [Fact]
    public async Task New_tickets_for_other_products_stay_quiet_unless_urgent_or_moved_to_mine()
    {
        File.Delete(AppPaths.StateFile);
        Products.Field = Field;
        var watcher = new TicketWatcher(_settings, new ZendeskClient(_settings, _zendesk), _notifier);
        await watcher.InitializeAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None); // the group's first look
        _notifier.Shown.Clear();

        Add(101, "orchestrate__cms_paas");
        var other = Add(102, "experiment__web_experimentation");
        Add(103, "experiment__web_experimentation", "high"); // P2: other products notify for P1 and P2 by default
        Add(104, "other__not_product_related"); // not sure: notifies
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal([101, 103, 104], _notifier.Shown.Select(n => n.TicketId!.Value).Order());
        Assert.Equal("CMS PaaS · Tier 2", _notifier.Shown.Single(n => n.TicketId == 101).Context);
        var now = watcher.Snapshot();
        Assert.Equal(3, now.Unassigned); // the quiet one isn't counted…
        Assert.Equal(1, now.OtherProducts); // …but Insights says it's there
        _notifier.Shown.Clear();

        // Triage moves it to a product of mine while it's still unassigned: it comes now.
        other.CustomFields = [Value(Field.Id, "orchestrate__cms_paas")];
        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal("Now for CMS PaaS", Assert.Single(_notifier.Shown).Heading);
    }
}
