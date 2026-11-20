using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <param name="Missing">Monitored, but not in the list loaded from Zendesk (deleted, or no longer visible to you).</param>
internal sealed record GroupItem(long Id, string Name, bool Missing = false)
{
    public string DisplayName => Missing ? $"{Name} (not found in Zendesk)" : Name;
    public override string ToString() => Name;
}

/// <summary>Why a group is suggested: you're a member, and/or some of your open tickets are in it.</summary>
internal sealed record Suggestion(bool Member, int Tickets)
{
    public string Reason => (Member, Tickets) switch
    {
        (true, 0) => "you're a member",
        (true, var n) => $"you're a member · {n} of your tickets",
        (false, 1) => "1 of your tickets",
        (false, var n) => $"{n} of your tickets"
    };
}

/// <summary>Groups: the groups to monitor, in sections (monitored, suggested, the rest); the filter box shows only matching ones.</summary>
internal sealed class GroupsPage : SettingsPage
{
    private const int ListWidth = Kit.CardWidth - 44;

    private readonly InputBox _filter = new(ListWidth, "Filter groups…", searchIcon: true);
    /// <summary>The groups, in the app's own list (as Clients and Colleagues): sections, tick boxes, hover, keyboard.</summary>
    private readonly AssigneeList _list;
    private readonly Label _loading = new LinkedLabel
    {
        AutoSize = false,
        Text = "Loading your groups…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Font = Theme.Body, ForeColor = Theme.TextMuted, BackColor = Theme.Surface, Visible = false
    };
    private readonly Panel _listHost = new() { BackColor = Theme.Surface, Size = new Size(ListWidth, 280), Margin = new Padding(0, 10, 0, 10) };
    private readonly ThinScrollBar _scrollBar = new() { BackColor = Theme.Surface };

    /// <summary>How far the list is scrolled (it moves up inside its host, as on Clients).</summary>
    private int _scroll;

    private void LayoutList()
    {
        var s = _listHost.DeviceDpi / 96f;
        var bar = (int)(10 * s);
        _scrollBar.Bounds = new Rectangle(_listHost.Width - bar, 0, bar, _listHost.Height);
        _list.Left = 0;
        _list.Width = _listHost.Width - bar - (int)(6 * s);
        ScrollTo(_scroll);
        _scrollBar.BringToFront();
        _loading.BringToFront();
    }

    private void ScrollTo(int position)
    {
        _scroll = Math.Clamp(position, 0, Math.Max(0, _list.Height - _listHost.Height));
        _list.Top = -_scroll;
        _list.FadeAt = _scroll + _listHost.Height; // the bottom edge fades (drawn by the list, so it moves along)
        _list.Invalidate();
        _scrollBar.Invalidate();
    }

    private void OnWheel(object? sender, MouseEventArgs e) =>
        ScrollTo(_scroll - e.Delta * SystemInformation.MouseWheelScrollLines * (int)(26 * _listHost.DeviceDpi / 96f) / 3 / 120);

    private enum Section { Monitored, Suggested, Other }

    /// <summary>The sections as shown (rows stay put when ticked; they move to their sections after Save or Discard).</summary>
    private List<(Section Section, List<GroupItem> Groups)> _shown = [];
    private readonly Label _hint = Theme.Label("Sign in to load your groups.", Theme.Small, Theme.TextMuted);
    private readonly Button _reload = Theme.SecondaryButton("Reload");

    private List<GroupItem> _allGroups = [];

    /// <summary>The account's groups have been loaded (signed in); until then only the saved ones are shown.</summary>
    private bool _groupsLoaded;

    private const string SignInText = "Sign in on the Account page to see your groups.";
    private readonly HashSet<long> _selected = [];
    private Dictionary<long, Suggestion> _suggestions = [];

    /// <summary>Groups loaded from Zendesk, remembered for a day (until the app restarts) so reopening Settings is instant.</summary>
    private static (string Subdomain, DateTime At, List<GroupItem> Groups, Dictionary<long, Suggestion> Suggestions)? _cache;

