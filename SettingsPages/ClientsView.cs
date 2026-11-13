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
    internal sealed record Client(long Id, string Name, int Tickets);

    private readonly AssigneeList _list;
    private readonly ThinScrollBar _scrollBar = new() { BackColor = Theme.Surface };
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Body, ForeColor = Theme.TextMuted,
        BackColor = Theme.Surface, Visible = false
    };
    private int _scroll;

    /// <summary>Clients with a choice (id -> choice): the only ones stored.</summary>
    private readonly Dictionary<long, ClientChoice> _choices = [];

    /// <summary>The last "find": the text and which match it's on (Enter goes to the next).</summary>
    private (string Text, int Index) _find = ("", -1);

    /// <summary>Clients per group loaded from Zendesk, remembered like the group list.</summary>
    private static (string Subdomain, DateTime At, Dictionary<long, List<Client>> Groups)? _cache;

    private int _version;
    private int _failed;
    private bool _loaded;
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
            Badge = id => _choices.GetValueOrDefault(id) is { } c ? BadgeFor(c) : null
        };
        _list.Changed += () => { UpdateHint(); Changed?.Invoke(); };
        _list.MenuRequested += (client, at) =>
            ClientMenu.Build(ChoiceFor(client), choice =>
            {
                Store(choice);
                // A deliberate choice: re-sort right away (tick boxes don't, so rows don't jump while ticking),
                // keep the scroll position and highlight the client where it is now.
                Arrange(_lastGroups, _loaded ? _cache?.Groups : null, keepScroll: true);
                HighlightClient(client.Id);
                Changed?.Invoke();
            }, AppearancePage.SoundOptions.Where(o => o.Value != ToastSound.Silent).Select(o => (o.Value, o.Label)).Prepend((ToastSound.Silent, "Silent")).ToList())
            .Show(at);
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
            ? $"muted until {end.ToLocalTime().ToString("ddd HH:mm", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))}"
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

    /// <summary>Sorts the list into its sections again (after Save or Discard), keeping the scroll position.</summary>
    public void Resort()
    {
        if (_lastGroups.Count > 0)
            Arrange(_lastGroups, _loaded ? _cache?.Groups : null, keepScroll: true);
    }

    /// <summary>While signed out: greyed, no ticking or client menu, still scrolls.</summary>
    public bool ReadOnly
    {
        get => _list.ReadOnly;
        set => _list.ReadOnly = value;
    }

    /// <summary>How many clients have a choice other than Normal (important, only urgent, muted, own sound).</summary>
    public int CustomizedCount => _choices.Values.Count(c => !c.IsDefaultAt(DateTimeOffset.UtcNow));

    /// <summary>"Clear customizations": every client back to Normal (Save keeps it, Discard brings them back).</summary>
    public void ClearAll()
    {
        _choices.Clear();
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
        ListCache.Clear(CacheName);
    }

    private const string CacheName = "clients";

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

        if (client is not null && groups.Count > 0)
        {
            if (_cache is null && ListCache.Load<Dictionary<long, List<Client>>>(CacheName, subdomain) is { } saved)
                _cache = (subdomain, saved.At, saved.Value); // from an earlier run
            if (_cache is not { } cache || cache.Subdomain != subdomain || DateTime.UtcNow - cache.At > GroupsPage.CacheLifetime)
                _cache = (subdomain, DateTime.UtcNow, []);
            var cached = _cache.Value.Groups;
            var missing = groups.Where(g => !cached.ContainsKey(g.Id)).ToList();
            if (missing.Count > 0)
            {
                ShowMessage("Loading the clients of your groups…");
                SetHint("Loading…");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                foreach (var group in missing)
                {
                    try
                    {
                        cached[group.Id] = (await client.GetGroupClientsAsync(group.Id, CancellationToken.None))
                            .Select(c => new Client(c.Id, c.Name, c.Tickets)).ToList();
                    }
                    catch (AuthRequiredException)
                    {
                        failed++;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Settings: could not load the clients of group {group.Id}", ex);
                        failed++;
                    }
                }
                Log.Info($"Settings: clients of {missing.Count - failed} group(s) in {clock.ElapsedMilliseconds} ms");
                ListCache.Save(CacheName, subdomain, _cache.Value.At, cached);
            }
            if (version != _version)
                return; // the groups, filter or session changed meanwhile
            loaded = cached;
        }
        else if (version != _version)
            return;

        _loaded = loaded is not null;
        _failed = failed;
        Arrange(groups, loaded);
    }

    /// <summary>
    /// Goes to the first client whose name contains <paramref name="text"/> (the next one when <paramref name="next"/>,
    /// e.g. Enter) and highlights it; the whole list stays. False when nothing matches.
    /// </summary>
    public bool Find(string text, bool next = false)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            _find = ("", -1);
            _list.Highlighted = Rectangle.Empty;
            return true;
        }
        var matches = _list.PersonRows().Where(r => r.Person.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (matches.Count == 0)
        {
            _list.Highlighted = Rectangle.Empty;
            return false;
        }
        var index = next && _find.Text == text ? (_find.Index + 1) % matches.Count : 0;
        _find = (text, index);
        var rect = matches[index].Rect;
        _list.Highlighted = rect;
        CenterOn(rect);
        return true;
    }

    /// <summary>The groups last shown, for re-sorting after a choice.</summary>
    private IReadOnlyList<WatchedGroup> _lastGroups = [];

    /// <summary>Highlights a client's row (its first one if it's in several groups) and brings it into view.</summary>
    private void HighlightClient(long id)
    {
        var row = _list.PersonRows().FirstOrDefault(r => r.Person.Id == id).Rect;
        _list.Highlighted = row;
        if (!row.IsEmpty)
            CenterOn(row);
    }

    /// <summary>Scrolls so the row is in the middle of the list (as far as the list allows).</summary>
    private void CenterOn(Rectangle row) => ScrollTo(row.Top + row.Height / 2 - Host.Height / 2);

    private void Arrange(IReadOnlyList<WatchedGroup> groups, Dictionary<long, List<Client>>? loaded, bool keepScroll = false)
    {
        _lastGroups = groups;
        _shown = Sections(groups, loaded, _choices.Values.ToList());
        foreach (var (_, clients) in _shown)
            foreach (var c in clients.Where(c => _choices.ContainsKey(c.Id) && c.Tickets > 0))
                _choices[c.Id].Name = c.Name; // keep the stored name current

        var visible = Layout(_shown, _choices.Values.ToList());
        _find = ("", -1);
        _list.SetGroups(visible.Select(s => new AssigneeList.Group(s.Title,
            s.Clients.Select(c => new AssigneeList.Person(c.Id, c.Name, c.Tickets > 0
                ? $"{c.Name} · {c.Tickets} ticket{(c.Tickets == 1 ? "" : "s")} in the last {ZendeskClient.ClientLookBackDays} days"
                : c.Name)).ToList())).ToList());
        if (!keepScroll)
            _scroll = 0;
        LayoutList();

        if (groups.Count == 0)
            ShowMessage("Tick groups on the Groups page first: their clients show here.");
        else if (visible.Count == 0)
            ShowMessage(_loaded ? "No tickets in these groups in the last 90 days." : "Sign in on the Account page to see the clients of your groups.");
        else
            _message.Visible = false;
        UpdateHint();
    }

    /// <summary>
    /// The list as shown, in sections like the Groups page: first "Premium Support · ★ Customized" (important, only
    /// urgent, own sound) for each group, then each group's other clients, then "Premium Support · Muted". A client in
    /// two groups shows in both. By name, "(No client)" last; empty sections are left out.
    /// </summary>
    internal static List<(string Title, List<Client> Clients)> Layout(IReadOnlyList<(WatchedGroup Group, List<Client> Clients)> sections,
        IReadOnlyList<ClientChoice> choices)
    {
        var now = DateTimeOffset.UtcNow;
        var byId = choices.ToDictionary(c => c.Id);
        bool Muted(Client c) => byId.GetValueOrDefault(c.Id)?.ModeAt(now) == ClientMode.Muted;
        bool Customized(Client c) => byId.GetValueOrDefault(c.Id) is { } choice && !Muted(c) && !choice.IsDefaultAt(now);
        static string Name(WatchedGroup g) => g.IsNoGroup ? "No group" : g.Name;

        var layout = new List<(string Title, List<Client> Clients)>();
        void Add(string title, IEnumerable<Client> clients)
        {
            var list = clients.ToList(); // already sorted by Sections
            if (list.Count > 0)
                layout.Add((title, list));
        }
        foreach (var (group, clients) in sections)
            Add($"{Name(group)} \u00b7 \u2605 Customized", clients.Where(Customized));
        foreach (var (group, clients) in sections)
            Add(Name(group), clients.Where(c => !Muted(c) && !Customized(c)));
        foreach (var (group, clients) in sections)
            Add($"{Name(group)} \u00b7 Muted", clients.Where(Muted));
        return layout;
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
        var text = clients.Count == 0 ? ""
            : (muted == 0 ? $"All {clients.Count} clients notify" : $"{clients.Count - muted} of {clients.Count} clients notify")
              + (important > 0 ? $" · {important} important" : "") + " · click a name for more";
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
