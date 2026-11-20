using ZendeskWatcher.Tracking;
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
    /// <param name="Handlers">Who the client's tickets in the group are assigned to, most first (none in older caches).</param>
    internal sealed record Client(long Id, string Name, int Tickets, List<string>? Products = null, List<ClientHandler>? Handlers = null);

    /// <summary>
    /// "Usually: Maria S. 12 · Alex K. 5 · you 3": the top 3 assignees of the client's tickets, and you (as "you") even when
    /// you're not among them. Null when no ticket is assigned.
    /// </summary>
    internal static string? UsuallyLine(IReadOnlyList<ClientHandler>? handlers, long? me)
    {
        if (handlers is not { Count: > 0 })
            return null;
        var top = handlers.Take(3).ToList();
        if (me is { } id && !top.Any(h => h.Id == id) && handlers.FirstOrDefault(h => h.Id == id) is { } mine)
            top.Add(mine);
        var shortNames = TeamWorkload.ShortNames(top.Where(h => h.Id != me).Select(h => h.Name));
        return "Usually: " + string.Join(" · ", top.Select(h => $"{(h.Id == me ? "you" : shortNames[h.Name])} {h.Tickets}"));
    }

    /// <summary>The signed-in agent (for "you" on the client card); null when unknown.</summary>
    public long? MeId { get; set; }

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
        if (UsuallyLine(c.Handlers, null) is { } usually)
            tip += $"\n{usually}";
        if (c.Products is { Count: > 0 } products)
            tip += $"\nProducts in Zendesk: {string.Join(" · ", products)}\n(may be out of date; bundled products aren't listed)";
        return tip;
    }

    private readonly AssigneeList _list;

    /// <summary>The client card shown while pointing at a client's name (made on first use), and what it's waiting to show.</summary>
    private ClientCard? _card;
    private readonly System.Windows.Forms.Timer _cardDelay = new() { Interval = 350 };
    private (Client Client, Rectangle Name)? _cardFor;

    /// <summary>The clients in the list, by id (for the card).</summary>
    /// <summary>
    /// The rows in view by their row id: a client in two groups has two rows, each with its own choice (choices are per
    /// group). Row ids are made up when the list is built; <c>GroupId</c> is the real group (also for an old one).
    /// </summary>
    private Dictionary<long, (long GroupId, string GroupTitle, Client Client)> _rows = [];

    private void HideCard()
    {
        _cardDelay.Stop();
        _cardFor = null;
        _card?.Hide();
    }
    private readonly ThinScrollBar _scrollBar = new() { BackColor = Theme.Surface };
    private readonly Label _message = new LinkedLabel
    {
        AutoSize = false,
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Body, ForeColor = Theme.TextMuted,
        BackColor = Theme.Surface, Visible = false
    };
    private int _scroll;

    /// <summary>Clients with a choice, per group (<see cref="ClientChoices"/>): the only ones stored.</summary>
    private readonly List<ClientChoice> _choices = [];

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

    private void WhenHostGoes()
    {
        // Another page, or the window closing: the card goes too.
        Host.VisibleChanged += (_, _) => { if (!Host.Visible) HideCard(); };
        Host.Disposed += (_, _) =>
        {
            _cardDelay.Dispose();
            _card?.Dispose();
        };
    }

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
            Badge = id => ChoiceOf(id) is { } c ? BadgeFor(c) : null,
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
        _list.GroupClearRequested += group =>
        {
            // A group no longer monitored: its clients' old choices are forgotten on Save (Discard brings them back).
            foreach (var row in group.People.Select(p => _rows.GetValueOrDefault(p.Id)).Where(r => r.Client is not null))
                _choices.RemoveAll(c => c.Id == row.Client.Id && c.GroupId == row.GroupId);
            Arrange(_lastGroups, _inView, keepScroll: true);
            UpdateHint();
            Changed?.Invoke();
        };
        WhenHostGoes();
        // Pointing at a client's name: its card beside it after a moment (products, tickets); gone when the mouse leaves.
        _list.NamePointed += (person, name) =>
        {
            if (person is null || !_rows.TryGetValue(person.Id, out var row) || row.Client is not { } client)
            {
                HideCard();
                return;
            }
            _cardFor = (client, name);
            if (_card is { Visible: true })
                ShowCard(); // moving from one name to the next: at once
            else
            {
                _cardDelay.Stop();
                _cardDelay.Start();
            }
        };
        _cardDelay.Tick += (_, _) =>
        {
            _cardDelay.Stop();
            ShowCard();
        };
        _list.FocusMoved += row =>
        {
            // Scroll just enough to show the keyboard's row.
            if (row.Top < _scroll)
                ScrollTo(row.Top);
            else if (row.Bottom > _scroll + Host.Height)
                ScrollTo(row.Bottom - Host.Height);
        };
        _list.MenuRequested += (client, at) => { HideCard(); ShowMenu(client, at); };
        void ShowMenu(AssigneeList.Person client, Point at) =>
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
            var menu = ClientMenu.Build(all, chosen =>
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
            }, AppearancePage.SoundOptions.Where(o => o.Value != ToastSound.Silent).Select(o => (o.Value, o.Label)).Prepend((ToastSound.Silent, "Silent")).ToList());
            menu.Show(at);
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
        if (group.People.Count == 0)
            return $"no tickets in the last {ZendeskClient.ClientLookBackDays} days";
        if (group.Locked)
        {
            var mutedOld = group.People.Count(p => ModeOf(p.Id) == ClientMode.Muted);
            var otherOld = group.People.Count - mutedOld;
            return "not monitored · " + string.Join(" · ", new[] { mutedOld > 0 ? $"{mutedOld} muted" : null, otherOld > 0 ? $"{otherOld} customized" : null }.OfType<string>());
        }
        var muted = group.People.Count(p => ModeOf(p.Id) == ClientMode.Muted);
        var customized = group.People.Count(p => ModeOf(p.Id) != ClientMode.Muted && ChoiceOf(p.Id) is { } c && !c.IsDefaultAt(DateTimeOffset.UtcNow));
        var text = group.People.Count == 1 ? "1 client" : $"{group.People.Count} clients";
        return text + (muted > 0 ? $" · {muted} muted" : "") + (customized > 0 ? $" · {customized} customized" : "");
    }


    /// <summary>The choice that applies to a row (the client in that row's group), if any.</summary>
    private ClientChoice? ChoiceOf(long rowId) =>
        _rows.TryGetValue(rowId, out var row) && row.Client is not null ? ClientChoices.Find(_choices, row.Client.Id, row.GroupId) : null;

    private ClientMode ModeOf(long rowId) => ChoiceOf(rowId)?.ModeAt(DateTimeOffset.UtcNow) ?? ClientMode.Normal;

    /// <summary>A copy of the row's choice for its group, or a new Normal one there.</summary>
    private ClientChoice ChoiceFor(AssigneeList.Person person)
    {
        var (groupId, title, client) = _rows[person.Id];
        var choice = ChoiceOf(person.Id)?.Copy() ?? new ClientChoice { Id = client.Id, Name = client.Name };
        choice.GroupId = groupId;
        choice.AllGroups = false;
        choice.GroupName = title;
        return choice;
    }

    /// <summary>Keeps a choice in its group, or forgets it when it's back to the defaults.</summary>
    private void Store(ClientChoice choice) => ClientChoices.Set(_choices, choice);

    /// <summary>
    /// What the row says at its right end: the client's choice ("★ Important", "P1, P2 only"; also while muted),
    /// "until Fri 14:00" for a mute for a while, "♪" for its own sound. In the accent color while it's important.
    /// </summary>
    internal static (string Text, bool Accent)? BadgeFor(ClientChoice choice)
    {
        var now = DateTimeOffset.UtcNow;
        var mode = choice.Mode switch
        {
            ClientMode.Important => "\u2605 Important",
            ClientMode.OnlyUrgent => "P1, P2 only",
            _ => null
        };
        var until = choice.IsMutedAt(now) && choice.MutedUntil is { } end
            ? $"muted until {Clock.DayTime(end.ToLocalTime().DateTime)}"
            : null;
        var text = string.Join("  ", new[] { mode, until, choice.Sound is null ? null : "\u266a" }.Where(t => t is not null));
        return text.Length == 0 ? null : (text, choice.ModeAt(now) == ClientMode.Important);
    }

    /// <summary>
    /// After the name: "you 6" when I've had their tickets in the group (90 days), and the product that matched the
    /// filter when the name didn't.
    /// </summary>
    private string? NoteFor(Client c)
    {
        var parts = new List<string>();
        if (MeId is { } me && c.Handlers?.FirstOrDefault(h => h.Id == me) is { } mine)
            parts.Add($"you {mine.Tickets}");
        if (!c.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) && MatchedProducts(c, _filter) is { Count: > 0 } why)
            parts.Add(string.Join(", ", why));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private void OnWheel(object? sender, MouseEventArgs e) =>
        ScrollTo(_scroll - e.Delta * SystemInformation.MouseWheelScrollLines * (int)(26 * Host.DeviceDpi / 96f) / 3 / 120);

    private void ShowCard()
    {
        if (_cardFor is not { } at || Host.FindForm() is not { Visible: true } window || !Host.Visible)
            return;
        _card ??= new ClientCard();
        _card.ShowFor(window, at.Name, at.Client.Name, at.Client.Tickets, ZendeskClient.ClientLookBackDays, at.Client.Products,
            MatchedProducts(at.Client, _filter), UsuallyLine(at.Client.Handlers, MeId));
    }

    private void ScrollTo(int position)
    {
        HideCard(); // the name moves away from its card
        _scroll = Math.Clamp(position, 0, Math.Max(0, _list.Height - Host.Height));
        _list.Top = -_scroll;
        _list.FadeAt = _scroll + Host.Height; // the bottom edge fades, as on Groups
        _list.Invalidate();
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
        _choices.AddRange(settings.Clients.Where(c => ClientChoices.Worth(settings.Clients, c, DateTimeOffset.UtcNow)).Select(c => c.Copy()));
        _list.Invalidate();
        UpdateHint();
    }

    public void Apply(Settings settings)
    {
        settings.Clients = _choices
            .Where(c => ClientChoices.Worth(_choices, c, DateTimeOffset.UtcNow))
            .Select(c => c.Copy())
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id).ThenBy(c => c.GroupId).ToList();
    }

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

    /// <summary>The choices (client in a group) in the list that aren't Normal.</summary>
    private List<ClientChoice> ShownCustomized()
    {
        var now = DateTimeOffset.UtcNow;
        return _shown.SelectMany(s => s.Clients.Select(c => ClientChoices.Find(_choices, c.Id, RealGroupId(s.Group.Id))))
            .OfType<ClientChoice>().Where(c => !c.IsDefaultAt(now)).Distinct().ToList();
    }

    /// <summary>"Clear customizations": the clients in the list back to Normal (Save keeps it, Discard brings them back).</summary>
    public void ClearAll()
    {
        var shown = _shown.SelectMany(s => s.Clients.Select(c => (c.Id, Group: RealGroupId(s.Group.Id)))).ToHashSet();
        _choices.RemoveAll(c => c.AllGroups ? shown.Any(r => r.Id == c.Id) : shown.Contains((c.Id, c.GroupId)));
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
        _digests = null; // Reload reads everything again, not just what changed
        ClientHistory.Clear();
    }

    private const string CacheName = "clients-handlers"; // renamed when products, then assignees came: older cached lists have none

    /// <summary>
    /// Groups whose clients are being fetched right now, per Zendesk account (a second request joins the first, also
    /// from a new connection after Save: a big group takes ~40 s, and loading it twice made it slower still).
    /// </summary>
    private static readonly Dictionary<(long Group, string Subdomain), Task<List<Client>?>> Loading = [];


    /// <summary>Opened again after this, the list reads what changed (new clients from today's tickets come in).</summary>
    private static readonly TimeSpan RefreshAfter = CacheAges.ClientsTopUp;

    /// <summary>Each group's tickets as last read (per account), so the next load only reads what changed.</summary>
    private static (string Subdomain, Dictionary<long, GroupDigest> Groups)? _digests;

    private static Dictionary<long, GroupDigest> DigestsFor(string subdomain)
    {
        if (_digests is not { } d || d.Subdomain != subdomain)
        {
            // Read on another thread when Settings opened (PreloadDigests): usually done by now, else waited for.
            var preloaded = _preload is { } p && p.Subdomain == subdomain ? p.Read.Result : null;
            _preload = null;
            _digests = (subdomain, preloaded ?? ClientHistory.Load(subdomain)); // shared with "Only clients I've worked with"
        }
        return _digests.Value.Groups;
    }

    private static (string Subdomain, Task<Dictionary<long, GroupDigest>> Read)? _preload;

    /// <summary>The groups monitored as last shown or counted: the saved records keep only these (<see cref="ClientHistory.Save"/>).</summary>
    private static HashSet<long>? _monitored;

    /// <summary>
    /// Settings opened: the saved tickets (cache\clients-digest.json, a few MB) are read on another thread, so Products and
    /// Clients don't spend ~60 ms reading them on screen the first time. UI thread only, like <see cref="DigestsFor"/>.
    /// </summary>
    internal static void PreloadDigests(string subdomain)
    {
        if (_digests is { } d && d.Subdomain == subdomain || _preload is { } p && p.Subdomain == subdomain || subdomain.Length == 0)
            return;
        _preload = (subdomain, Task.Run(() => ClientHistory.Load(subdomain)));
    }

    /// <summary>The groups' tickets as last read (no request), shared with the Clients list.</summary>
    internal static IReadOnlyDictionary<long, GroupDigest> SavedDigests(string subdomain) => DigestsFor(subdomain);

    /// <summary>
    /// Reads the groups' tickets (only what changed since the last read, usually one request per group) for My products'
    /// counts, and saves them for the Clients list too.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<long, GroupDigest>> ReadDigestsAsync(IEnumerable<long> groupIds, ZendeskClient client, string subdomain)
    {
        var digests = DigestsFor(subdomain);
        foreach (var id in groupIds)
            digests[id] = (await client.ReadGroupDigestAsync(id, digests.GetValueOrDefault(id), CancellationToken.None)).Digest;
        _monitored = [.. groupIds];
        ClientHistory.Save(subdomain, digests, _monitored);
        return digests;
    }

    /// <summary>A group's clients from Zendesk (only what changed since the last read); null when they couldn't be loaded.</summary>
    private static Task<List<Client>?> LoadGroupAsync(long groupId, ZendeskClient client, string subdomain)
    {
        if (Loading.TryGetValue((groupId, subdomain), out var running))
            return running;
        var task = Fetch();
        Loading[(groupId, subdomain)] = task;
        return task;

        async Task<List<Client>?> Fetch()
        {
            await Task.Yield(); // registered in Loading before it can finish
            try
            {
                var digests = DigestsFor(subdomain);
                // Names and products as last loaded (this group's list, fresh or older): only new clients are looked up.
                var known = new Dictionary<long, (string Name, List<string> Products)>();
                void Remember(Dictionary<long, List<Client>> groups)
                {
                    if (groups.TryGetValue(groupId, out var was))
                        foreach (var c in was)
                            known.TryAdd(c.Id, (c.Name, c.Products ?? []));
                }
                if (_cache is { } fresh && fresh.Subdomain == subdomain)
                    Remember(fresh.Groups);
                if (_old.Subdomain == subdomain)
                    Remember(_old.Groups);
                var (clients, digest) = await client.GetGroupClientsAsync(groupId, digests.GetValueOrDefault(groupId), known, CancellationToken.None);
                digests[groupId] = digest;
                ClientHistory.Save(subdomain, digests, _monitored);
                return clients.Select(c => new Client(c.Id, c.Name, c.Tickets, c.Products, c.Handlers)).ToList();
            }
            catch (Exception ex)
            {
                if (ex is not AuthRequiredException)
                    Log.Error($"Settings: could not load the clients of group {groupId}", ex);
                return null;
            }
            finally
            {
                Loading.Remove((groupId, subdomain));
            }
        }
    }

    /// <summary>For the screenshot tool: sample ticket records (My products' counts).</summary>
    internal static void SetSampleDigests(string subdomain, Dictionary<long, GroupDigest> digests) => _digests = (subdomain, digests);

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
        _monitored = [.. groups.Select(g => g.Id)];

        if (client is not null && groups.Count > 0)
        {
            if (_cache is null && ListCache.Load<Dictionary<long, List<Client>>>(CacheName, subdomain, anyAge: true) is { } saved)
                _cache = (subdomain, saved.At, saved.Value); // from an earlier run
            // Over 15 minutes old: shown at once while what changed is read (usually one quick request), and kept for a
            // group that fails to load. Only when the page is opened: people come here seldom.
            if (_old.Subdomain != subdomain)
                _old = (subdomain, []);
            var expired = _cache is { } aged && aged.Subdomain == subdomain && DateTime.UtcNow - aged.At > RefreshAfter;
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
                var results = await Task.WhenAll(missing.Select(async group => (group.Id, Clients: await LoadGroupAsync(group.Id, client, subdomain))));
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
        Refilter();
    }

    /// <summary>Which clients show: all, the ones I've worked with, or the others (a view only: nothing is muted by it).</summary>
    public enum Worked { All, Mine, NotMine }

    private Worked _worked;

    /// <summary>
    /// Shows all clients, only the ones I've had a ticket from in the group (90 days), or only the others. Like the name
    /// filter it only changes what's shown, so a group's tick box and Options ▾ then apply to those: e.g. "Not worked
    /// with", untick the group, tick back the few to keep.
    /// </summary>
    public void FilterWorked(Worked worked)
    {
        if (worked == _worked)
            return;
        _worked = worked;
        Refilter();
    }

    /// <summary>The client passes the worked-with filter (in the group it's listed under).</summary>
    private bool ShowsWorked(Client c) => _worked == Worked.All || (MeId is { } me && c.Handlers?.Any(h => h.Id == me) == true) == (_worked == Worked.Mine);

    private void Refilter()
    {
        _list.ForgetFilteredFolds();
        if (_lastGroups.Count > 0)
            Arrange(_lastGroups, _inView);
    }

    /// <summary>The groups last shown, for re-sorting after a choice.</summary>
    private IReadOnlyList<WatchedGroup> _lastGroups = [];

    private void Arrange(IReadOnlyList<WatchedGroup> groups, Dictionary<long, List<Client>>? loaded, bool keepScroll = false)
    {
        _lastGroups = groups;
        _shown = Sections(groups, loaded, _choices, GroupName);
        var names = _shown.SelectMany(s => s.Clients).Where(c => c.Tickets > 0).DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => c.Name);
        foreach (var choice in _choices)
            if (names.TryGetValue(choice.Id, out var name))
                choice.Name = name; // keep the stored name current

        // Unfiltered, a loaded group without clients still shows, with "no tickets in the last 90 days".
        var visible = Layout(_shown, _choices, _filter, ShowsWorked,
            _filter.Length == 0 && _worked == Worked.All && loaded is not null ? loaded.Keys.ToHashSet() : null);
        if (visible.All(s => s.Clients.Count == 0))
            visible = []; // nothing at all: the message below says why
        HideCard();
        _list.Filtering = _filter.Length > 0 || _worked != Worked.All;
        // One row per client and group, each with a made-up id that leads back to both (see _rows).
        _rows = [];
        var groupsInView = new List<AssigneeList.Group>();
        foreach (var s in visible)
        {
            var people = new List<AssigneeList.Person>();
            foreach (var c in s.Clients)
            {
                var rowId = _rows.Count + 1;
                _rows[rowId] = (RealGroupId(s.GroupId), s.Title, c);
                people.Add(new AssigneeList.Person(rowId, c.Name, TipFor(c), Note: NoteFor(c)));
            }
            groupsInView.Add(new AssigneeList.Group(s.Title, people, StartOpen: s.Changed && !s.Old, Locked: s.Old)); // groups with muted or customized clients start open
        }
        _list.SetGroups(groupsInView);
        if (!keepScroll)
            _scroll = 0;
        LayoutList();

        if (groups.Count == 0)
            ShowMessage("Tick groups on the Groups page first: their clients show here.");
        else if (visible.Count == 0 && _worked != Worked.All && _filter.Length == 0 && _shown.Any(s => s.Clients.Count > 0))
            ShowMessage(_worked == Worked.Mine ? "You haven't had tickets from these groups' clients in the last 90 days." : "You've worked with all of them.");
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
    /// groups shows in both; empty groups are left out, except those in <paramref name="keepEmpty"/> (loaded, no
    /// tickets in 90 days: shown with a note). With a <paramref name="filter"/>, only clients whose name contains it
    /// (and groups with any). <c>Old</c>: a group no longer monitored (<see cref="Sections"/>), shown greyed.
    /// </summary>
    internal static List<(string Title, List<Client> Clients, bool Changed, bool Old, long GroupId)> Layout(IReadOnlyList<(WatchedGroup Group, List<Client> Clients)> sections,
        IReadOnlyList<ClientChoice> choices, string filter = "", Func<Client, bool>? shows = null, IReadOnlySet<long>? keepEmpty = null)
    {
        var now = DateTimeOffset.UtcNow;
        bool Changed(Client c, long sectionId) => ClientChoices.Find(choices, c.Id, RealGroupId(sectionId)) is { } choice && !choice.IsDefaultAt(now);
        return sections
            .Select(s => (s.Group, Clients: filter.Length == 0 && shows is null ? s.Clients
                : s.Clients.Where(c => Matches(c, filter) && (shows?.Invoke(c) ?? true)).ToList()))
            .Where(s => s.Clients.Count > 0 || keepEmpty?.Contains(s.Group.Id) == true)
            .Select(s => (s.Group.IsNoGroup ? "No group" : s.Group.Name, s.Clients, s.Clients.Any(c => Changed(c, s.Group.Id)), s.Group.Id < 0, s.Group.Id))
            .ToList();
    }

    /// <summary>The real group of a section: its own id, or for an old group (<see cref="OldGroupId"/>) the id it stands for.</summary>
    internal static long RealGroupId(long sectionId) => sectionId < 0 ? -sectionId - 1 : sectionId;

    /// <summary>
    /// One section per group: clients with a choice, then other ticked ones, then muted, each by name with "(No client)"
    /// last. Clients with a choice in a group whose loaded list doesn't
    /// have them (no recent tickets, or not loaded) are added under that group. When that group isn't
    /// monitored any more, they get a section of their own after the others, as it was when monitored (an old choice
    /// for every group still counts everywhere). Its <see cref="WatchedGroup.Id"/> is the group's id negated (shown
    /// greyed, see <see cref="OldGroupId"/>), its name from <paramref name="groupName"/> ("Another group" when unknown).
    /// </summary>
    internal static List<(WatchedGroup Group, List<Client> Clients)> Sections(IReadOnlyList<WatchedGroup> groups,
        IReadOnlyDictionary<long, List<Client>>? loaded, IReadOnlyList<ClientChoice> excluded, Func<long, string?>? groupName = null)
    {
        var sections = groups.Select(g => (Group: g, Clients: (loaded?.GetValueOrDefault(g.Id) ?? []).ToList())).ToList();
        if (sections.Count == 0)
            return [];
        var listed = sections.SelectMany(s => s.Clients.Select(c => (c.Id, s.Group.Id))).ToHashSet();
        var anywhere = listed.Select(l => l.Item1).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var old = new List<(WatchedGroup Group, List<Client> Clients)>();
        // An old every-group choice only needs a row of its own when the client isn't in any list.
        foreach (var client in excluded.Where(c => !c.IsDefaultAt(now) && (c.AllGroups ? !anywhere.Contains(c.Id) : !listed.Contains((c.Id, c.GroupId))))
                     .DistinctBy(c => (c.Id, c.GroupId)))
        {
            var section = sections.FirstOrDefault(s => s.Group.Id == client.GroupId);
            if (section.Group is null)
            {
                section = old.FirstOrDefault(s => s.Group.Id == OldGroupId(client.GroupId));
                if (section.Group is null)
                {
                    var name = client.GroupId == WatchedGroup.NoGroupId ? "No group" : groupName?.Invoke(client.GroupId) ?? "Another group";
                    section = (new WatchedGroup { Id = OldGroupId(client.GroupId), Name = name }, []);
                    old.Add(section);
                }
            }
            section.Clients.Add(new Client(client.Id, client.Name, 0));
        }
        sections.AddRange(old.OrderBy(s => s.Group.Name, StringComparer.CurrentCultureIgnoreCase));
        // Clients with a choice (important, only urgent, own sound) first, then the other ticked ones, then muted; by
        // name, "(No client)" last in each. Sorted when the list is built, so ticking a box doesn't make it jump.
        int Rank(Client c, long sectionId) => ClientChoices.Find(excluded, c.Id, RealGroupId(sectionId)) is not { } choice ? 1
            : choice.ModeAt(now) == ClientMode.Muted ? 2
            : choice.IsDefaultAt(now) ? 1 : 0;
        return sections
            .Select(s => (s.Group, s.Clients
                .OrderBy(c => Rank(c, s.Group.Id))
                .ThenBy(c => c.Id == ClientChoice.NoClientId ? 1 : 0)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();
    }

    /// <summary>A group's name by id (all groups, also those not monitored), for the sections of old groups.</summary>
    public Func<long, string?> GroupName { get; set; } = _ => null;

    /// <summary>
    /// The section id of a group no longer monitored: its id negated, below every real one (Zendesk ids are positive;
    /// "No group" (0) becomes -1).
    /// </summary>
    internal static long OldGroupId(long groupId) => -groupId - 1;

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        _message.BringToFront();
    }

    private void UpdateHint()
    {
        // Per client (a client in two groups counts once): muted when muted in every group it's in.
        var now = DateTimeOffset.UtcNow;
        var modes = _shown.SelectMany(s => s.Clients.Select(c => (Client: c,
                Mode: ClientChoices.Find(_choices, c.Id, RealGroupId(s.Group.Id))?.ModeAt(now) ?? ClientMode.Normal)))
            .GroupBy(r => r.Client.Id).ToList();
        var clients = modes.Select(g => g.First().Client).ToList();
        var muted = modes.Count(g => g.All(r => r.Mode == ClientMode.Muted));
        var important = modes.Count(g => g.Any(r => r.Mode == ClientMode.Important));
        var onlyUrgent = modes.Count(g => g.Any(r => r.Mode == ClientMode.OnlyUrgent));
        var matching = _filter.Length == 0 ? 0 : clients.Count(c => Matches(c, _filter));
        var text = clients.Count == 0 ? ""
            : _filter.Length > 0
                ? (matching == 0 ? "" : $"{matching} of {clients.Count} clients match · a group's box applies to {(matching == 1 ? "this one" : $"these {matching}")}")
            : (muted == 0 ? $"All {clients.Count} clients notify" : $"{clients.Count - muted} of {clients.Count} clients notify")
              + (important > 0 ? $" · {important} important" : "") + (onlyUrgent > 0 ? $" · {onlyUrgent} only P1, P2" : "");
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
