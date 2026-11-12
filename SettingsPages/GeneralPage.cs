using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>General: startup options, updates, feedback and the log folder.</summary>
internal sealed class GeneralPage : SettingsPage
{
    private const string Author = "Claude & Daniel Dahlin @ Optimizely";
    private const string FeedbackName = "Daniel";

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
        var startup = new Card("Startup", null, Kit.CardWidth);
        startup.Body.Controls.Add(_startup);
        startup.Body.Controls.Add(_startSummary);
        Panel.Controls.Add(startup);

        var updates = new Card("Updates", "New versions are downloaded automatically and installed when you restart the app.", Kit.CardWidth);
        _checkUpdates.AutoSize = true;
        updates.Body.Controls.Add(Kit.Row(_checkUpdates));
        Panel.Controls.Add(updates);

        var feedback = new Card("Feedback", $"Ideas, questions or something not working? Send feedback opens an email to {FeedbackName} " +
            "in Outlook (or another mail app) with your app and Windows version filled in. If something went wrong, please attach the latest log from the log folder.", Kit.CardWidth);
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
    }

    public override void Apply(Settings settings) => settings.ShowStartSummary = _startSummary.Checked;

    /// <summary>Reset: the start summary goes back to its default; "Start with Windows" (a Windows setting) stays.</summary>
    public void ResetStartSummary() => _startSummary.Checked = new Settings().ShowStartSummary;

    /// <summary>Mirrors the tray menu's update item ("Checking for updates…", "Restart to update to …").</summary>
    public void ShowUpdateState(string text, bool enabled)
    {
        _checkUpdates.Text = text;
        _checkUpdates.Enabled = enabled;
    }
}
