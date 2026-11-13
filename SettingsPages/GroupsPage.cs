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

/// <summary>Groups: the groups to monitor, in sections (monitored, suggested, the rest) with a filter.</summary>
internal sealed class GroupsPage : SettingsPage
{
    private const int ListWidth = Kit.CardWidth - 44;

    private readonly InputBox _filter = new(ListWidth, "Find a group…  (Enter: next)", searchIcon: true);
    private readonly SmoothListView _list = new()
    {
        Width = ListWidth, Height = 280, View = View.Details, CheckBoxes = true, HeaderStyle = ColumnHeaderStyle.None,
        FullRowSelect = true, MultiSelect = false, ShowGroups = true, BorderStyle = BorderStyle.None, HideSelection = false, // "find" selects while typing
        BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Body, Margin = new Padding(0, 10, 0, 10)
    };
    private readonly Label _loading = new()
    {
        Text = "Loading your groups…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Font = Theme.Body, ForeColor = Theme.TextMuted, BackColor = Theme.Surface, Visible = false
    };
    private readonly Panel _listHost = new() { BackColor = Theme.Surface };
    private readonly ThinScrollBar _scrollBar = new() { BackColor = Theme.Surface };

    private void LayoutList()
    {
        var s = _listHost.DeviceDpi / 96f;
        var bar = (int)(10 * s);
        _scrollBar.Bounds = new Rectangle(_listHost.Width - bar, 0, bar, _listHost.Height);
        _list.VisibleWidth = _listHost.Width - bar - (int)(4 * s); // the column stops before the themed bar: no sideways scrolling
        _list.Bounds = new Rectangle(0, 0, _listHost.Width - bar + SystemInformation.GetVerticalScrollBarWidthForDpi(_listHost.DeviceDpi), _listHost.Height);
        _scrollBar.BringToFront();
        _loading.BringToFront();
    }

    private readonly ListViewGroup _monitoredSection = new("Monitored");
    private readonly ListViewGroup _suggestedSection = new("Suggested");
    private readonly ListViewGroup _otherSection = new("Other groups");
    private readonly Label _hint = Theme.Label("Sign in to load your groups.", Theme.Small, Theme.TextMuted);
    private readonly Button _reload = Theme.SecondaryButton("Reload");

    private List<GroupItem> _allGroups = [];

    /// <summary>The account's groups have been loaded (signed in); until then only the saved ones are shown.</summary>
    private bool _groupsLoaded;

    private const string SignInText = "Sign in on the Account page to see your groups.";
    private readonly HashSet<long> _selected = [];
    private Dictionary<long, Suggestion> _suggestions = [];
    private bool _suppressEvents;

    /// <summary>Groups loaded from Zendesk, remembered for a day (until the app restarts) so reopening Settings is instant.</summary>
    private static (string Subdomain, DateTime At, List<GroupItem> Groups, Dictionary<long, Suggestion> Suggestions)? _cache;

    private const string CacheName = "groups";

    /// <summary>The groups and suggestions as saved in the data folder (<see cref="ListCache"/>).</summary>
    private sealed record SavedGroups(List<GroupItem> Groups, Dictionary<long, Suggestion> Suggestions);

    /// <summary>How long loaded groups, group members (Colleagues) and clients are reused; Reload fetches them again.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);

    /// <summary>Reload was clicked: the account is checked again and everything is fetched fresh.</summary>
    public event Action? ReloadRequested;

    /// <summary>The ticked groups, including <see cref="WatchedGroup.NoGroupId"/>.</summary>
    public IReadOnlySet<long> SelectedIds => _selected;

    public IReadOnlyDictionary<long, Suggestion> Suggestions => _suggestions;

    /// <summary>All groups known so far (loaded, or the saved ones until then).</summary>
    public IReadOnlyList<GroupItem> AllGroups => _allGroups;

