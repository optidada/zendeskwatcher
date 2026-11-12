using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// The "Help colleagues" card (its own page): which groups and assignees to cover, priorities and hours.
/// Uses the monitored groups and suggestions from the Groups page, and the session to load group members.
/// </summary>
internal sealed class HelpColleaguesCard
{
    private const int PeopleWidth = Kit.CardWidth - 120;

    /// <summary>A group member offered under "Assignees". Zone: their IANA time zone, null if it can't be used.</summary>
    internal sealed record Member(long Id, string Name, string? Zone)
    {
        public TimeRegion Region => TimeRegions.Of(Zone);
    }

    private readonly SettingsContext _context;
    private readonly GroupsPage _groups;

    private readonly ToggleSwitch _help = new("Customer replies on colleagues' tickets outside their working hours");
    private readonly RadioButton _same = Kit.Radio("Same as my monitored groups");
    private readonly RadioButton _custom = Kit.Radio("Only these groups:");
    private readonly FlowLayoutPanel _groupList = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(20, 0, 0, 4)
    };
    private readonly AssigneeList _peopleList;
    private bool _peopleLoaded;
    private int _peopleFailed;
    private readonly Label _peopleAll = Kit.LinkLabel("All");
    private readonly Label _peopleNone = Kit.LinkLabel("None");
    private readonly Label _peopleHint = Theme.Label("", Theme.Small, Theme.TextMuted, PeopleWidth);
    private readonly ComboBox _workStart = Kit.HourCombo(0, 23);
    private readonly ComboBox _workEnd = Kit.HourCombo(1, 24);
    private readonly PriorityChecks _priorities = new(new Padding(0, 8, 0, 0));
    private readonly CheckBox _myHours = Kit.Check("Only notify me while I'm working myself (same hours, my local time)");

    /// <summary>Groups chosen under "Only these groups".</summary>
    private readonly HashSet<long> _chosen = [];
    /// <summary>Group names, for chosen groups that aren't loaded (yet).</summary>
    private readonly Dictionary<long, string> _names = [];
    /// <summary>Unticked colleagues (id -> name): the only ones stored, so new group members are included automatically.</summary>
    private readonly Dictionary<long, string> _excluded = [];
    /// <summary>The colleagues currently shown in the list.</summary>
    private List<Member> _peopleShown = [];
    private int _peopleVersion;
    private TimeRegion _myRegion = TimeRegion.Other;

    /// <summary>
    /// Help colleagues was never set up: when it's switched on and the members load, colleagues in your own region
    /// start unticked (they usually work when you do). Only once.
    /// </summary>
    private bool _applyRegionDefault;

    /// <summary>Group members loaded from Zendesk (group id -> members), remembered like the group list.</summary>
    private static (string Subdomain, DateTime At, Dictionary<long, List<Member>> Groups)? _memberCache;

    public Card Card { get; }

    public HelpColleaguesCard(SettingsContext context, GroupsPage groups)
    {
        _context = context;
        _groups = groups;
        _peopleList = new AssigneeList(PeopleWidth,
            isCovered: id => !_excluded.ContainsKey(id),
            setCovered: (person, covered) =>
            {
                if (covered) _excluded.Remove(person.Id); else _excluded[person.Id] = person.Name;
            })
        { Margin = new Padding(20, 0, 0, 0) };
        _peopleList.Changed += () =>
        {
            UpdatePeopleHint(_peopleLoaded, _peopleFailed);
            _context.Changed();
        };

        Card = new Card("Who and when", "Which colleagues' tickets you cover, for which priorities, and their working hours.", Kit.CardWidth);
        Card.Body.Controls.Add(_help);
        var options = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(50, 2, 0, 0) };
        options.Controls.Add(Theme.Label("Groups", Theme.BodyBold));
        options.Controls.Add(_same);
        options.Controls.Add(_custom);
        options.Controls.Add(_groupList);
        var peopleTitle = Theme.Label("Assignees", Theme.BodyBold);
        peopleTitle.Margin = new Padding(0, 6, 14, 4);
        options.Controls.Add(Kit.Row(peopleTitle, _peopleAll, _peopleNone));
        options.Controls.Add(_peopleList);
        _peopleHint.Margin = new Padding(20, 2, 0, 4);
        options.Controls.Add(_peopleHint);
        options.Controls.Add(_priorities.Row);
        options.Controls.Add(Theme.Label("Tickets without a priority count as Normal.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 100));

        var hours = Kit.Row(Theme.Label("Assignee's working hours"), _workStart, Theme.Label("to"), _workEnd);
        hours.Margin = new Padding(0, 8, 0, 0);
        options.Controls.Add(hours);
        options.Controls.Add(Theme.Label("Monday to Friday, in the assignee's own time zone (from their Zendesk profile). " +
            "Assignees without a time zone are skipped.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 100));
        _myHours.Margin = new Padding(0, 8, 0, 0);
        options.Controls.Add(_myHours);
        Card.Body.Controls.Add(options);

        foreach (var radio in new[] { _same, _custom })
            radio.CheckedChanged += (_, _) => { UpdateOptions(); _context.Changed(); if (radio.Checked) RefreshPeople(); };
        _help.CheckedChanged += (_, _) => { UpdateOptions(); RefreshPeople(); _context.Changed(); };
        _peopleAll.Click += (_, _) => SetAllPeople(true);
        _peopleNone.Click += (_, _) => SetAllPeople(false);
        foreach (var combo in new[] { _workStart, _workEnd })
            combo.SelectedIndexChanged += (_, _) => _context.Changed();
        _myHours.CheckedChanged += (_, _) => _context.Changed();
        _priorities.Changed += () => _context.Changed();

        _context.SessionUpdated += reload =>
        {
            if (reload)
                _memberCache = null; // Reload fetches the group members again too
            if (_context.Me is { } me)
                _myRegion = TimeRegions.Of(ZoneOf(me) ?? LocalZone());
            RefreshPeople();
        };
    }

    public void Load(Settings settings)
    {
        _help.Checked = settings.HelpColleagues;
        _same.Checked = settings.HelpSameAsMonitored;
        _custom.Checked = !settings.HelpSameAsMonitored;
        _applyRegionDefault = !settings.HelpColleagues && settings.HelpExcludedAssignees.Count == 0;
        _chosen.Clear();
        _excluded.Clear();
        foreach (var group in settings.HelpGroups)
        {
            _chosen.Add(group.Id);
            _names[group.Id] = group.Name;
        }
        foreach (var colleague in settings.HelpExcludedAssignees)
            _excluded[colleague.Id] = colleague.Name;
        _workStart.SelectedIndex = Math.Clamp(settings.WorkdayStartHour, 0, 23);
        _workEnd.SelectedIndex = Math.Clamp(settings.WorkdayEndHour, 1, 24) - 1;
        _myHours.Checked = settings.HelpOnlyDuringMyHours;
        _priorities.Selected = settings.HelpPriorities;
        ShowGroups();
    }

    public void Apply(Settings s)
    {
        s.HelpColleagues = _help.Checked;
        s.HelpSameAsMonitored = _same.Checked;
        s.HelpGroups = _chosen.Select(id => new WatchedGroup { Id = id, Name = _names.GetValueOrDefault(id, $"Group {id}") })
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        s.HelpExcludedAssignees = _excluded.Select(kv => new Colleague { Id = kv.Key, Name = kv.Value })
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id).ToList();
        s.WorkdayStartHour = _workStart.SelectedIndex;
        s.WorkdayEndHour = Math.Max(_workEnd.SelectedIndex + 1, s.WorkdayStartHour + 1);
        s.HelpOnlyDuringMyHours = _myHours.Checked;
        s.HelpPriorities = _priorities.Selected;
    }

    /// <summary>For the screenshot tool: sample group members (cached, so nothing is requested), as if signed in.</summary>
    internal void ShowSample(Dictionary<long, List<Member>> members, TimeRegion myRegion)
    {
        _memberCache = (_context.Current().Subdomain, DateTime.UtcNow, members);
        _myRegion = myRegion;
        RefreshPeople();
    }

    /// <summary>
    /// The groups offered: your monitored groups, groups you're a member of, and any chosen before. Rebuilt when the
    /// Help colleagues page is shown, since monitored groups can change on the Groups page.
    /// </summary>
    public void ShowGroups()
    {
        foreach (var group in _groups.AllGroups)
            _names[group.Id] = group.Name;

        var selected = _groups.SelectedIds;
        var suggestions = _groups.Suggestions;
        var offered = selected.Where(id => id != WatchedGroup.NoGroupId)
            .Concat(suggestions.Where(kv => kv.Value.Member).Select(kv => kv.Key))
            .Concat(_chosen)
            .Distinct()
            .Select(id => (Id: id, Name: _names.GetValueOrDefault(id, $"Group {id}")))
            // Selected first, then monitored, then member groups; by name within each. Sorted only when the list
            // is rebuilt, so ticking a box doesn't make it jump.
            .OrderBy(g => _chosen.Contains(g.Id) ? 0 : selected.Contains(g.Id) ? 1 : 2)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _groupList.SuspendLayout();
        foreach (var old in _groupList.Controls.Cast<Control>().ToList())
            old.Dispose();
        _groupList.Controls.Clear();
        if (offered.Count == 0)
            _groupList.Controls.Add(Theme.Label("Monitor a group on the Groups page first.", Theme.Small, Theme.TextMuted));
        foreach (var (id, name) in offered)
        {
            var why = selected.Contains(id) ? "monitored"
                : suggestions.TryGetValue(id, out var s) && s.Member ? "you're a member" : null;
            var box = Kit.Check(why is null ? name : $"{name}  ·  {why}");
            box.Checked = _chosen.Contains(id);
            box.CheckedChanged += (_, _) =>
            {
                if (box.Checked) _chosen.Add(id); else _chosen.Remove(id);
                _context.Changed();
                RefreshPeople();
            };
            _groupList.Controls.Add(box);
        }
        _groupList.ResumeLayout();
        UpdateOptions();
        RefreshPeople();
    }

    // ------------------------------------------------------------------ assignees

    /// <summary>The IANA id of a profile's time zone, if it can be used (the same rule "Help colleagues" applies).</summary>
    private static string? ZoneOf(User user) =>
        IanaId(TicketRules.ResolveTimeZone(user.IanaTimeZone) ?? TicketRules.ResolveTimeZone(user.TimeZone));

    private static string? LocalZone() => IanaId(TimeZoneInfo.Local);

    private static string? IanaId(TimeZoneInfo? zone) =>
        zone is null ? null
        : zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;

    /// <summary>The groups "Help colleagues" covers with the choices as they are right now.</summary>
    private List<long> CoveredGroupIds() =>
        (_same.Checked ? _groups.SelectedIds : _chosen).Where(id => id != WatchedGroup.NoGroupId).ToList();

    private async void RefreshPeople()
    {
        try
        {
            await ShowPeopleAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Settings: could not show the assignees", ex);
        }
    }

    /// <summary>
    /// Lists the members of the covered groups, all ticked except the ones you excluded. Members are only loaded
    /// while "Help colleagues" is on and you're signed in; otherwise just the excluded colleagues are shown.
    /// </summary>
    private async Task ShowPeopleAsync()
    {
        var version = ++_peopleVersion;
        var groupIds = CoveredGroupIds();
        var failed = 0;
        Dictionary<long, List<Member>>? loaded = null;

        if (_help.Checked && _context.Client is { } client && groupIds.Count > 0)
        {
            var subdomain = _context.Current().Subdomain;
            if (_memberCache is not { } cache || cache.Subdomain != subdomain || DateTime.UtcNow - cache.At > GroupsPage.CacheLifetime)
                _memberCache = (subdomain, DateTime.UtcNow, []);
            var groups = _memberCache.Value.Groups;

            var missing = groupIds.Where(id => !groups.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                _peopleHint.Text = "Loading the group members…";
                foreach (var id in missing)
                {
                    try
                    {
                        groups[id] = (await client.GetGroupMembersAsync(id, CancellationToken.None))
                            .Where(u => u.Role != "end-user")
                            .Select(u => new Member(u.Id, u.Name ?? $"User {u.Id}", ZoneOf(u)))
                            .ToList();
                    }
                    catch (AuthRequiredException)
                    {
                        failed++;
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Settings: could not load the members of group {id}", ex);
                        failed++;
                    }
                }
                Log.Info($"Settings: loaded the members of {missing.Count - failed} group(s) for Help colleagues");
            }
            if (version != _peopleVersion || !ReferenceEquals(client, _context.Client))
                return; // the choices or the session changed meanwhile
            loaded = groups;
        }
        else if (version != _peopleVersion)
            return;

        List<Member> shown;
        if (loaded is null)
            shown = _excluded.Select(kv => new Member(kv.Key, kv.Value, null)).ToList();
        else
        {
            var meId = _context.Me?.Id;
            shown = groupIds.SelectMany(id => loaded.GetValueOrDefault(id) ?? [])
                .Where(m => m.Id != meId)
                .DistinctBy(m => m.Id)
                .ToList();
            foreach (var m in shown.Where(m => _excluded.ContainsKey(m.Id)))
                _excluded[m.Id] = m.Name; // keep the stored name current

            if (_applyRegionDefault && shown.Count > 0)
            {
                _applyRegionDefault = false;
                foreach (var m in RegionDefaultExclusions(shown, _myRegion))
                    _excluded[m.Id] = m.Name;
                _context.Changed();
            }
        }
        _peopleShown = shown.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var isLoaded = loaded is not null;
        _peopleLoaded = isLoaded;
        _peopleFailed = failed;

        // Signed in: by region (from each colleague's time zone), so e.g. only other regions can be covered.
        // Otherwise just the unticked colleagues, whose time zones aren't known.
        var sections = isLoaded
            ? Arrange(_peopleShown, _excluded.Keys.ToHashSet(), _myRegion).Select(s => (Region: (TimeRegion?)s.Region, s.Members)).ToList()
            : [(Region: (TimeRegion?)null, Members: _peopleShown)];
        _peopleList.SetGroups(sections.Select(section => new AssigneeList.Group(
            section.Region is { } r ? TimeRegions.Name(r) + (r == _myRegion ? "  ·  your region" : "") : null,
            section.Members.Select(m => new AssigneeList.Person(m.Id, m.Name, !isLoaded ? null
                : m.Zone is { } zone ? $"{m.Name} · {zone.Replace('_', ' ')}" : $"{m.Name} · no time zone in their Zendesk profile")).ToList())).ToList());
        UpdatePeopleHint(isLoaded, failed);
        UpdateOptions();
    }

    /// <summary>
    /// The assignees by region: other regions first, your own region last, "no time zone" (always skipped) at the very
    /// end. Within a region the covered (ticked) ones first, then by name. Sorted when the list is built, so ticking
    /// a box doesn't make it jump.
    /// </summary>
    internal static List<(TimeRegion Region, List<Member> Members)> Arrange(IEnumerable<Member> members, IReadOnlySet<long> excluded, TimeRegion myRegion) =>
        members.GroupBy(m => m.Region)
            .OrderBy(g => g.Key == TimeRegion.NoTimeZone ? 2 : g.Key == myRegion ? 1 : 0)
            .ThenBy(g => g.Key)
            .Select(g => (g.Key, g.OrderBy(m => excluded.Contains(m.Id) ? 1 : 0)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();

    /// <summary>The first time: colleagues in your own region aren't covered (they're usually at work when you are).</summary>
    internal static IEnumerable<Member> RegionDefaultExclusions(IEnumerable<Member> members, TimeRegion myRegion) =>
        members.Where(m => m.Region == myRegion);

    private void UpdatePeopleHint(bool loaded, int failed)
    {
        var excludedShown = _peopleShown.Count(m => _excluded.ContainsKey(m.Id));
        var everyone = excludedShown == 0 ? "Everyone in these groups." : "Everyone in these groups except the ones unticked above.";
        string text;
        if (CoveredGroupIds().Count == 0)
            text = "Choose a group first.";
        else if (!loaded)
            text = _help.Checked ? everyone + " Sign in to choose from the group members." : everyone;
        else if (_peopleShown.Count == 0)
            text = "No other members found in these groups.";
        else
            text = $"{_peopleShown.Count - excludedShown} of {_peopleShown.Count} colleagues covered. New group members are included automatically.";
        if (failed > 0)
            text += $" Could not load the members of {failed} group(s).";
        _peopleHint.Text = text;
    }

    private void SetAllPeople(bool covered)
    {
        if (!_help.Checked)
            return;
        _peopleList.SetAll(covered);
    }

    private void UpdateOptions()
    {
        var on = _help.Checked;
        _same.Enabled = _custom.Enabled = _workStart.Enabled = _workEnd.Enabled = _myHours.Enabled = on;
        _priorities.Enabled = on;
        _groupList.Enabled = on && _custom.Checked;
        _peopleList.Enabled = _peopleAll.Enabled = _peopleNone.Enabled = on;
    }
}
