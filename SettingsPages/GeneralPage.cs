using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>General: startup options, updates, feedback and the log folder.</summary>
internal sealed class GeneralPage : SettingsPage
{
    private const string Author = "Claude & Daniel Dahlin @ Optimizely";
    private const string FeedbackName = "Daniel";

    private readonly ThemeTile[] _appThemeTiles =
    [
        new(NotificationTheme.Optimizely, "Optimizely"), new(NotificationTheme.NeonDark, "Neon dark"), new(NotificationTheme.Terminal, "Terminal"),
        new(NotificationTheme.CleanLight, "Clean light"), new(NotificationTheme.CozyPurple, "Cozy"), new(NotificationTheme.Night, "Night"), new(NotificationTheme.Retro, "Retro"),
        new(NotificationTheme.WindowsNative, "Windows")
    ];
    private readonly ToggleSwitch _startup = new("Start Zendesk Watcher when I sign in to Windows");
    private readonly ToggleSwitch _startSummary = new("Show a summary when Zendesk Watcher starts");
    private readonly Button _checkUpdates = Theme.SecondaryButton("Check for updates");
    private readonly Button _sendFeedback = Theme.PrimaryButton("Send feedback…");
    private readonly Button _openLogs = Theme.SecondaryButton("Open log folder");
    private readonly Button _reset = Theme.SecondaryButton("Reset to defaults…");

    /// <summary>The buttons; the tray app does the same as its menu items.</summary>
    public event Action? CheckForUpdatesClicked;
    public event Action? SendFeedbackClicked;
    public event Action? OpenLogFolderClicked;

    /// <summary>Reset to defaults was confirmed: the window shows the defaults (not saved until Save).</summary>
    public event Action? ResetRequested;

    public GeneralPage(SettingsContext context, string version)
        : base(context, "General", $"Zendesk Watcher {version}, by {Author}.")
    {
        var theme = new Card("App theme", "The look of this Settings window and its dialogs, and of the notifications unless you choose " +
            "another style for them (Notifications → Look & sound). With Windows, the window follows Windows' light or dark mode and accent color.", Kit.CardWidth);
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

        var updates = new Card("Updates", "New versions are downloaded automatically and installed when you restart the app.", Kit.CardWidth);
        _checkUpdates.AutoSize = true;
        updates.Body.Controls.Add(Kit.Row(_checkUpdates));
        Panel.Controls.Add(updates);

        var feedback = new Card("Feedback", $"Ideas, questions or something not working? Send feedback opens an email to {FeedbackName} " +
            "with a zip of your log and settings attached (in classic Outlook; other mail apps show the zip to attach). You see everything before sending.", Kit.CardWidth);
        feedback.Body.Controls.Add(Kit.Row(_sendFeedback, _openLogs));
        Panel.Controls.Add(feedback);

        var reset = new Card("Reset", "Puts all settings back to their defaults, except your Zendesk account and monitored groups. " +
            "Nothing changes until you click Save.", Kit.CardWidth);
        reset.Body.Controls.Add(Kit.Row(_reset));
        Panel.Controls.Add(reset);
        _reset.Click += (_, _) =>
        {
            if (ThemedDialog.Confirm(Context.Window, "Reset to defaults?",
                    "All settings go back to their defaults, except your account and monitored groups. Nothing is saved until you click Save.",
                    "Show defaults"))
                ResetRequested?.Invoke();
        };

        _checkUpdates.Click += (_, _) => CheckForUpdatesClicked?.Invoke();
        _sendFeedback.Click += (_, _) => SendFeedbackClicked?.Invoke();
        _openLogs.Click += (_, _) => OpenLogFolderClicked?.Invoke();
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

    /// <summary>Mirrors the tray menu's update item ("Checking for updates…", "Restart to update to …").</summary>
    public void ShowUpdateState(string text, bool enabled)
    {
        // While it checks or downloads: a spinner with that text (and not greyed out), clicks ignored.
        var working = !enabled && text.EndsWith('\u2026');
        _checkUpdates.SetBusy(false);
        _checkUpdates.Text = text;
        _checkUpdates.Enabled = enabled || working;
        if (working)
            _checkUpdates.SetBusy(true, text);
    }
}
