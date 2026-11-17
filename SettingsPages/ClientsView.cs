using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// The list on the Clients page: the clients (Zendesk organizations) of each monitored group, ticked unless muted.
/// A click on a name opens the client's choices (<see cref="ClientMenu"/>), shown at the right end of the row. Only
/// clients with a choice are stored, so new clients are included automatically. Clients come from the group's
/// tickets of the last 90 days (Zendesk doesn't tie them to groups).
/// </summary>
internal sealed class ClientsView
{
    /// <summary>A group's clients: id, name, tickets in the last 90 days.</summary>
    /// <param name="Products">From the client's "Products" field in Zendesk; often out of date (a hint, never a rule).</param>
    internal sealed record Client(long Id, string Name, int Tickets, List<string>? Products = null);

    /// <summary>A client matches the filter by its name or one of its products.</summary>
    internal static bool Matches(Client client, string filter) =>
        filter.Length == 0 || client.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || MatchedProducts(client, filter).Count > 0;

    /// <summary>The client's products that contain the filter text (the reason a client matched, when its name doesn't).</summary>
    internal static List<string> MatchedProducts(Client client, string filter) =>
        filter.Length == 0 ? [] : [.. (client.Products ?? []).Where(p => p.Contains(filter, StringComparison.CurrentCultureIgnoreCase))];

    /// <summary>The row's tooltip: name, recent tickets, and the products Zendesk lists (with a word on how reliable that is).</summary>
    private static string TipFor(Client c)
    {
        var tip = c.Tickets > 0 ? $"{c.Name} · {c.Tickets} ticket{(c.Tickets == 1 ? "" : "s")} in the last {ZendeskClient.ClientLookBackDays} days" : c.Name;
        if (c.Products is { Count: > 0 } products)
            tip += $"\nProducts in Zendesk: {string.Join(" · ", products)}\n(may be out of date; bundled products aren't listed)";
        return tip;
    }

    private readonly AssigneeList _list;
    private readonly ThinScrollBar _scrollBar = new() { BackColor = Theme.Surface };
    private readonly Label _message = new LinkedLabel
    {
        AutoSize = false,
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Body, ForeColor = Theme.TextMuted,
        BackColor = Theme.Surface, Visible = false
    };
    private int _scroll;

    /// <summary>Clients with a choice (id -> choice): the only ones stored.</summary>
    private readonly Dictionary<long, ClientChoice> _choices = [];

    /// <summary>The filter box's text: only clients whose name contains it are shown ("" for all).</summary>
    private string _filter = "";

    /// <summary>Clients per group loaded from Zendesk, remembered like the group list.</summary>
    private static (string Subdomain, DateTime At, Dictionary<long, List<Client>> Groups)? _cache;

    /// <summary>The clients from before the last daily refresh: in view while it loads, and for a group that failed.</summary>
    private static (string Subdomain, Dictionary<long, List<Client>> Groups) _old = ("", []);

    private int _version;
    private int _failed;
    /// <summary>The clients in view per group (null before any loaded).</summary>
    private Dictionary<long, List<Client>>? _inView;
    private bool _loaded => _inView is not null;
    private List<(WatchedGroup Group, List<Client> Clients)> _shown = [];

    /// <summary>Where the list is shown: the same place as the group list.</summary>
    public Panel Host { get; } = new() { BackColor = Theme.Surface, Visible = false };

    /// <summary>A client's choice changed.</summary>
    public event Action? Changed;

    /// <summary>The line under the list changed (<see cref="Hint"/>).</summary>
    public event Action? HintChanged;

    public string Hint { get; private set; } = "";

