using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>General: the app theme and startup options (Updates & feedback is its sub-page).</summary>
internal sealed class GeneralPage : SettingsPage
{
    private const string Author = "Claude & Daniel Dahlin @ Optimizely";

    private readonly ThemeTile[] _appThemeTiles =
    [
        new(NotificationTheme.Optimizely, "Optimizely"), new(NotificationTheme.NeonDark, "Neon dark"), new(NotificationTheme.Terminal, "Terminal"),
        new(NotificationTheme.CleanLight, "Clean light"), new(NotificationTheme.CozyPurple, "Cozy"), new(NotificationTheme.Night, "Night"), new(NotificationTheme.Retro, "Retro"),
        new(NotificationTheme.WindowsNative, "Windows")
    ];
    private readonly ToggleSwitch _startup = new("Start Zendesk Watcher when I sign in to Windows");
    private readonly ToggleSwitch _startSummary = new("Show a summary when Zendesk Watcher starts");
    public GeneralPage(SettingsContext context, string version)
        : base(context, "General", $"Zendesk Watcher {version}, by {Author}.")
    {
        var theme = new Card("App theme", "The look of this window and its dialogs, and of notifications unless they have their own style " +
            "(Notifications → Look & sound). The Windows theme follows Windows' light or dark mode and accent color.", Kit.CardWidth);
        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Kit.CardWidth - 44, 0), BackColor = Theme.Surface };
        tiles.Controls.AddRange(_appThemeTiles);
        theme.Body.Controls.Add(tiles);
        Panel.Controls.Add(theme);
        foreach (var tile in _appThemeTiles)
            tile.Click += (_, _) => SelectAppTheme(tile.Value);

        var startup = new Card("Startup", null, Kit.CardWidth);
        startup.Body.Controls.Add(_startup);
        startup.Body.Controls.Add(_startSummary);
        Panel.Controls.Add(startup);

        TrackChanges(_startup, _startSummary);
    }

    /// <summary>"Start with Windows" as ticked; a Windows setting (registry), not part of <see cref="Settings"/>.</summary>
    public bool StartWithWindows => _startup.Checked;

    public override void Load(Settings settings)
    {
        _startup.Checked = StartupRegistration.IsEnabled;
        _startSummary.Checked = settings.ShowStartSummary;
        SelectAppTheme(settings.AppTheme);
    }

    public override void Apply(Settings settings)
    {
        settings.ShowStartSummary = _startSummary.Checked;
        if (_appThemeTiles.FirstOrDefault(t => t.Selected) is { } tile)
            settings.AppTheme = tile.Value;
    }

    /// <summary>Selects the app theme and shows it on the whole window right away (only kept if saved).</summary>
    public void SelectAppTheme(NotificationTheme theme)
    {
        foreach (var tile in _appThemeTiles)
            tile.Selected = tile.Value == theme;
        var from = Theme.Use(theme);
        if (from != Theme.Current)
            Theme.Recolor(Context.Window, from, Theme.Current);
        Changed();
    }

    /// <summary>Reset: the start summary goes back to its default; "Start with Windows" (a Windows setting) stays.</summary>
    public void ResetStartSummary() => _startSummary.Checked = new Settings().ShowStartSummary;

}