    private const string CacheName = "groups";

    /// <summary>The groups and suggestions as saved in the data folder (<see cref="ListCache"/>).</summary>
    private sealed record SavedGroups(List<GroupItem> Groups, Dictionary<long, Suggestion> Suggestions);

    /// <summary>How long loaded groups, group members (Colleagues) and clients are reused; Reload fetches them again.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    /// <summary>The group list itself changes rarely: reused for a week (Reload fetches it at once).</summary>
    public static readonly TimeSpan GroupsLifetime = TimeSpan.FromDays(7);

    /// <summary>Reload was clicked: the account is checked again and everything is fetched fresh.</summary>
    public event Action? ReloadRequested;

    /// <summary>The ticked groups, including <see cref="WatchedGroup.NoGroupId"/>.</summary>
    public IReadOnlySet<long> SelectedIds => _selected;

    public IReadOnlyDictionary<long, Suggestion> Suggestions => _suggestions;

    /// <summary>All groups known so far (loaded, or the saved ones until then).</summary>
    public IReadOnlyList<GroupItem> AllGroups => _allGroups;

    public GroupsPage(SettingsContext context)
        : base(context, "Groups", "Groups to watch for new unassigned tickets. Your own tickets notify you in any group.")
    {
        var card = new Card("Monitored groups",
            "To skip some clients: [[Groups → Clients|Clients]].", Kit.CardWidth); // "(No group)" is explained on How it works
        _list = new AssigneeList(ListWidth,
            isCovered: id => _selected.Contains(id),
            setCovered: (group, monitored) =>
            {
                // The row stays where it is (moving it away from under the mouse was confusing): only its tick
                // changes. The list is sorted into its sections again after Save or Discard, or next time.
                if (monitored) _selected.Add(group.Id); else _selected.Remove(group.Id);
                _needsSort = true;
            })
        {
            SingleColumn = true, PlainHeadings = true, HoverRows = true,
            IsDim = id => _allGroups.Any(g => g.Id == id && g.Missing) // not found in Zendesk any more
        };
        _list.Changed += () =>
        {
            SetListGroups(); // "Monitored · 4": the counts follow the ticks
            UpdateHint();
            Changed();
        };
        _listHost.Controls.Add(_loading); // overlay, shown while loading
        _listHost.Controls.Add(_scrollBar);
        _listHost.Controls.Add(_list);

        // The app's thin scrollbar; the list moves up and down inside its host (Windows draws nothing here).
        _scrollBar.State = () => (0, Math.Max(0, _list.Height - 1), _listHost.Height, _scroll);
        _scrollBar.ScrollTo = ScrollTo;
        _listHost.MouseWheel += OnWheel;
        _list.MouseWheel += OnWheel;
        _list.Resize += (_, _) => ScrollTo(_scroll);
        _list.FocusMoved += row =>
        {
            // Scroll just enough to show the keyboard's row.
            if (row.Top < _scroll)
                ScrollTo(row.Top);
            else if (row.Bottom > _scroll + _listHost.Height)
                ScrollTo(row.Bottom - _listHost.Height);
        };
        _listHost.Resize += (_, _) => LayoutList();
        LayoutList();
        card.Body.Controls.Add(_filter);
        card.Body.Controls.Add(_listHost);

        // Full card width, so the Reload button sits at the right edge.
        var footer = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, MinimumSize = new Size(ListWidth, 0), MaximumSize = new Size(ListWidth, 0),
            BackColor = Theme.Surface, Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _hint.Anchor = AnchorStyles.Left;
        _reload.Margin = Padding.Empty;
        _reload.Anchor = AnchorStyles.Right;
        footer.Controls.Add(_hint, 0, 0);
        footer.Controls.Add(_reload, 1, 0);
        card.Body.Controls.Add(footer);
        Panel.Controls.Add(card);

        _reload.Click += (_, _) => ReloadRequested?.Invoke();
        _filter.TextChanged += (_, _) =>
        {
            ShowList(); // only the matching groups, sorted into their sections
            ScrollTo(0);
        };
        UseSignedOutStrip(_ => ColorAll());
        context.SignedOutChanged += () =>
        {
            if (Context.SignedOut)
                ShowCached();
        };
    }

