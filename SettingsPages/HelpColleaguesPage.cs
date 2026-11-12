namespace ZendeskWatcher.SettingsPages;

/// <summary>Help colleagues: its own page, as it has the most options (groups, assignees, priorities, hours).</summary>
internal sealed class HelpColleaguesPage : SettingsPage
{
    public HelpColleaguesPage(SettingsContext context, GroupsPage groups)
        : base(context, "Help colleagues", "Step in when a customer replies on a colleague's ticket while the colleague is off work.")
    {
        Card = new HelpColleaguesCard(context, groups);
        Panel.Controls.Add(Card.Card);
    }

    internal HelpColleaguesCard Card { get; }

    public override bool IsSubPage => true; // under Notifications

    public override string NavTitle => "Colleagues";

    public override void Load(Settings settings) => Card.Load(settings);

    public override void Apply(Settings settings) => Card.Apply(settings);

    /// <summary>Monitored groups may have changed on the Groups page.</summary>
    public override void OnShowing() => Card.ShowGroups();
}