    public ClientsView(int width)
    {
        _list = new AssigneeList(width,
            isCovered: id => ModeOf(id) != ClientMode.Muted,
            setCovered: (client, covered) =>
            {
                // The box mutes for good or unmutes; the client's other choices (important, sound) stay.
                var choice = ChoiceFor(client);
                choice.IsMuted = !covered;
                choice.MutedUntil = null;
                Store(choice);
            })
        {
            Badge = id => _choices.GetValueOrDefault(id) is { } c ? BadgeFor(c) : null,
            // Each group: a tick box for all its clients (mixed when some are muted) and a count; folded until opened.
            GroupTick = group => group.People.All(p => ModeOf(p.Id) != ClientMode.Muted) ? true
                : group.People.All(p => ModeOf(p.Id) == ClientMode.Muted) ? false : null,
            GroupNote = GroupNote,
            SingleColumn = true // one client per line, each with "Options ▾"
        };
        _list.GroupToggled += group =>
        {
            // All ticked: mute them all; otherwise (none or some): all notify. Other choices (important, sound) stay.
            var mute = group.People.All(p => ModeOf(p.Id) != ClientMode.Muted);
            foreach (var client in group.People)
            {
                var choice = ChoiceFor(client);
                choice.IsMuted = mute;
                choice.MutedUntil = null;
                Store(choice);
            }
        };
        _list.Changed += () => { UpdateHint(); Changed?.Invoke(); };
        _list.FocusMoved += row =>
        {
            // Scroll just enough to show the keyboard's row.
            if (row.Top < _scroll)
                ScrollTo(row.Top);
            else if (row.Bottom > _scroll + Host.Height)
                ScrollTo(row.Bottom - Host.Height);
        };
        _list.MenuRequested += (client, at) =>
            ClientMenu.Build(ChoiceFor(client), choice =>
            {
                Store(choice);
                // The row stays where it is (as on the Groups page): it only looks different. The list is sorted
                // into its sections again after Save or Discard, or when the page is shown next time.
                _list.Invalidate();
                UpdateHint();
                Changed?.Invoke();
            }, AppearancePage.SoundOptions.Where(o => o.Value != ToastSound.Silent).Select(o => (o.Value, o.Label)).Prepend((ToastSound.Silent, "Silent")).ToList())
            .Show(at);
        // A group's Options ▾: one choice for all its clients (those shown, while filtering). Only what was changed is
        // applied: picking a sound keeps each client's Important or Only urgent, and the other way round.
        _list.GroupMenuRequested += (group, at) =>
        {
            var choices = group.People.Select(ChoiceFor).ToList();
            var first = choices.FirstOrDefault();
            bool Same(Func<ClientChoice, object?> part) => first is not null && choices.All(c => Equals(part(c), part(first)));
            var all = new ClientChoice
            {
                Name = $"all {group.People.Count} clients in {group.Title}",
                Mode = Same(c => c.Mode) ? first!.Mode : ClientMode.Normal,
                IsMuted = Same(c => (c.IsMuted, c.MutedUntil)) && first!.IsMuted,
                MutedUntil = Same(c => (c.IsMuted, c.MutedUntil)) ? first!.MutedUntil : null,
                Sound = Same(c => c.Sound) ? first!.Sound : null
            };
            ClientMenu.Build(all, chosen =>
            {
                var modeChanged = chosen.Mode != all.Mode || chosen.IsMuted != all.IsMuted || chosen.MutedUntil != all.MutedUntil;
                var soundChanged = chosen.Sound != all.Sound;
                foreach (var choice in choices)
                {
                    if (modeChanged)
                        (choice.Mode, choice.IsMuted, choice.MutedUntil) = (chosen.Mode, chosen.IsMuted, chosen.MutedUntil);
                    if (soundChanged)
                        choice.Sound = chosen.Sound;
                    Store(choice);
                }
                _list.Invalidate();
                UpdateHint();
                Changed?.Invoke();
            }, AppearancePage.SoundOptions.Where(o => o.Value != ToastSound.Silent).Select(o => (o.Value, o.Label)).Prepend((ToastSound.Silent, "Silent")).ToList())
            .Show(at);
        };
        Host.Controls.Add(_message);
        Host.Controls.Add(_scrollBar);
        Host.Controls.Add(_list);
        _scrollBar.State = () => (0, Math.Max(0, _list.Height - 1), Host.Height, _scroll);
        _scrollBar.ScrollTo = ScrollTo;
        Host.Resize += (_, _) => LayoutList();
        Host.MouseWheel += OnWheel;
        _list.MouseWheel += OnWheel;
        _list.Resize += (_, _) => ScrollTo(_scroll);
    }

