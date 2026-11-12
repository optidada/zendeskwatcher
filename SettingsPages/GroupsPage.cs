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

    private readonly InputBox _filter = new(ListWidth, "Filter groups…", searchIcon: true);
    private readonly SmoothListView _list = new()
    {
        Width = ListWidth, Height = 280, View = View.Details, CheckBoxes = true, HeaderStyle = ColumnHeaderStyle.None,
        FullRowSelect = true, MultiSelect = false, ShowGroups = true, BorderStyle = BorderStyle.None,
        BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Body, Margin = new Padding(0, 10, 0, 10)
    };
    private readonly Label _loading = new()
    {
        Text = "Loading your groups…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Font = Theme.Body, ForeColor = Theme.TextMuted, BackColor = Theme.Surface, Visible = false
    };
    private readonly Panel _listHost = new() { BackColor = Theme.Surface };
    private readonly ListViewGroup _monitoredSection = new("Monitored");
    private readonly ListViewGroup _suggestedSection = new("Suggested");
    private readonly ListViewGroup _otherSection = new("Other groups");
    private readonly Label _hint = Theme.Label("Sign in to load your groups.", Theme.Small, Theme.TextMuted);
    private readonly Button _reload = Theme.SecondaryButton("Reload");

    private List<GroupItem> _allGroups = [];
    private readonly HashSet<long> _selected = [];
    private Dictionary<long, Suggestion> _suggestions = [];
    private bool _suppressEvents;

    /// <summary>Groups loaded from Zendesk, remembered for this app session so reopening Settings is instant.</summary>
    private static (string Subdomain, DateTime At, List<GroupItem> Groups, Dictionary<long, Suggestion> Suggestions)? _cache;

    /// <summary>How long loaded groups (and, on the Notifications page, group members) are reused.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Reload was clicked: the account is checked again and everything is fetched fresh.</summary>
    public event Action? ReloadRequested;

    /// <summary>The ticked groups, including <see cref="WatchedGroup.NoGroupId"/>.</summary>
    public IReadOnlySet<long> SelectedIds => _selected;

    public IReadOnlyDictionary<long, Suggestion> Suggestions => _suggestions;

    /// <summary>All groups known so far (loaded, or the saved ones until then).</summary>
    public IReadOnlyList<GroupItem> AllGroups => _allGroups;

    public GroupsPage(SettingsContext context)
        : base(context, "Groups", "The groups to monitor for new unassigned tickets. Their count shows on the tray icon. Your own tickets notify you whatever group they're in.", composited: false)
    {
        var card = new Card("Monitored groups",
            $"Only groups visible to your account are listed. \"{WatchedGroup.NoGroupName}\" covers tickets that have neither a group nor an assignee. " +
            "Whether you get notifications is set under Notifications.", Kit.CardWidth);
        _list.Columns.Add("Group");
        _list.Groups.AddRange([_monitoredSection, _suggestedSection, _otherSection]);
        _listHost.Size = _list.Size;
        _listHost.Margin = _list.Margin;
        _list.Dock = DockStyle.Fill;
        _listHost.Controls.Add(_loading); // overlay, shown while loading
        _listHost.Controls.Add(_list);
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
        _filter.TextChanged += (_, _) => ShowList();
        _list.ItemChecked += (_, e) =>
        {
            if (_suppressEvents)
                return;
            var item = e.Item;
            var group = (GroupItem)item.Tag!;
            if (item.Checked) _selected.Add(group.Id); else _selected.Remove(group.Id);

            // Move the row to its section after the event. Only this row moves, so the list doesn't jump.
            _list.BeginInvoke(() => { MoveItem(item); Changed(); });
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
        // Show the saved groups until the real list is loaded, so they are not lost when offline.
        foreach (var group in settings.WatchedGroups)
            _selected.Add(group.Id);
        SetGroups(settings.WatchedGroups.Where(g => !g.IsNoGroup).Select(g => new GroupItem(g.Id, g.Name)));
        _reload.Enabled = false;
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
    }

    public void SetReloadEnabled(bool enabled) => _reload.Enabled = enabled;

    /// <summary>
    /// Loads the groups and suggestions for the signed-in account (reusing the last half hour's unless
    /// <paramref name="force"/>). Stops quietly when <paramref name="stale"/> says the account changed meanwhile.
    /// </summary>
    public async Task LoadAsync(ZendeskClient client, long meId, string subdomain, bool force, Func<bool> stale)
    {
        // Groups rarely change: reuse what was loaded in the last half hour (Reload always fetches).
        if (!force && _cache is { } cache && cache.Subdomain == subdomain && DateTime.UtcNow - cache.At < CacheLifetime)
        {
            _suggestions = cache.Suggestions;
            SetGroups(WithMissing(cache.Groups));
            _reload.Enabled = true;
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        SetLoading(true);
        var groups = (await client.GetGroupsAsync(CancellationToken.None)).Select(g => new GroupItem(g.Id, g.Name)).ToList();
        if (stale())
            return;
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
        Log.Info($"Settings: groups {groupsMs} ms, suggestions {clock.ElapsedMilliseconds - groupsMs} ms");
    }

    public void SetLoading(bool loading)
    {
        _loading.Visible = loading;
        _list.Visible = !loading;
        if (loading)
            _loading.BringToFront();
        _filter.Enabled = !loading;
        if (loading)
            _hint.Text = "Loading…";
        else
            UpdateHint();
    }

    /// <summary>For the screenshot tool: sample groups and suggestions, as if loaded.</summary>
    internal void ShowSample(Dictionary<long, Suggestion> suggestions, IEnumerable<GroupItem> groups)
    {
        _suggestions = suggestions;
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

    /// <summary>Sets the available groups; the "(No group)" entry is always listed first.</summary>
    private void SetGroups(IEnumerable<GroupItem> groups)
    {
        _allGroups = groups
            .Where(g => g.Id != WatchedGroup.NoGroupId)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(new GroupItem(WatchedGroup.NoGroupId, WatchedGroup.NoGroupName))
            .ToList();
        ShowList();
    }

    /// <summary>Monitored groups first (highlighted), then suggestions, then the rest; all filtered by the search box.</summary>
    private void ShowList()
    {
        var filter = _filter.Text.Trim();
        var visible = _allGroups.Where(g => filter.Length == 0 || g.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var ordered = visible
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

    /// <summary>Moves one row to its section, in order, without rebuilding the list (a rebuild resets the scroll position).</summary>
    private void MoveItem(ListViewItem item)
    {
        if (item.ListView != _list)
            return; // the list was rebuilt in the meantime

        var group = (GroupItem)item.Tag!;
        var section = SectionFor(group);

        _suppressEvents = true;
        _list.BeginUpdate();
        _list.Items.Remove(item);

        if (group.Missing && section != _monitoredSection)
        {
            // A group that no longer exists has nowhere to go once unticked.
            _allGroups.Remove(group);
        }
        else
        {
            Style(item, group, section);
            // Rows show in collection order within their section.
            var index = _list.Items.Cast<ListViewItem>().ToList()
                .FindIndex(i => i.Group == section && CompareInSection(group, (GroupItem)i.Tag!, section) < 0);
            _list.Items.Insert(index < 0 ? _list.Items.Count : index, item);
        }

        UpdateSectionHeaders();
        _list.EndUpdate();
        _suppressEvents = false;

        UpdateHint();
    }

    /// <summary>"Other groups" only makes sense next to monitored or suggested ones; alone it's just "Groups".</summary>
    private void UpdateSectionHeaders()
    {
        var monitored = _monitoredSection.Items.Count;
        var suggested = _suggestedSection.Items.Count;
        var others = _otherSection.Items.Count;
        _monitoredSection.Header = $"Monitored · {monitored}";
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
