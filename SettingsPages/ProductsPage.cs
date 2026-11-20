using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// Products (under Groups): the products I work with, from the ticket field "Product". The picked ones show as buttons at
/// the top (× removes); below, every product grouped by area with a tick box and how many tickets it had in the monitored
/// groups in 90 days (from the tickets the Clients list keeps). Nothing picked: all products.
/// </summary>
internal sealed class ProductsPage : SettingsPage
{
    private const int ListWidth = Kit.CardWidth - 44;

    /// <summary>Tickets for other products still notify with these priorities.</summary>
    private static readonly (string Label, string[] Priorities)[] OtherProductChoices =
        [("Never", []), ("P1", ["urgent"]), ("P1, P2", ["urgent", "high"])];

    private readonly GroupsPage _groups;
    private readonly ProductChips _chips = new() { Width = ListWidth, Margin = new Padding(0, 4, 0, 6), EmptyText = "No products picked: every product notifies.",
        CautionText = "The Product field can be wrong. A ticket filed under another product stays quiet, except the priorities chosen below." };
    private readonly Label _suggest = Kit.LinkLabel("Suggest from my tickets");
    private readonly Label _clearAll = Kit.LinkLabel("Remove all");
    private readonly ChoicePills _otherProducts = new([.. OtherProductChoices.Select(c => c.Label)]) { AccessibleName = "Other products notify" };
    private readonly FlowLayoutPanel _list = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty
    };
    private readonly Label _listHint = Theme.Label("", Theme.Small, Theme.TextMuted, ListWidth);
    private readonly Button _count = Theme.SecondaryButton("Count tickets");
    private readonly ThemedToolTip _tips = new(); // in the app's colors

    private List<string> _selected = [];
    private readonly Dictionary<string, CheckBox> _boxes = [];

    /// <summary>Each area's heading: its tick box (all of it) and "3 of 7".</summary>
    private readonly Dictionary<string, (CheckBox Box, Label Picked, int Total)> _areaHeads = [];

    /// <summary>The areas opened in the list (all start closed; kept while the app runs, like the topics on How it works).</summary>
    internal static readonly HashSet<string> Opened = [];

    /// <summary>A button at the top that stands for a whole area: "area:" and its name.</summary>
    private const string AreaPrefix = "area:";

    /// <summary>The chevron at the end of an area's row and the space before it (the products' numbers line up with the totals).</summary>
    private const int ChevronWidth = 28;
    private string? _listShown; // what the list was built from, so showing the page again doesn't rebuild it
    private bool _filling;

    public ProductsPage(SettingsContext context, GroupsPage groups)
        : base(context, "Products", "Pick the products you work with: new tickets for other products stay quiet, except the priorities you choose (P1 and P2 to begin with). Your own tickets always notify.")
    {
        _groups = groups;
        var mine = new Card("My products", "New tickets and colleagues' tickets for other products don't notify, except the priorities " +
            "you choose here. When the product is missing or doesn't fit the client, it notifies. Your own and CC'd tickets always notify.", Kit.CardWidth);
        mine.Body.Controls.Add(_chips);
        var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 4) };
        _suggest.Margin = new Padding(0, 4, 16, 4);
        _clearAll.Margin = new Padding(0, 4, 0, 4);
        links.Controls.Add(_suggest);
        links.Controls.Add(_clearAll);
        mine.Body.Controls.Add(links);
        var other = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 0) };
        var otherLabel = Theme.Label("Other products notify");
        otherLabel.Margin = new Padding(0, 10, 16, 0);
        _otherProducts.Margin = new Padding(0, 2, 0, 2);
        other.Controls.Add(otherLabel);
        other.Controls.Add(_otherProducts);
        mine.Body.Controls.Add(other);
        Panel.Controls.Add(mine);

        var all = new Card("All products", $"From the ticket field Product. The numbers: tickets in your monitored groups in the last " +
            $"{ZendeskClient.ClientLookBackDays} days.", Kit.CardWidth);
        all.Body.Controls.Add(_list);
        _listHint.Margin = new Padding(0, 6, 0, 4);
        all.Body.Controls.Add(_listHint);
        _count.Margin = new Padding(0, 6, 0, 0);
        _count.Visible = false;
        all.Body.Controls.Add(_count);
        Panel.Controls.Add(all);

        _chips.Removed += value => SetPicked(value.StartsWith(AreaPrefix) && Field is { } field
            ? AreaValues(field, value[AreaPrefix.Length..]) : [value], false);
        _clearAll.Click += (_, _) => RemoveAll();
        _suggest.Click += (_, _) =>
        {
            if (Suggestion() is { Count: > 0 } suggestion)
            {
                _selected = suggestion;
                ShowPicked();
                Changed();
            }
        };
        _otherProducts.SelectedIndexChanged += (_, _) => Changed();
        _count.Click += async (_, _) => await CountAsync();
        Context.SessionUpdated += _ => { _listShown = null; if (Panel.Visible) ShowList(); };
    }

    public override bool IsSubPage => true; // under Groups

    /// <summary>"Remove all": nothing picked, every product notifies (also for the rendered samples).</summary>
    internal void RemoveAll() => SetPicked([.. _selected], false);

    public override void Load(Settings settings)
    {
        _selected = [.. settings.MyProducts];
        var other = settings.OtherProductPriorities;
        _otherProducts.SelectedIndex = other.Count == 0 ? 0 : other.Any(p => p is "high" or "normal" or "low") ? 2 : 1;
        ShowPicked();
    }

    public override void Apply(Settings s)
    {
        s.MyProducts = [.. _selected];
        s.OtherProductPriorities = [.. OtherProductChoices[_otherProducts.SelectedIndex].Priorities];
    }

    public override void OnShowing()
    {
        ShowList();
        _ = EnsureFieldAsync();
    }

    private string Subdomain => Context.Current().Subdomain;
    private ProductField? Field => Products.Field ?? Products.LoadCached(Subdomain);

    /// <summary>The field isn't known yet (first start, before the first check): read it now (signed in).</summary>
    private async Task EnsureFieldAsync()
    {
        if (Field is not null || Context.Client is not { } client)
            return;
        await Products.EnsureFieldAsync(client, Subdomain, CancellationToken.None);
        _listShown = null;
        ShowList();
    }

    private void SetPicked(IEnumerable<string> values, bool on)
    {
        foreach (var value in values.ToList())
        {
            _selected.Remove(value);
            if (on)
                _selected.Add(value);
        }
        ShowPicked();
        Changed();
    }

    /// <summary>A product area's values (not "Other": those always notify), in no particular order.</summary>
    private static List<string> AreaValues(ProductField field, string area) =>
        field.Names.Keys.Where(v => Products.AreaOf(field.NameOf(v)) == area).ToList();

    /// <summary>
    /// The buttons at the top, by area: a whole area picked is one button ("All of Commerce"), otherwise one per product.
    /// And the ticks in the list, from <see cref="_selected"/>.
    /// </summary>
    private void ShowPicked()
    {
        var field = Field;
        var chips = new List<(string Value, string Name)>();
        if (field is null)
            chips.AddRange(_selected.Select(v => (v, v)));
        else
        {
            var placed = new HashSet<string>();
            foreach (var area in Products.Areas)
            {
                var values = AreaValues(field, area);
                if (area != Products.Other && values.Count > 1 && values.All(_selected.Contains))
                {
                    chips.Add((AreaPrefix + area, $"All of {area}"));
                    placed.UnionWith(values);
                    continue;
                }
                foreach (var value in values.Where(_selected.Contains).OrderBy(field.NameOf, StringComparer.CurrentCultureIgnoreCase))
                {
                    chips.Add((value, field.NameOf(value)));
                    placed.Add(value);
                }
            }
            chips.AddRange(_selected.Where(v => !placed.Contains(v)).Select(v => (v, field.NameOf(v)))); // no longer in Zendesk's list
        }
        _chips.SetItems(chips);
        _clearAll.Visible = _selected.Count > 0;
        _otherProducts.Enabled = _selected.Count > 0;
        _filling = true;
        foreach (var (value, box) in _boxes)
            box.Checked = _selected.Contains(value);
        if (field is not null)
            foreach (var (area, head) in _areaHeads)
            {
                var picked = AreaValues(field, area).Count(_selected.Contains);
                head.Box.Checked = picked == head.Total;
                head.Picked.Text = picked == 0 ? "" : $"{picked} of {head.Total}";
            }
        _filling = false;
    }

    // ------------------------------------------------------------------ counts

    private List<TicketDigest> GroupTickets()
    {
        var digests = ClientsView.SavedDigests(Subdomain);
        return _groups.MonitoredGroups().Select(g => digests.GetValueOrDefault(g.Id)).OfType<GroupDigest>().SelectMany(d => d.Tickets).ToList();
    }

    private static Dictionary<string, int> Count(IEnumerable<TicketDigest> tickets) => tickets
        .Where(t => !string.IsNullOrEmpty(t.Product)).GroupBy(t => t.Product!).ToDictionary(g => g.Key, g => g.Count());

    private List<string>? Suggestion() =>
        Field is { } field && Context.Me is { } me ? Suggest(Count(GroupTickets().Where(t => t.AssigneeId == me.Id)), field) : null;

    /// <summary>
    /// The products of my tickets worth picking: about a product (not "Other"), and at least 2 tickets and a tenth of
    /// mine (any with few tickets in all).
    /// </summary>
    internal static List<string> Suggest(IReadOnlyDictionary<string, int> mine, ProductField field)
    {
        var about = mine.Where(kv => Products.AreaOf(field.NameOf(kv.Key)) != Products.Other).ToList();
        var total = about.Sum(kv => kv.Value);
        var least = total < 10 ? 1 : Math.Max(2, total / 10);
        return about.Where(kv => kv.Value >= least).OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    /// <summary>Reads the groups' tickets (as the Clients list does: only what changed since last time), then shows the counts.</summary>
    private async Task CountAsync()
    {
        if (Context.Client is not { } client)
            return;
        _count.Enabled = false;
        _count.Text = "Counting…";
        try
        {
            await ClientsView.ReadDigestsAsync(_groups.MonitoredGroups().Select(g => g.Id), client, Subdomain);
        }
        catch (Exception ex)
        {
            if (ex is not AuthRequiredException)
                Log.Error("Settings: could not count the tickets for My products", ex);
        }
        finally
        {
            _count.Text = "Count tickets";
            _count.Enabled = true;
        }
        _listShown = null;
        ShowList();
    }

    // ------------------------------------------------------------------ the list

    /// <summary>Every product by area (not "Other": those always notify), most tickets first; a tick box and the count each.</summary>
    private void ShowList()
    {
        var field = Field;
        var tickets = GroupTickets();
        var counted = tickets.Any(t => t.Product is not null);
        var counts = Count(tickets);
        var mine = Context.Me is { } me ? Count(tickets.Where(t => t.AssigneeId == me.Id)) : [];
        var key = $"{field?.Id}|{field?.Names.Count}|{tickets.Count}|{string.Join(",", counts.Select(kv => kv.Value))}|{Context.Me?.Id}";
        _count.Visible = !counted && Context.Client is not null && _groups.MonitoredGroups().Count > 0;
        _suggest.Visible = counted && Context.Me is not null;
        _listHint.Text = field is null
            ? Context.Client is null ? "Sign in first (Account page): the products come from Zendesk." : "Reading the products from Zendesk…"
            : !counted ? "No numbers yet: Count tickets reads your groups' tickets of the last 90 days (as the Clients list does)." : "";
        _listHint.Visible = _listHint.Text.Length > 0;
        if (key == _listShown)
            return;
        _listShown = key;

        _list.SuspendLayout();
        foreach (var old in _list.Controls.Cast<Control>().ToList())
            old.Dispose();
        _list.Controls.Clear();
        _boxes.Clear();
        _areaHeads.Clear();
        if (field is not null)
        {
            foreach (var area in Products.Areas.Where(a => a != Products.Other))
            {
                var values = field.Names.Keys.Where(v => Products.AreaOf(field.NameOf(v)) == area)
                    .OrderByDescending(v => counts.GetValueOrDefault(v)).ThenBy(field.NameOf, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (values.Count == 0)
                    continue;
                // The heading: a tick box for the whole area, then "▸ Commerce  1 of 3" (a click opens or closes it) and its tickets.
                var head = new TableLayoutPanel
                {
                    ColumnCount = 5, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, _list.Controls.Count == 0 ? 0 : 6, 0, 0),
                    MinimumSize = new Size(ListWidth, 0), MaximumSize = new Size(ListWidth, 0) // the totals line up with the products' numbers
                };
                head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
                head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
                head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var all = Kit.Check("");
                all.AccessibleName = $"All of {area}";
                all.Margin = new Padding(0, 3, 0, 3);
                all.Anchor = AnchorStyles.Left;
                var open = Opened.Contains(area);
                var title = Theme.Label(area, Theme.BodyBold, Theme.Text);
                // Opens and closes like the topics on How it works: the chevron at the right, a click anywhere on the row.
                var chevron = new Chevron { Anchor = AnchorStyles.Right, Margin = new Padding(8, 0, 0, 0), Open = open };
                title.Cursor = Cursors.Hand;
                title.Anchor = AnchorStyles.Left;
                title.Margin = new Padding(0, 0, 0, 0);
                var picked = Theme.Label("", Theme.Small, Theme.TextMuted);
                picked.Anchor = AnchorStyles.Right;
                picked.Margin = new Padding(0, 3, 12, 0);
                var total = Theme.Label(counted ? values.Sum(v => counts.GetValueOrDefault(v)).ToString() : "", Theme.BodyBold, Theme.Text);
                total.Anchor = AnchorStyles.Right;
                total.Margin = new Padding(0, 0, 4, 0);
                head.Controls.Add(all, 0, 0);
                head.Controls.Add(title, 1, 0);
                head.Controls.Add(picked, 2, 0);
                head.Controls.Add(total, 3, 0);
                head.Controls.Add(chevron, 4, 0);
                _list.Controls.Add(head);
                _areaHeads[area] = (all, picked, values.Count);
                var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(24, 0, 0, 4), Visible = open };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ListWidth - 84 - ChevronWidth));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
                var areaName = area;
                all.CheckedChanged += (_, _) =>
                {
                    if (!_filling)
                        SetPicked(values, all.Checked);
                };
                // An area's products are made the first time it's opened: most stay closed, and ~60 tick boxes with
                // their numbers made up most of the page's first showing (~350 ms → about a third).
                var filled = false;
                void Fill()
                {
                    if (filled)
                        return;
                    filled = true;
                    grid.SuspendLayout();
                    FillArea(grid, values, field, counts, mine, counted);
                    grid.ResumeLayout();
                }
                foreach (var target in new Control[] { head, title, picked, total, chevron })
                {
                    target.Cursor = Cursors.Hand;
                    target.Click += (_, _) =>
                    {
                        var nowOpen = !grid.Visible;
                        if (nowOpen) Opened.Add(areaName); else Opened.Remove(areaName);
                        if (nowOpen)
                            Fill();
                        chevron.Open = nowOpen;
                        grid.Visible = nowOpen;
                    };
                }
                if (open)
                    Fill();
                _list.Controls.Add(grid);
            }
        }
        _list.ResumeLayout();
        ShowPicked(); // names known now
    }

    /// <summary>One area's products: a tick box (ticked when picked) and its tickets in your groups each.</summary>
    private void FillArea(TableLayoutPanel grid, List<string> values, ProductField field, Dictionary<string, int> counts,
        Dictionary<string, int> mine, bool counted)
    {
        for (var row = 0; row < values.Count; row++)
        {
            var value = values[row];
            var n = counts.GetValueOrDefault(value);
            var box = Kit.Check(field.NameOf(value));
            box.Margin = new Padding(0, 1, 0, 1);
            if (counted && n == 0)
                box.ForeColor = Theme.TextMuted; // none in your groups: dimmed
            box.Checked = _selected.Contains(value);
            box.CheckedChanged += (_, _) =>
            {
                if (!_filling)
                    SetPicked([value], box.Checked);
            };
            if (mine.GetValueOrDefault(value) is > 0 and var m)
                _tips.SetToolTip(box, $"{m} of them assigned to you");
            var number = Theme.Label(counted ? n.ToString() : "", Theme.Body, n == 0 ? Theme.TextMuted : Theme.Text);
            number.Anchor = AnchorStyles.Right;
            number.Margin = new Padding(0, 0, 4, 0);
            grid.Controls.Add(box, 0, row);
            grid.Controls.Add(number, 1, row);
            _boxes[value] = box;
        }
    }
}