    public GroupsPage(SettingsContext context)
        : base(context, "Groups", "The groups to monitor for new unassigned tickets. Their count shows on the tray icon. Your own tickets notify you whatever group they're in.")
    {
        var card = new Card("Monitored groups",
            $"Only groups visible to your account are listed. \"{WatchedGroup.NoGroupName}\" covers tickets that have neither a group nor an assignee. " +
            "Clients (below Groups in the sidebar) lists their clients, to untick the ones you don't need to hear about.", Kit.CardWidth);
        _list.Columns.Add("Group");
        _list.Groups.AddRange([_monitoredSection, _suggestedSection, _otherSection]);
        _listHost.Size = _list.Size;
        _listHost.Margin = _list.Margin;
        _listHost.Controls.Add(_loading); // overlay, shown while loading
        _listHost.Controls.Add(_scrollBar);
        _listHost.Controls.Add(_list);

        // Windows' scrollbar only comes in light or dark gray, which clashes with colored themes: the list is made
        // wider than its host so that one is cut off, and a thin themed scrollbar sits at the right edge instead.
        _scrollBar.State = _list.VerticalScrollState;
        _scrollBar.ScrollTo = _list.ScrollToPosition;
        _list.ScrollChanged += () => _scrollBar.Invalidate();
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
        _filter.TextChanged += (_, _) => Find(next: false);
        _filter.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true; // no beep
            Find(next: true);
        };
        _list.ItemChecked += (_, e) =>
        {
            if (_suppressEvents)
                return;
            var item = e.Item;
            var group = (GroupItem)item.Tag!;
            if (item.Checked) _selected.Add(group.Id); else _selected.Remove(group.Id);

            // The row stays where it is (moving it away from under the mouse was confusing): it only looks
            // monitored or not. The list is sorted into its sections again when the page is shown next time.
            _list.BeginInvoke(() => { RestyleInPlace(item); Changed(); });
            _needsSort = true;
        };
        _list.MouseClick += (_, e) =>
        {
            // Clicking anywhere on the row toggles it, not just the checkbox.
            var hit = _list.HitTest(e.Location);
            if (hit.Item is not null && hit.Location != ListViewHitTestLocations.StateImage)
                hit.Item.Checked = !hit.Item.Checked;
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
        if (_needsSort)
            ShowList(); // groups ticked or unticked last time move to their sections now
    }

    /// <summary>After Save or Discard: rows move to their sections, keeping the scroll position as far as possible.</summary>
    public void Resort()
    {
        if (!_needsSort)
            return;
        var position = _list.VerticalScrollState()?.Pos;
        ShowList();
        if (position is { } pos)
            _list.ScrollToPosition(pos);
        _scrollBar.Invalidate();
    }

    /// <summary>Groups were ticked or unticked since the list was last sorted into its sections.</summary>
    private bool _needsSort;

    /// <summary>A ticked or unticked row: monitored look (highlighted, bold) or not, without moving it.</summary>
    private void RestyleInPlace(ListViewItem item)
    {
        if (item.ListView != _list)
            return; // the list was rebuilt in the meantime
        var group = (GroupItem)item.Tag!;
        var monitored = _selected.Contains(group.Id);
        item.BackColor = monitored ? Theme.AccentSoft : Theme.Surface;
        item.Font = monitored ? Theme.BodyBold : Theme.Body;
        UpdateSectionHeaders();
        UpdateHint();
    }

    public void SetReloadEnabled(bool enabled) => _reload.Enabled = enabled;

    /// <summary>
    /// Loads the groups and suggestions for the signed-in account (reusing the last day's unless
    /// <paramref name="force"/>). Stops quietly when <paramref name="stale"/> says the account changed meanwhile.
    /// </summary>
    public async Task LoadAsync(ZendeskClient client, long meId, string subdomain, bool force, Func<bool> stale)
    {
        // Groups rarely change: reuse what was loaded in the last day, also after a restart (Reload always fetches).
        if (force)
            ListCache.Clear(CacheName);
        else if (_cache is null && ListCache.Load<SavedGroups>(CacheName, subdomain) is { } saved)
            _cache = (subdomain, saved.At, saved.Value.Groups, saved.Value.Suggestions);
        if (!force && _cache is { } cache && cache.Subdomain == subdomain && DateTime.UtcNow - cache.At < CacheLifetime)
        {
            _suggestions = cache.Suggestions;
            _groupsLoaded = true;
            SetGroups(WithMissing(cache.Groups));
            _reload.Enabled = true;
            return;
        }

        if (force)
            ClientsView.ClearCache();
        _reload.SetBusy(true, "Loading\u2026");
        try
        {
            await FetchAsync(client, meId, subdomain, stale);
        }
        finally
        {
            _reload.SetBusy(false);
        }
    }

    private async Task FetchAsync(ZendeskClient client, long meId, string subdomain, Func<bool> stale)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
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

    /// <summary>The last "find": the text and which match it's on (Enter goes to the next).</summary>
    private (string Text, int Index) _find = ("", -1);