    /// <summary>"14 clients · 2 muted · 1 customized" on a group's line.</summary>
    private string GroupNote(AssigneeList.Group group)
    {
        var muted = group.People.Count(p => ModeOf(p.Id) == ClientMode.Muted);
        var customized = group.People.Count(p => ModeOf(p.Id) != ClientMode.Muted && _choices.TryGetValue(p.Id, out var c) && !c.IsDefaultAt(DateTimeOffset.UtcNow));
        var text = group.People.Count == 1 ? "1 client" : $"{group.People.Count} clients";
        return text + (muted > 0 ? $" · {muted} muted" : "") + (customized > 0 ? $" · {customized} customized" : "");
    }

    private ClientMode ModeOf(long id) => _choices.GetValueOrDefault(id)?.ModeAt(DateTimeOffset.UtcNow) ?? ClientMode.Normal;

    /// <summary>A copy of the client's choice, or a new Normal one.</summary>
    private ClientChoice ChoiceFor(AssigneeList.Person client) =>
        _choices.GetValueOrDefault(client.Id)?.Copy() ?? new ClientChoice { Id = client.Id, Name = client.Name, GroupId = GroupOf(client.Id) };

    /// <summary>Keeps a choice, or forgets it when it's back to the defaults.</summary>
    private void Store(ClientChoice choice)
    {
        if (choice.IsDefaultAt(DateTimeOffset.UtcNow))
            _choices.Remove(choice.Id);
        else
            _choices[choice.Id] = choice;
    }

    /// <summary>
    /// What the row says at its right end: the client's choice ("★ Important", "Urgent only"; also while muted),
    /// "until Fri 14:00" for a mute for a while, "♪" for its own sound. In the accent color while it's important.
    /// </summary>
    internal static (string Text, bool Accent)? BadgeFor(ClientChoice choice)
    {
        var now = DateTimeOffset.UtcNow;
        var mode = choice.Mode switch
        {
            ClientMode.Important => "\u2605 Important",
            ClientMode.OnlyUrgent => "Urgent only",
            _ => null
        };
        var until = choice.IsMutedAt(now) && choice.MutedUntil is { } end
            ? $"muted until {Clock.DayTime(end.ToLocalTime().DateTime)}"
            : null;
        var text = string.Join("  ", new[] { mode, until, choice.Sound is null ? null : "\u266a" }.Where(t => t is not null));
        return text.Length == 0 ? null : (text, choice.ModeAt(now) == ClientMode.Important);
    }

    /// <summary>The group a client is shown under first (for showing an unticked one there before the list loads).</summary>
    private long GroupOf(long clientId) =>
        _shown.FirstOrDefault(s => s.Clients.Any(c => c.Id == clientId)).Group?.Id ?? 0;

    private void OnWheel(object? sender, MouseEventArgs e) =>
        ScrollTo(_scroll - e.Delta * SystemInformation.MouseWheelScrollLines * (int)(26 * Host.DeviceDpi / 96f) / 3 / 120);

    private void ScrollTo(int position)
    {
        _scroll = Math.Clamp(position, 0, Math.Max(0, _list.Height - Host.Height));
        _list.Top = -_scroll;
        _scrollBar.Invalidate();
    }

    private void LayoutList()
    {
        var s = Host.DeviceDpi / 96f;
        var bar = (int)(10 * s);
        _scrollBar.Bounds = new Rectangle(Host.Width - bar, 0, bar, Host.Height);
        _list.Left = 0;
        _list.Width = Host.Width - bar - (int)(6 * s);
        ScrollTo(_scroll);
        _scrollBar.BringToFront();
        _message.BringToFront();
    }

    public void Load(Settings settings)
    {
        _choices.Clear();
        foreach (var client in settings.Clients.Where(c => !c.IsDefaultAt(DateTimeOffset.UtcNow)))
            _choices[client.Id] = client.Copy();
        _list.Invalidate();
        UpdateHint();
    }

