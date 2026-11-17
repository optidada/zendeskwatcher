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

    private readonly ToggleSwitch _help = new("Client replies on colleagues' tickets when they're off work or away");
    private readonly RadioButton _same = Kit.Radio("Same as my monitored groups");
    private readonly RadioButton _custom = Kit.Radio("Only these groups:");

    /// <summary>"Only these groups" has groups to offer (monitored, or ones you're a member of); greyed out otherwise.</summary>
    private bool _hasGroupsToChoose = true;
    private readonly FlowLayoutPanel _groupList = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(20, 0, 0, 4)
    };
    private readonly AssigneeList _peopleList;
    private bool _peopleLoaded;
    private int _peopleFailed;
    private readonly Label _peopleHint = Theme.Linked("", Theme.Small, Theme.TextMuted, PeopleWidth);
    private readonly HourRange _workHours = new() { Margin = new Padding(0, 4, 0, 2) };
    private readonly DayChecks _workDays = new("Days", new Padding(0, 2, 0, 0));
    private readonly PriorityChoice _priorities = new(new Padding(0, 0, 0, 0), label: false);
    private readonly CheckBox _myHours = Kit.Check("Only notify me while I'm working myself");

    /// <summary>Groups chosen under "Only these groups".</summary>
    private readonly HashSet<long> _chosen = [];
    /// <summary>Group names, for chosen groups that aren't loaded (yet).</summary>
    private readonly Dictionary<long, string> _names = [];
    /// <summary>Unticked colleagues in ticked regions (id -> name).</summary>
    private readonly Dictionary<long, string> _excluded = [];
    /// <summary>Ticked colleagues in unticked regions (id -> name).</summary>
    private readonly Dictionary<long, string> _included = [];
    /// <summary>Unticked regions: newcomers there aren't covered either. Only exceptions per person are stored.</summary>
    private readonly HashSet<TimeRegion> _offRegions = [];
    /// <summary>The region of each colleague shown (from their time zone), once the members are loaded.</summary>
    private readonly Dictionary<long, TimeRegion> _regionOf = [];
    /// <summary>The colleagues currently shown in the list.</summary>
    private List<Member> _peopleShown = [];
    private int _peopleVersion;
    private TimeRegion _myRegion = TimeRegion.Other;

    /// <summary>
    /// Help colleagues was never set up: when it's switched on and the members load, your own region starts unticked
    /// (they usually work when you do). Only once.
    /// </summary>
    private bool _applyRegionDefault;

    private const string MemberCacheName = "group-members";

    /// <summary>Group members loaded from Zendesk (group id -> members), remembered like the group list.</summary>
    private static (string Subdomain, DateTime At, Dictionary<long, List<Member>> Groups)? _memberCache;

    public Card Card { get; }

    /// <summary>While signed out: the group choice and the people list (both from Zendesk) can't be changed.</summary>
    public void SetReadOnly(bool readOnly)
    {
        _peopleList.ReadOnly = readOnly;
        UpdateOptions();
    }

    public HelpColleaguesCard(SettingsContext context, GroupsPage groups)
    {
        _context = context;
        _groups = groups;
        _peopleList = new AssigneeList(PeopleWidth,
            isCovered: IsCovered,
            setCovered: (person, covered) => SetCovered(person.Id, person.Name, covered))
        {
            Margin = new Padding(20, 0, 0, 0),
            // "Working now" uses the hours and days below, as chosen right now (even before saving).
            IsWorkingAt = local => !TicketRules.IsOutsideHours(local, _workHours.Start, _workHours.End, _workDays.Selected),
            GroupTick = group => group.Tag is TimeRegion region ? RegionTick(region, group.People) : null,
            GroupNote = group => group.Tag is TimeRegion region ? RegionNote(region, group.People) : ""
        };
        _peopleList.GroupToggled += group =>
        {
            if (group.Tag is TimeRegion region)
                ToggleRegion(region, group.People);
        };
        _peopleList.Changed += () =>
        {
            UpdatePeopleHint(_peopleLoaded, _peopleFailed);
            _context.Changed();
        };

        Card = new Card("Who and when", "Whose tickets you cover, which priorities, and their working hours.", Kit.CardWidth);
        Card.Body.Controls.Add(_help);
        var options = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(50, 2, 0, 0) };
        // Sections with the same heading and room above each, so the card reads in parts: groups, people, priorities, hours.
        Label Section(string title, bool first = false)
        {
            var heading = Theme.Label(title, Theme.BodyBold);
            heading.Margin = new Padding(0, first ? 2 : 18, 0, 4);
            return heading;
        }
        Label Note(string text) => Theme.Label(text, Theme.Small, Theme.TextMuted, Kit.CardWidth - 100);

        options.Controls.Add(Section("Groups", first: true));
        options.Controls.Add(_same);
        options.Controls.Add(_custom);
        options.Controls.Add(_groupList);

        options.Controls.Add(Section("Assignees"));
        options.Controls.Add(_peopleList);
        _peopleHint.Margin = new Padding(20, 2, 0, 0);
        options.Controls.Add(_peopleHint);

        options.Controls.Add(Section("Priorities"));
        options.Controls.Add(_priorities.Row);
        options.Controls.Add(Note("Tickets without a priority count as Normal (P3)."));

        options.Controls.Add(Section("Their working hours"));
        options.Controls.Add(_workHours);
        options.Controls.Add(_workDays.Row);
        options.Controls.Add(Note("In each colleague's own time zone (from Zendesk). Colleagues without a time zone are skipped."));

        var myHoursLink = Theme.Linked("[[Change my work hours|Notifications/Working hours]]", Theme.Body);
        myHoursLink.Margin = new Padding(6, 4, 0, 0);
        var myHoursRow = Kit.Row(_myHours, myHoursLink);
        myHoursRow.Margin = new Padding(0, 18, 0, 0);
        options.Controls.Add(myHoursRow);
        Card.Body.Controls.Add(options);

        foreach (var radio in new[] { _same, _custom })
            radio.CheckedChanged += (_, _) => { UpdateOptions(); _context.Changed(); if (radio.Checked) RefreshPeople(); };
        _help.CheckedChanged += (_, _) => { UpdateOptions(); RefreshPeople(); _context.Changed(); };
        _workHours.RangeChanged += (_, _) => { _peopleList.Invalidate(); _context.Changed(); };
        _myHours.CheckedChanged += (_, _) => _context.Changed();
        _workDays.Changed += () => { _peopleList.Invalidate(); _context.Changed(); };
        _priorities.Changed += () => _context.Changed();

        _context.SessionUpdated += reload =>
        {
            if (reload)
            {
                _memberCache = null; // Reload fetches the group members again too
                ListCache.Clear(MemberCacheName);
            }
            if (_context.Me is { } me)
                _myRegion = TimeRegions.Mine(me);
            RefreshPeople();
        };
    }

    public void Load(Settings settings)
    {
        _help.Checked = settings.HelpColleagues;
        _same.Checked = settings.HelpSameAsMonitored;
        _custom.Checked = !settings.HelpSameAsMonitored;
        _applyRegionDefault = settings.HelpRegionDefaultDone != true && settings.HelpExcludedAssignees.Count == 0
            && settings.HelpIncludedAssignees.Count == 0 && settings.HelpOffRegions.Count == 0;
        _chosen.Clear();
        _excluded.Clear();
        _included.Clear();
        _offRegions.Clear();
        _offRegions.UnionWith(settings.HelpOffRegions);
        foreach (var colleague in settings.HelpIncludedAssignees)
            _included[colleague.Id] = colleague.Name;
        foreach (var group in settings.HelpGroups)
        {
            _chosen.Add(group.Id);
            _names[group.Id] = group.Name;
        }
        foreach (var colleague in settings.HelpExcludedAssignees)
            _excluded[colleague.Id] = colleague.Name;
        _workHours.SetRange(settings.WorkdayStartHour, settings.WorkdayEndHour);
        _myHours.Checked = settings.HelpOnlyDuringMyHours;
        _workDays.Selected = settings.WorkDays;
        _priorities.Selected = settings.HelpPriorities;
        ShowGroups();
    }

    public void Apply(Settings s)
    {
        s.HelpColleagues = _help.Checked;
        s.HelpSameAsMonitored = _same.Checked;
        s.HelpGroups = _chosen.Select(id => new WatchedGroup { Id = id, Name = _names.GetValueOrDefault(id, $"Group {id}") })
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        s.HelpExcludedAssignees = Colleagues(_excluded);
        s.HelpIncludedAssignees = Colleagues(_included);
        s.HelpOffRegions = _offRegions.Order().ToList();
        s.HelpRegionDefaultDone = _applyRegionDefault ? null : true; // done once applied, or when it was already
        s.WorkdayStartHour = _workHours.Start;
        s.WorkdayEndHour = _workHours.End;
        s.HelpOnlyDuringMyHours = _myHours.Checked;
        s.WorkDays = _workDays.Selected;
        s.HelpPriorities = _priorities.Selected;
    }

    private static List<Colleague> Colleagues(Dictionary<long, string> people) =>
        people.Select(kv => new Colleague { Id = kv.Key, Name = kv.Value })
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id).ToList();

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
        // Says right on the option when it covers nothing, instead of only a hint under the assignee list.
        var monitored = selected.Count(id => id != WatchedGroup.NoGroupId);
        _same.Text = monitored == 0 ? "Same as my monitored groups (none yet)" : $"Same as my monitored groups ({monitored})";
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
        _hasGroupsToChoose = offered.Count > 0;
        if (!_hasGroupsToChoose)
            _groupList.Controls.Add(Theme.Label("No groups yet: tick groups on the Groups page first.",
                Theme.Small, Theme.TextMuted, Kit.CardWidth - 130));
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
            if (_memberCache is null && ListCache.Load<Dictionary<long, List<Member>>>(MemberCacheName, subdomain, anyAge: true) is { } saved)
                _memberCache = (subdomain, saved.At, saved.Value); // from an earlier run
            // Over a day old: loaded again, keeping the old members of a group that fails to load.
            Dictionary<long, List<Member>> old = [];
            if (_memberCache is { } aged && aged.Subdomain == subdomain && DateTime.UtcNow - aged.At > GroupsPage.CacheLifetime)
                old = aged.Groups;
            if (_memberCache is not { } cache || cache.Subdomain != subdomain || old.Count > 0)
                _memberCache = (subdomain, DateTime.UtcNow, []);
            var groups = _memberCache.Value.Groups;

            var missing = groupIds.Where(id => !groups.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                _peopleHint.Text = "Loading the group members…";
                // All groups at once (the app sends a few requests side by side).
                var results = await Task.WhenAll(missing.Select(async id =>
                {
                    try
                    {
                        return (Id: id, Members: (await client.GetGroupMembersAsync(id, CancellationToken.None))
                            .Where(u => u.Role != "end-user")
                            .Select(u => new Member(u.Id, u.Name ?? $"User {u.Id}", TimeRegions.ZoneOf(u)))
                            .ToList());
                    }
                    catch (Exception ex)
                    {
                        if (ex is not AuthRequiredException)
                            Log.Error($"Settings: could not load the members of group {id}", ex);
                        return (Id: id, Members: (List<Member>?)null);
                    }
                }));
                foreach (var (id, members) in results)
                {
                    if (members is not null)
                        groups[id] = members;
                    else if (!old.ContainsKey(id))
                        failed++;
                }
                Log.Info($"Settings: loaded the members of {results.Count(r => r.Members is not null)} group(s) for Help colleagues");
                ListCache.Save(MemberCacheName, subdomain, _memberCache.Value.At, groups);
            }
            if (version != _peopleVersion || !ReferenceEquals(client, _context.Client))
                return; // the choices or the session changed meanwhile
            loaded = old.Count == 0 ? groups : groups.Concat(old.Where(o => !groups.ContainsKey(o.Key))).ToDictionary();
        }
        else if (_help.Checked && _context.Client is null && groupIds.Count > 0)
        {
            // Signed out: the members as last loaded (any age; the page says they may be out of date), read-only.
            var subdomain = _context.Current().Subdomain;
            if (_memberCache is null && ListCache.Load<Dictionary<long, List<Member>>>(MemberCacheName, subdomain, anyAge: true) is { } saved)
                _memberCache = (subdomain, saved.At, saved.Value);
            if (_memberCache is { } cache && cache.Subdomain == subdomain)
                loaded = cache.Groups;
        }
        else if (version != _peopleVersion)
            return;

        List<Member> shown;
        _regionOf.Clear();
        if (loaded is null)
            shown = _excluded.Concat(_included).Select(kv => new Member(kv.Key, kv.Value, null)).ToList();
        else
        {
            var meId = _context.Me?.Id;
            shown = groupIds.SelectMany(id => loaded.GetValueOrDefault(id) ?? [])
                .Where(m => m.Id != meId)
                .DistinctBy(m => m.Id)
                .ToList();
            foreach (var m in shown)
            {
                _regionOf[m.Id] = m.Region;
                if (_excluded.ContainsKey(m.Id)) _excluded[m.Id] = m.Name; // keep the stored names current
                if (_included.ContainsKey(m.Id)) _included[m.Id] = m.Name;
            }
            // From before regions had a tick: a region whose colleagues were all unticked one by one becomes an
            // unticked region (the same for them; newcomers there now stay unticked too).
            foreach (var region in shown.GroupBy(m => m.Region).Where(g => !_offRegions.Contains(g.Key) && g.All(m => _excluded.ContainsKey(m.Id))))
            {
                _offRegions.Add(region.Key);
                foreach (var m in region)
                    _excluded.Remove(m.Id);
            }

            if (_applyRegionDefault && shown.Count > 0)
            {
                _applyRegionDefault = false;
                _offRegions.Add(_myRegion);
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
            ? Arrange(_peopleShown, IsCovered, _myRegion).Select(s => (Region: (TimeRegion?)s.Region, s.Members)).ToList()
            : [(Region: (TimeRegion?)null, Members: _peopleShown)];
        _peopleList.SetGroups(sections.Select(section => new AssigneeList.Group(
            section.Region is { } r ? TimeRegions.Name(r) + (r == _myRegion ? " · your region" : "") : null,
            section.Members.Select(m => new AssigneeList.Person(m.Id, m.Name, !isLoaded ? null
                : m.Zone is { } zone ? $"{m.Name} · {zone.Replace('_', ' ')}" : $"{m.Name} · no time zone in their Zendesk profile",
                isLoaded ? TicketRules.ResolveTimeZone(m.Zone) : null)).ToList(),
            Tag: section.Region,
            // A region with exceptions starts open, so you see who they are.
            StartOpen: section.Region is { } open && section.Members.Any(m => IsException(m.Id, open)))).ToList());
        UpdatePeopleHint(isLoaded, failed);
        UpdateOptions();
    }

    /// <summary>
    /// The assignees by region: other regions first, your own region last, "no time zone" (always skipped) at the very
    /// end. Within a region the covered (ticked) ones first, then by name. Sorted when the list is built, so ticking
    /// a box doesn't make it jump.
    /// </summary>
    internal static List<(TimeRegion Region, List<Member> Members)> Arrange(IEnumerable<Member> members, Func<long, bool> isCovered, TimeRegion myRegion) =>
        members.GroupBy(m => m.Region)
            .OrderBy(g => g.Key == TimeRegion.NoTimeZone ? 2 : g.Key == myRegion ? 1 : 0)
            .ThenBy(g => g.Key)
            .Select(g => (g.Key, g.OrderBy(m => isCovered(m.Id) ? 0 : 1)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .ToList();

    // ------------------------------------------------------------------ region ticks

    /// <summary>Ticked: by their region's tick, unless they're an exception to it. Region unknown (signed out): the exceptions.</summary>
    private bool IsCovered(long id) =>
        _regionOf.TryGetValue(id, out var region) && _offRegions.Contains(region) ? _included.ContainsKey(id) : !_excluded.ContainsKey(id);

    /// <summary>Unticked in a ticked region, or ticked in an unticked one.</summary>
    private bool IsException(long id, TimeRegion region) =>
        _offRegions.Contains(region) ? _included.ContainsKey(id) : _excluded.ContainsKey(id);

    private void SetCovered(long id, string name, bool covered)
    {
        var off = _regionOf.TryGetValue(id, out var region) && _offRegions.Contains(region);
        _excluded.Remove(id);
        _included.Remove(id);
        if (off && covered)
            _included[id] = name;
        else if (!off && !covered)
            _excluded[id] = name;
    }

    /// <summary>The region's box: ticked, unticked, or mixed (null) when someone in it is an exception.</summary>
    private bool? RegionTick(TimeRegion region, IEnumerable<AssigneeList.Person> people) =>
        people.Any(p => IsException(p.Id, region)) ? null : !_offRegions.Contains(region);

    /// <summary>"5 people · 1 unticked": how many, and the exceptions, so it's clear which way newcomers go.</summary>
    private string RegionNote(TimeRegion region, IReadOnlyList<AssigneeList.Person> people)
    {
        var exceptions = people.Count(p => IsException(p.Id, region));
        var count = people.Count == 1 ? "1 person" : $"{people.Count} people";
        return exceptions == 0 ? count : $"{count} · {exceptions} {(_offRegions.Contains(region) ? "ticked" : "unticked")}";
    }

    /// <summary>A click on a region's box: mixed becomes ticked; otherwise ticked and unticked swap. No exceptions after.</summary>
    private void ToggleRegion(TimeRegion region, IEnumerable<AssigneeList.Person> people)
    {
        var list = people.ToList();
        var tick = RegionTick(region, list);
        if (tick is true) _offRegions.Add(region); else _offRegions.Remove(region);
        foreach (var p in list)
        {
            _excluded.Remove(p.Id);
            _included.Remove(p.Id);
        }
    }

    private void UpdatePeopleHint(bool loaded, int failed)
    {
        var excludedShown = _peopleShown.Count(m => !IsCovered(m.Id));
        var everyone = excludedShown == 0 ? "Everyone in these groups." : "Everyone in these groups except the ones unticked above.";
        string text;
        if (CoveredGroupIds().Count == 0)
            text = _same.Checked
                ? "No groups to cover yet: tick groups on the Groups page, or choose \"Only these groups\"."
                : "Tick at least one group above.";
        else if (!loaded)
            text = _help.Checked ? everyone + " Sign in to choose from the group members." : everyone;
        else if (_peopleShown.Count == 0)
            text = "No other members found in these groups.";
        else
            text = $"{_peopleShown.Count - excludedShown} of {_peopleShown.Count} colleagues covered. Newcomers follow their region's tick.";
        if (failed > 0)
            text += $" Could not load the members of {failed} group(s).";
        _peopleHint.Text = text;
    }

    private void UpdateOptions()
    {
        var on = _help.Checked;
        var signedIn = !_context.SignedOut; // signed out: the lists from Zendesk can't be changed
        _workHours.Enabled = _myHours.Enabled = on;
        // Which groups: a choice between lists from Zendesk, so it waits for sign-in too (the current choice stays).
        _same.Enabled = on && signedIn;
        _custom.Enabled = on && signedIn && _hasGroupsToChoose; // nothing to choose: greyed out
        _workDays.Enabled = on;
        _priorities.Enabled = on;
        _groupList.Enabled = on && _custom.Checked && signedIn;
        _peopleList.Enabled = on;
    }
}