    public override void Load(Settings settings)
    {
        // Show the saved groups until the real list is loaded, so they are not lost when offline. Cleared first:
        // Discard must also untick groups ticked since the last save.
        _selected.Clear();
        foreach (var group in settings.WatchedGroups)
            _selected.Add(group.Id);
        if (_groupsLoaded)
        {
            ShowList(); // Discard: the loaded list stays, only the ticks go back
            return;
        }
        SetGroups(settings.WatchedGroups.Where(g => !g.IsNoGroup).Select(g => new GroupItem(g.Id, g.Name)));
        _reload.Enabled = false;
        if (Context.SignedOut)
            ShowCached();
    }

    /// <summary>
    /// Signed out: the list as last loaded (whatever its age; the strip says it may be out of date), so the whole
    /// list shows instead of only the ticked groups. Signing in loads it again as usual.
    /// </summary>
    private void ShowCached()
    {
        if (_groupsLoaded)
            return; // loaded in this window already: that list stays
        var subdomain = Context.Current().Subdomain;
        if (_cache is null && ListCache.Load<SavedGroups>(CacheName, subdomain, anyAge: true) is { } saved)
            _cache = (subdomain, saved.At, saved.Value.Groups, saved.Value.Suggestions);
        if (_cache is not { } cache || cache.Subdomain != subdomain)
            return;
        _suggestions = cache.Suggestions;
        SetGroups(WithMissing(cache.Groups));
    }

    /// <summary>The ticked groups as they are right now, in the order the Clients page shows them.</summary>
    public List<WatchedGroup> MonitoredGroups()
    {
        var names = _allGroups.ToDictionary(g => g.Id, g => g.Name);
        return _selected
            .Select(id => new WatchedGroup { Id = id, Name = id == WatchedGroup.NoGroupId ? WatchedGroup.NoGroupName : names.GetValueOrDefault(id, $"Group {id}") })
            .OrderBy(g => g.IsNoGroup ? 1 : 0)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }


    public override void Apply(Settings settings)
    {
        var names = _allGroups.ToDictionary(g => g.Id, g => g.Name);
        names[WatchedGroup.NoGroupId] = WatchedGroup.NoGroupName;
        settings.WatchedGroups = _selected
            .Select(id => new WatchedGroup { Id = id, Name = names.GetValueOrDefault(id, $"Group {id}") })
            .OrderBy(g => g.Id == WatchedGroup.NoGroupId ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Sizes the group list so the page fits the window: only the list scrolls, not the page.</summary>
    public override void OnShowing()
    {
        _list.Invalidate(); // the theme may have changed on the General page
        FitList();
        if (_needsSort)
            ShowList(); // groups ticked or unticked last time move to their sections now
    }

    public override void OnWindowResized() => FitList();

    /// <summary>The list as tall as the window allows (at least 120 px), so the page itself doesn't scroll.</summary>
    private void FitList()
    {
        var content = Context.Content;
        Panel.PerformLayout();
        var available = content.ClientSize.Height - content.Padding.Vertical;
        var minimum = (int)(120 * Context.Window.DeviceDpi / 96f);
        var height = Math.Max(minimum, _listHost.Height + available - Panel.PreferredSize.Height);
        if (height != _listHost.Height)
        {
            _listHost.Height = height;
            Panel.PerformLayout();
        }
    }

    /// <summary>For the rendered screenshots: a row as if the mouse were over it.</summary>
    internal void ShowHoverForRender(int index) => _list.PointAtForRender(index);

    /// <summary>After Save or Discard: the filter starts empty again (all groups, in their new sections).</summary>
    public void ClearFind() => _filter.Text = "";

    /// <summary>After Save or Discard: rows move to their sections, shown from the top (like Clients): the monitored groups lead.</summary>
    public void Resort()
    {
        if (!_needsSort)
            return;
        ShowList();
        ScrollTo(0);
    }

    /// <summary>Groups were ticked or unticked since the list was last sorted into its sections.</summary>
    private bool _needsSort;

    public void SetReloadEnabled(bool enabled) => _reload.Enabled = enabled;

    /// <summary>
    /// Loads the groups and suggestions for the signed-in account (reusing the last week's unless
    /// <paramref name="force"/>). Stops quietly when <paramref name="stale"/> says the account changed meanwhile.
    /// </summary>
    public async Task LoadAsync(ZendeskClient client, long meId, string subdomain, bool force, Func<bool> stale)
    {
        // Groups rarely change: reuse what was loaded in the last week, also after a restart (Reload always fetches).
        if (force)
            ListCache.Clear(CacheName);
        else if (_cache is null && ListCache.Load<SavedGroups>(CacheName, subdomain, anyAge: true) is { } saved)
            _cache = (subdomain, saved.At, saved.Value.Groups, saved.Value.Suggestions);
        var old = false;
        if (!force && _cache is { } cache && cache.Subdomain == subdomain)
        {
            _suggestions = cache.Suggestions;
            _groupsLoaded = true;
            SetGroups(WithMissing(cache.Groups));
            _reload.Enabled = true;
            if (DateTime.UtcNow - cache.At < GroupsLifetime)
                return;
            old = true; // over a week old: it stays in view while a fresh one loads
        }

        if (force)
            ClientsView.ClearCache();
        _reload.SetBusy(true, "Loading\u2026");
        try
        {
            await FetchAsync(client, meId, subdomain, stale, quiet: old);
        }
        finally
        {
            _reload.SetBusy(false);
        }
    }

    /// <summary>Fetches the groups and suggestions; <paramref name="quiet"/> keeps the list in view meanwhile.</summary>
    private async Task FetchAsync(ZendeskClient client, long meId, string subdomain, Func<bool> stale, bool quiet = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (!quiet)
            SetLoading(true);
        var groups = (await client.GetGroupsAsync(CancellationToken.None)).Select(g => new GroupItem(g.Id, g.Name)).ToList();
        if (stale())
            return;
        _groupsLoaded = true;
        SetGroups(WithMissing(groups));
        SetLoading(false);
        _reload.Enabled = true;
        var groupsMs = clock.ElapsedMilliseconds;

        // Suggestions (needs a ticket search, the slowest part) fill in afterwards.
        await LoadSuggestionsAsync(client, meId);
        if (stale())
            return;
        ShowList();
        _cache = (subdomain, DateTime.UtcNow, groups, _suggestions);
        ListCache.Save(CacheName, subdomain, _cache.Value.At, new SavedGroups(groups, _suggestions));
        Log.Info($"Settings: groups {groupsMs} ms, suggestions {clock.ElapsedMilliseconds - groupsMs} ms");
    }

    public void SetLoading(bool loading)
    {
        _loading.Text = "Loading your groups\u2026";
        _loading.Visible = loading;
        _list.Visible = !loading;
        if (loading)
            _loading.BringToFront();
        _filter.Enabled = !loading;
        if (loading)
            _hint.Text = "Loading…";
        else
        {
            UpdateHint();
            ShowSignInIfEmpty();
        }
    }

    /// <summary>Not signed in and nothing saved: the list says how to get groups instead of standing empty.</summary>
    private void ShowSignInIfEmpty()
    {
        var empty = !_groupsLoaded && _allGroups.Count == 0;
        if (empty)
        {
            _loading.Text = SignInText;
            _loading.Visible = true;
            _loading.BringToFront();
        }
        else if (_loading.Text == SignInText)
            _loading.Visible = false;
    }

    /// <summary>For the screenshot tool: sample groups and suggestions, as if loaded.</summary>
    internal void ShowSample(Dictionary<long, Suggestion> suggestions, IEnumerable<GroupItem> groups)
    {
        _suggestions = suggestions;
        _groupsLoaded = true;
        SetGroups(groups);
    }

    /// <summary>
    /// Adds monitored groups that Zendesk didn't return, flagged, so they can be unticked. Not removed automatically:
    /// an agent who may only list assignable groups won't see every group.
    /// </summary>
    private List<GroupItem> WithMissing(List<GroupItem> loaded)
    {
        var loadedIds = loaded.Select(g => g.Id).ToHashSet();
        var missing = _allGroups
            .Where(g => g.Id != WatchedGroup.NoGroupId && _selected.Contains(g.Id) && !loadedIds.Contains(g.Id))
            .Select(g => g with { Missing = true })
            .ToList();
        if (missing.Count > 0)
            Log.Info($"Settings: monitored group(s) not found in Zendesk: {string.Join(", ", missing.Select(g => $"{g.Name} ({g.Id})"))}");
        return loaded.Concat(missing).ToList();
    }

    /// <summary>
    /// Sets the available groups, with the "(No group)" entry first: once the account's groups are loaded, or when
    /// it's ticked already (so it can be unticked). Before signing in it would be the only entry, which looks odd.
    /// </summary>
    private void SetGroups(IEnumerable<GroupItem> groups)
    {
        var sorted = groups
            .Where(g => g.Id != WatchedGroup.NoGroupId)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
        _allGroups = (_groupsLoaded || _selected.Contains(WatchedGroup.NoGroupId)
            ? sorted.Prepend(new GroupItem(WatchedGroup.NoGroupId, WatchedGroup.NoGroupName))
            : sorted).ToList();
        ShowList();
        ShowSignInIfEmpty();
    }

    /// <summary>The filter box's text, trimmed ("" shows every group).</summary>
    private string FilterText => _filter.Text.Trim();

    private bool Matches(GroupItem group) =>
        FilterText.Length == 0 || group.Name.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase);

    private const string NoMatchPrefix = "No group matches";

    /// <summary>The list says so when the filter matches nothing (over the empty list, like the loading text).</summary>
    private void ShowNoMatch()
    {
        var none = FilterText.Length > 0 && _shown.Sum(sec => sec.Groups.Count) == 0 && _allGroups.Count > 0;
        if (none)
        {
            _loading.Text = $"{NoMatchPrefix} \"{FilterText}\".";
            _loading.Visible = true;
            _loading.BringToFront();
        }
        else if (_loading.Text.StartsWith(NoMatchPrefix, StringComparison.Ordinal))
            _loading.Visible = false;
    }

    /// <summary>Monitored groups first, then suggestions, then the rest.</summary>
    private void ShowList()
    {
        _needsSort = false;
        _allGroups.RemoveAll(g => g.Missing && !_selected.Contains(g.Id)); // gone from Zendesk and unticked: nowhere to go
        _shown = _allGroups
            .Where(Matches)
            .GroupBy(SectionFor)
            .OrderBy(sec => sec.Key)
            .Select(sec => (sec.Key, sec.OrderBy(g => g, Comparer<GroupItem>.Create((x, y) => CompareInSection(x, y, sec.Key))).ToList()))
            .ToList();
        SetListGroups();
        UpdateHint();
        ShowNoMatch();
    }

    /// <summary>The shown sections into the list, with their counts ("Monitored · 3"); the rows keep their order.</summary>
    private void SetListGroups()
    {
        int Count(Section section) => _shown.Where(sec => sec.Section == section).Sum(sec => sec.Groups.Count);
        var monitored = Count(Section.Monitored);
        var suggested = Count(Section.Suggested);
        string Title(Section section, int count) => section switch
        {
            // How many are ticked, also before the rows have moved to their sections (on the next visit).
            Section.Monitored => $"Monitored · {(FilterText.Length > 0 ? count : _selected.Count)}",
            Section.Suggested => $"Suggested · {count}",
            // "Other groups" only makes sense next to monitored or suggested ones; alone it's just "Groups".
            _ => monitored + suggested == 0 ? $"Groups · {count}" : $"Other groups · {count}"
        };
        _list.SetGroups(_shown.Select(sec => new AssigneeList.Group(Title(sec.Section, sec.Groups.Count),
            sec.Groups.Select(g => new AssigneeList.Person(g.Id, sec.Section == Section.Suggested ? g.Name : g.DisplayName, Tip: null,
                Note: sec.Section == Section.Suggested && _suggestions.TryGetValue(g.Id, out var why) ? why.Reason : null)).ToList(),
            Tag: sec.Section)).ToList());
        _list.ReadOnly = Context.SignedOut;
        ScrollTo(_scroll);
    }

    /// <summary>Monitored if ticked; suggested if it's one of your groups or has your tickets; otherwise the rest.</summary>
    private Section SectionFor(GroupItem group) =>
        _selected.Contains(group.Id) ? Section.Monitored
        : !group.Missing && _suggestions.ContainsKey(group.Id) ? Section.Suggested
        : Section.Other;

    /// <summary>Suggestions: groups you're a member of first, then by how many of your tickets they have. Others: by name.</summary>
    private int CompareInSection(GroupItem a, GroupItem b, Section section)
    {
        if (section == Section.Suggested && _suggestions.TryGetValue(a.Id, out var sa) && _suggestions.TryGetValue(b.Id, out var sb))
        {
            var byMember = sb.Member.CompareTo(sa.Member);
            if (byMember != 0) return byMember;
            var byTickets = sb.Tickets.CompareTo(sa.Tickets);
            if (byTickets != 0) return byTickets;
        }
        return _allGroups.IndexOf(a).CompareTo(_allGroups.IndexOf(b));
    }

    /// <summary>Signed out: greyed and read-only (the strip says why); the list still scrolls.</summary>
    private void ColorAll()
    {
        _list.ReadOnly = Context.SignedOut;
        _list.Invalidate();
    }

    /// <summary>Loads the suggestions with the group list; never fatal.</summary>
    private async Task LoadSuggestionsAsync(ZendeskClient client, long meId)
    {
        try
        {
            var memberOfTask = client.GetMyGroupIdsAsync(meId, CancellationToken.None); // both at once
            var myTicketsTask = client.GetMyOpenTicketsAsync(CancellationToken.None);
            var memberOf = (await memberOfTask).ToHashSet();
            var myTickets = await myTicketsTask;
            var ticketCounts = myTickets.Where(t => t.GroupId is not null).GroupBy(t => t.GroupId!.Value).ToDictionary(g => g.Key, g => g.Count());

            _suggestions = memberOf.Union(ticketCounts.Keys)
                .ToDictionary(id => id, id => new Suggestion(memberOf.Contains(id), ticketCounts.GetValueOrDefault(id)));
            Log.Info($"Settings: {_suggestions.Count} suggested group(s) ({memberOf.Count} memberships, tickets in {ticketCounts.Count} group(s))");
        }
        catch (Exception ex) when (ex is not AuthRequiredException)
        {
            Log.Error("Settings: could not load group suggestions", ex);
            _suggestions = [];
        }
    }

    private void UpdateHint()
    {
        var realGroups = _allGroups.Count(g => g.Id != WatchedGroup.NoGroupId && !g.Missing);
        var missing = _allGroups.Count(g => g.Missing);
        if (FilterText.Length > 0 && realGroups > 0)
        {
            var matching = _allGroups.Count(g => g.Id != WatchedGroup.NoGroupId && !g.Missing && Matches(g));
            _hint.Text = matching == 0 ? $"{_selected.Count} monitored" : $"{matching} of {realGroups} groups match · {_selected.Count} monitored";
            return;
        }
        _hint.Text = (realGroups <= 0
            ? $"{_selected.Count} monitored · sign in to load your groups"
            : $"{_selected.Count} monitored · {realGroups} groups available")
            + (missing > 0 ? $" · {missing} not found in Zendesk" : "");
    }
}