    /// <summary>
    /// Goes to the first group whose name contains the text in the find box (the next one on Enter) and selects it;
    /// the whole list stays. The text turns orange when nothing matches.
    /// </summary>
    private void Find(bool next)
    {
        var text = _filter.Text.Trim();
        _filter.Box.ForeColor = Theme.Text;
        if (text.Length == 0)
        {
            _find = ("", -1);
            _list.SelectedItems.Clear();
            return;
        }
        // Display order: by section, then as listed in it.
        var sections = new[] { _monitoredSection, _suggestedSection, _otherSection };
        var matches = _list.Items.Cast<ListViewItem>()
            .Where(i => ((GroupItem)i.Tag!).Name.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(i => Array.IndexOf(sections, i.Group))
            .ThenBy(i => i.Index)
            .ToList();
        if (matches.Count == 0)
        {
            _filter.Box.ForeColor = Theme.Warning;
            _list.SelectedItems.Clear();
            return;
        }
        var index = next && _find.Text == text ? (_find.Index + 1) % matches.Count : 0;
        _find = (text, index);
        var item = matches[index];
        item.Selected = true;
        item.Focused = true;
        item.EnsureVisible();
        // Then centered: the list scrolls in pixels (it shows sections), so move by the row's distance from the middle.
        if (_list.VerticalScrollState() is { } state)
        {
            var bounds = item.Bounds;
            _list.ScrollToPosition(state.Pos + bounds.Top + bounds.Height / 2 - _listHost.Height / 2);
        }
        _scrollBar.Invalidate();
    }

    /// <summary>Monitored groups first (highlighted), then suggestions, then the rest.</summary>
    private void ShowList()
    {
        _needsSort = false;
        _allGroups.RemoveAll(g => g.Missing && !_selected.Contains(g.Id)); // gone from Zendesk and unticked: nowhere to go
        var ordered = _allGroups
            .OrderBy(g => SectionFor(g) == _monitoredSection ? 0 : SectionFor(g) == _suggestedSection ? 1 : 2)
            .ThenBy(g => g, Comparer<GroupItem>.Create((x, y) => CompareInSection(x, y, SectionFor(x))))
            .ToList();

        _suppressEvents = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var group in ordered)
        {
            var item = new ListViewItem { Tag = group };
            Style(item, group, SectionFor(group));
            _list.Items.Add(item);
        }
        UpdateSectionHeaders();
        _list.EndUpdate();
        _list.FitColumn();
        _suppressEvents = false;

        UpdateHint();
    }

    /// <summary>Monitored if ticked; suggested if it's one of your groups or has your tickets; otherwise the rest.</summary>
    private ListViewGroup SectionFor(GroupItem group) =>
        _selected.Contains(group.Id) ? _monitoredSection
        : !group.Missing && _suggestions.ContainsKey(group.Id) ? _suggestedSection
        : _otherSection;

    /// <summary>Suggestions: groups you're a member of first, then by how many of your tickets they have. Others: by name.</summary>
    private int CompareInSection(GroupItem a, GroupItem b, ListViewGroup section)
    {
        if (section == _suggestedSection && _suggestions.TryGetValue(a.Id, out var sa) && _suggestions.TryGetValue(b.Id, out var sb))
        {
            var byMember = sb.Member.CompareTo(sa.Member);
            if (byMember != 0) return byMember;
            var byTickets = sb.Tickets.CompareTo(sa.Tickets);
            if (byTickets != 0) return byTickets;
        }
        return _allGroups.IndexOf(a).CompareTo(_allGroups.IndexOf(b));
    }

    private void Style(ListViewItem item, GroupItem group, ListViewGroup section)
    {
        var monitored = section == _monitoredSection;
        item.Text = section == _suggestedSection && _suggestions.TryGetValue(group.Id, out var why)
            ? $"{group.Name}  ·  {why.Reason}"
            : group.DisplayName;
        item.Checked = monitored;
        item.Group = section;
        item.BackColor = monitored ? Theme.AccentSoft : Theme.Surface;
        item.Font = monitored ? Theme.BodyBold : Theme.Body;
        item.ForeColor = group.Missing ? Theme.TextMuted : Theme.Text;
    }

    /// <summary>"Other groups" only makes sense next to monitored or suggested ones; alone it's just "Groups".</summary>
    private void UpdateSectionHeaders()
    {
        var monitored = _monitoredSection.Items.Count;
        var suggested = _suggestedSection.Items.Count;
        var others = _otherSection.Items.Count;
        // How many are ticked, also before the rows have moved to their sections (on the next visit).
        _monitoredSection.Header = $"Monitored · {_selected.Count}";
        _suggestedSection.Header = $"Suggested · {suggested}";
        _otherSection.Header = monitored + suggested == 0 ? $"Groups · {others}" : $"Other groups · {others}";
    }

    /// <summary>Loads the suggestions with the group list; never fatal.</summary>
    private async Task LoadSuggestionsAsync(ZendeskClient client, long meId)
    {
        try
        {
            var memberOf = (await client.GetMyGroupIdsAsync(meId, CancellationToken.None)).ToHashSet();
            var myTickets = await client.GetMyOpenTicketsAsync(CancellationToken.None);
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
        _hint.Text = (realGroups <= 0
            ? $"{_selected.Count} monitored · sign in to load your groups"
            : $"{_selected.Count} monitored · {realGroups} groups available")
            + (missing > 0 ? $" · {missing} not found in Zendesk" : "");
    }
}
