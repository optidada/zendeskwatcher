using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Updates & feedback (under General): check for updates, send feedback / open the log folder, reset to defaults.</summary>
internal sealed class UpdatesPage : SettingsPage
{
    private const string FeedbackName = "Daniel";

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

    public UpdatesPage(SettingsContext context)
        : base(context, "Updates & feedback", "New versions, feedback to the makers, and starting over.")
    {
        var updates = new Card("Updates", "New versions are downloaded automatically and installed when you restart the app.", Kit.CardWidth);
        _checkUpdates.AutoSize = true;
        updates.Body.Controls.Add(Kit.Row(_checkUpdates));
        Panel.Controls.Add(updates);

        var feedback = new Card("Feedback", $"Ideas, questions or something not working? Send feedback opens an email to {FeedbackName} " +
            "with your log and settings in a zip: classic Outlook attaches it, other mail apps show it to attach. Nothing is sent without you.", Kit.CardWidth);
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
    }

    public override bool IsSubPage => true; // under General

    public override string NavTitle => "Updates & feedback";

    public override void Load(Settings settings) { }

    public override void Apply(Settings settings) { }

    /// <summary>Mirrors the tray menu's update item ("Checking for updates…", "Restart to update to …").</summary>
    public void ShowUpdateState(string text, bool enabled)
    {
        // While it checks or downloads: a spinner with that text (and not greyed out), clicks ignored.
        var working = !enabled && text.EndsWith('…');
        _checkUpdates.SetBusy(false);
        _checkUpdates.Text = text;
        _checkUpdates.Enabled = enabled || working;
        if (working)
            _checkUpdates.SetBusy(true, text);
    }
}