    public void Apply(Settings settings) =>
        settings.Clients = _choices.Values
            .Where(c => !c.IsDefaultAt(DateTimeOffset.UtcNow))
            .Select(c => c.Copy())
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id).ToList();

    /// <summary>Sorts the list into its sections again (after Save or Discard), scrolled to the top.</summary>
    public void Resort()
    {
        if (_lastGroups.Count > 0)
        {
            _list.KeepFolds(); // open groups stay open, also once they have no customized clients left
            Arrange(_lastGroups, _inView); // from the top: the rows moved, and the changed clients now lead their group
        }
    }

    /// <summary>While signed out: greyed, no ticking or client menu, still scrolls.</summary>
    public bool ReadOnly
    {
        get => _list.ReadOnly;
        set => _list.ReadOnly = value;
    }

    /// <summary>
    /// How many clients in the list have a choice other than Normal. Choices for clients not in the list (their group
    /// isn't monitored right now) don't count: they're kept for when the group comes back.
    /// </summary>
    public int CustomizedCount => ShownCustomized().Count;

    private List<long> ShownCustomized()
    {
        var now = DateTimeOffset.UtcNow;
        return _shown.SelectMany(s => s.Clients).Select(c => c.Id).Distinct()
            .Where(id => _choices.GetValueOrDefault(id) is { } c && !c.IsDefaultAt(now)).ToList();
    }

    /// <summary>"Clear customizations": the clients in the list back to Normal (Save keeps it, Discard brings them back).</summary>
    public void ClearAll()
    {
        foreach (var id in ShownCustomized())
            _choices.Remove(id);
        Resort();
        _list.Invalidate();
        UpdateHint();
        Changed?.Invoke();
    }

    /// <summary>A choice made from a notification while Settings is open.</summary>
    public void SetChoice(ClientChoice choice)
    {
        Store(choice.Copy());
        _list.Invalidate();
        UpdateHint();
    }

    /// <summary>Reload was clicked: fetch the clients again next time.</summary>
    public static void ClearCache()
    {
        _cache = null;
        _old = ("", []);
        ListCache.Clear(CacheName);
    }

    private const string CacheName = "clients-products"; // renamed when products came: older cached lists have none

    /// <summary>Groups whose clients are being fetched right now (a second request joins the first).</summary>
    private static readonly Dictionary<(long Group, ZendeskClient Client), Task<List<Client>?>> Loading = [];

    /// <summary>A group's clients from Zendesk; null when they couldn't be loaded.</summary>
    private static Task<List<Client>?> LoadGroupAsync(long groupId, ZendeskClient client)
    {
        if (Loading.TryGetValue((groupId, client), out var running))
            return running;
        var task = Fetch();
        Loading[(groupId, client)] = task;
        return task;

        async Task<List<Client>?> Fetch()
        {
            await Task.Yield(); // registered in Loading before it can finish
            try
            {
                return (await client.GetGroupClientsAsync(groupId, CancellationToken.None))
                    .Select(c => new Client(c.Id, c.Name, c.Tickets, c.Products)).ToList();
            }
            catch (Exception ex)
            {
                if (ex is not AuthRequiredException)
                    Log.Error($"Settings: could not load the clients of group {groupId}", ex);
                return null;
            }
            finally
            {
                Loading.Remove((groupId, client));
            }
        }
    }

    /// <summary>For the screenshot tool: sample clients (cached, so nothing is requested).</summary>
    internal static void SetSample(string subdomain, Dictionary<long, List<Client>> groups) => _cache = (subdomain, DateTime.UtcNow, groups);

    /// <summary>
    /// Shows the clients of these groups (loading what isn't cached, when signed in), filtered by name. Without a
    /// session only the unticked clients show, under the group they were unticked in.
    /// </summary>
    public async Task ShowAsync(IReadOnlyList<WatchedGroup> groups, ZendeskClient? client, string subdomain)
    {
        var version = ++_version;
        Dictionary<long, List<Client>>? loaded = null;
        var failed = 0;
        var refreshed = false; // the old clients were in view while loading: the scroll position stays

        if (client is not null && groups.Count > 0)
        {
            if (_cache is null && ListCache.Load<Dictionary<long, List<Client>>>(CacheName, subdomain, anyAge: true) is { } saved)
                _cache = (subdomain, saved.At, saved.Value); // from an earlier run
            // Over a day old: shown while the clients load again, and kept for a group that fails to load.
            if (_old.Subdomain != subdomain)
                _old = (subdomain, []);
            var expired = _cache is { } aged && aged.Subdomain == subdomain && DateTime.UtcNow - aged.At > GroupsPage.CacheLifetime;
            if (expired)
                _old = (subdomain, _cache!.Value.Groups);
            if (_cache is not { } cache || cache.Subdomain != subdomain || expired)
                _cache = (subdomain, DateTime.UtcNow, []);
            var old = _old.Groups;
            var cached = _cache.Value.Groups;
            var missing = groups.Where(g => !cached.ContainsKey(g.Id)).ToList();
            if (missing.Count > 0)
            {
                if (missing.Any(g => old.ContainsKey(g.Id)))
                {
                    _inView = cached.Concat(old.Where(o => !cached.ContainsKey(o.Key))).ToDictionary();
                    Arrange(groups, _inView);
                    refreshed = true;
                }
                else
                    ShowMessage("Loading the clients of your groups…");
                SetHint("Loading…");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                // All groups at once (the app sends a few requests side by side).
                var results = await Task.WhenAll(missing.Select(async group => (group.Id, Clients: await LoadGroupAsync(group.Id, client))));
                foreach (var (id, clients) in results)
                {
                    if (clients is not null)
                        cached[id] = clients;
                    else if (!old.ContainsKey(id))
                        failed++;
                }
                Log.Info($"Settings: clients of {results.Count(r => r.Clients is not null)} group(s) in {clock.ElapsedMilliseconds} ms");
                ListCache.Save(CacheName, subdomain, _cache.Value.At, cached);
            }
            if (version != _version)
                return; // the groups, filter or session changed meanwhile
            // A group that failed to load keeps its old clients in view (tried again next time).
            loaded = old.Count == 0 ? cached : cached.Concat(old.Where(o => !cached.ContainsKey(o.Key))).ToDictionary();
        }
        else if (client is null && groups.Count > 0)
        {
            // Signed out: the clients as last loaded (any age; the page says they may be out of date), read-only.
            if (_cache is null && ListCache.Load<Dictionary<long, List<Client>>>(CacheName, subdomain, anyAge: true) is { } saved)
                _cache = (subdomain, saved.At, saved.Value);
            if (_cache is { } cache && cache.Subdomain == subdomain)
                loaded = cache.Groups;
        }
        else if (version != _version)
            return;

        _inView = loaded;
        _failed = failed;
        Arrange(groups, loaded, keepScroll: refreshed);
    }

    /// <summary>
    /// Shows only the clients whose name contains <paramref name="text"/> (all when empty), with their groups open. A
    /// group's tick box then applies to the clients shown. Choices made meanwhile don't move or hide rows.
    /// </summary>
    public void Filter(string text)
    {
        text = text.Trim();
        if (text == _filter)
            return;
        _filter = text;
        _list.ForgetFilteredFolds();
        if (_lastGroups.Count > 0)
            Arrange(_lastGroups, _inView);
    }

    /// <summary>The groups last shown, for re-sorting after a choice.</summary>
    private IReadOnlyList<WatchedGroup> _lastGroups = [];

    private void Arrange(IReadOnlyList<WatchedGroup> groups, Dictionary<long, List<Client>>? loaded, bool keepScroll = false)
    {
        _lastGroups = groups;
        _shown = Sections(groups, loaded, _choices.Values.ToList());
        foreach (var (_, clients) in _shown)
            foreach (var c in clients.Where(c => _choices.ContainsKey(c.Id) && c.Tickets > 0))
                _choices[c.Id].Name = c.Name; // keep the stored name current

        var visible = Layout(_shown, _choices.Values.ToList(), _filter);
        _list.Filtering = _filter.Length > 0;
        _list.SetGroups(visible.Select(s => new AssigneeList.Group(s.Title,
            s.Clients.Select(c => new AssigneeList.Person(c.Id, c.Name, TipFor(c),
                // Matched by a product, not the name: that product after the name says why it's in the list.
                Note: !c.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) && MatchedProducts(c, _filter) is { Count: > 0 } why
                    ? string.Join(", ", why) : null)).ToList(),
            StartOpen: s.Changed)).ToList()); // groups with muted or customized clients start open
        if (!keepScroll)
            _scroll = 0;
        LayoutList();

        if (groups.Count == 0)
            ShowMessage("Tick groups on the Groups page first: their clients show here.");
        else if (visible.Count == 0 && _filter.Length > 0 && _shown.Any(s => s.Clients.Count > 0))
            ShowMessage($"No client matches \"{_filter}\".");
        else if (visible.Count == 0)
            ShowMessage(_loaded ? "No tickets in these groups in the last 90 days." : "Sign in on the Account page to see the clients of your groups.");
        else
            _message.Visible = false;
        UpdateHint();
    }

    /// <summary>
    /// The list as shown: one foldable section per group (customized clients first, muted last, as sorted by
    /// <see cref="Sections"/>). <c>Changed</c>: it has muted or customized clients, so it starts open. A client in two
    /// groups shows in both; empty groups are left out. With a <paramref name="filter"/>, only clients whose name
    /// contains it (and groups with any).
    /// </summary>
    internal static List<(string Title, List<Client> Clients, bool Changed)> Layout(IReadOnlyList<(WatchedGroup Group, List<Client> Clients)> sections,
        IReadOnlyList<ClientChoice> choices, string filter = "")
    {
        var now = DateTimeOffset.UtcNow;
        var byId = choices.ToDictionary(c => c.Id);
        bool Changed(Client c) => byId.GetValueOrDefault(c.Id) is { } choice && !choice.IsDefaultAt(now);
        return sections
            .Select(s => (s.Group, Clients: filter.Length == 0 ? s.Clients
                : s.Clients.Where(c => Matches(c, filter)).ToList()))
            .Where(s => s.Clients.Count > 0)
            .Select(s => (s.Group.IsNoGroup ? "No group" : s.Group.Name, s.Clients, s.Clients.Any(Changed)))
            .ToList();
    }

    /// <summary>
    /// One section per group: clients with a choice, then other ticked ones, then muted, each by name with "(No client)"
    /// last. Clients with a choice the loaded lists don't
    /// have (no recent tickets, or not loaded) are added under the group the choice was made in, or the first group.
    /// </summary>
    internal static List<(WatchedGroup Group, List<Client> Clients)> Sections(IReadOnlyList<WatchedGroup> groups,
        IReadOnlyDictionary<long, List<Client>>? loaded, IReadOnlyList<ClientChoice> excluded)
    {
        var sections = groups.Select(g => (Group: g, Clients: (loaded?.GetValueOrDefault(g.Id) ?? []).ToList())).ToList();
        if (sections.Count == 0)
            return [];
        var listed = sections.SelectMany(s => s.Clients).Select(c => c.Id).ToHashSet();
        foreach (var client in excluded.Where(c => !listed.Contains(c.Id)))
        {
            var section = sections.FirstOrDefault(s => s.Group.Id == client.GroupId);
            (section.Group is null ? sections[0] : section).Clients.Add(new Client(client.Id, client.Name, 0));
        }
        // Clients with a choice (important, only urgent, own sound) first, then the other ticked ones, then muted; by
        // name, "(No client)" last in each. Sorted when the list is built, so ticking a box doesn't make it jump.
        var now = DateTimeOffset.UtcNow;
        var choices = excluded.ToDictionary(c => c.Id);
        int Rank(Client c) => choices.GetValueOrDefault(c.Id) is not { } choice ? 1
            : choice.ModeAt(now) == ClientMode.Muted ? 2
            : choice.IsDefaultAt(now) ? 1 : 0;
        return sections
            .Select(s => (s.Group, s.Clients
                .OrderBy(Rank)
                .ThenBy(c => c.Id == ClientChoice.NoClientId ? 1 : 0)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        _message.BringToFront();
    }

    private void UpdateHint()
    {
        var clients = _shown.SelectMany(s => s.Clients).DistinctBy(c => c.Id).ToList();
        var muted = clients.Count(c => ModeOf(c.Id) == ClientMode.Muted);
        var important = clients.Count(c => ModeOf(c.Id) == ClientMode.Important);
        var onlyUrgent = clients.Count(c => ModeOf(c.Id) == ClientMode.OnlyUrgent);
        var matching = _filter.Length == 0 ? 0 : clients.Count(c => Matches(c, _filter));
        var text = clients.Count == 0 ? ""
            : _filter.Length > 0
                ? (matching == 0 ? "" : $"{matching} of {clients.Count} clients match · a group's box applies to {(matching == 1 ? "this one" : $"these {matching}")}")
            : (muted == 0 ? $"All {clients.Count} clients notify" : $"{clients.Count - muted} of {clients.Count} clients notify")
              + (important > 0 ? $" · {important} important" : "") + (onlyUrgent > 0 ? $" · {onlyUrgent} only urgent" : "");
        if (_failed > 0)
            text += $" · could not load {_failed} group(s)";
        SetHint(text);
    }

    private void SetHint(string text)
    {
        Hint = text;
        HintChanged?.Invoke();
    }
}
