using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Appearance: popup style and position, sound and display time per kind, and test notifications.</summary>
internal sealed class AppearancePage : SettingsPage
{
    private static readonly Option<ToastSound>[] SoundOptions =
    [
        new(ToastSound.Default, "Default"), new(ToastSound.Mail, "Mail"), new(ToastSound.InstantMessage, "Instant message"),
        new(ToastSound.Reminder, "Reminder"), new(ToastSound.Sms, "SMS"),
        new(ToastSound.ChipBlip, "Chiptune: Blip"), new(ToastSound.ChipArpeggio, "Chiptune: Arpeggio"),
        new(ToastSound.ChipBounce, "Chiptune: Bounce"), new(ToastSound.ChipAlert, "Chiptune: Alert"),
        new(ToastSound.ChipFanfare, "Chiptune: Fanfare"),
        new(ToastSound.SongBouncy, "Chiptune song: Bouncy"), new(ToastSound.SongGroove, "Chiptune song: Groove"),
        new(ToastSound.SongCelebration, "Chiptune song: Celebration"),
        new(ToastSound.Silent, "Silent")
    ];

    private static readonly Option<ToastDisplay>[] DisplayOptions =
    [
        new(ToastDisplay.Standard, "Standard (~5 s)"), new(ToastDisplay.Medium, "Medium (~10 s)"), new(ToastDisplay.Long, "Long (~25 s)"),
        new(ToastDisplay.UntilDismissed, "Stay until dismissed")
    ];

    private readonly ThemeTile[] _themeTiles =
    [
        new(NotificationTheme.Optimizely, "Optimizely"), new(NotificationTheme.NeonDark, "Neon dark"), new(NotificationTheme.Terminal, "Terminal"),
        new(NotificationTheme.CleanLight, "Clean light"), new(NotificationTheme.CozyPurple, "Cozy"),
        new(NotificationTheme.WindowsNative, "Windows")
    ];
    private readonly ComboBox _position = Kit.Combo<PopupPosition>([new(PopupPosition.BottomRight, "Bottom right"), new(PopupPosition.TopRight, "Top right")]);
    private readonly ComboBox _newSound = Kit.Combo(SoundOptions);
    private readonly ComboBox _newDisplay = Kit.Combo(DisplayOptions);
    private readonly ComboBox _mySound = Kit.Combo(SoundOptions);
    private readonly ComboBox _myDisplay = Kit.Combo(DisplayOptions);
    private readonly ComboBox _helpSound = Kit.Combo(SoundOptions);
    private readonly ComboBox _helpDisplay = Kit.Combo(DisplayOptions);
    private readonly ComboBox _ccSound = Kit.Combo(SoundOptions);
    private readonly ComboBox _ccDisplay = Kit.Combo(DisplayOptions);
    private readonly ComboBox _summarySound = Kit.Combo(SoundOptions);
    private readonly ComboBox _summaryDisplay = Kit.Combo(DisplayOptions);
    private readonly ComboBox _appSound = Kit.Combo(SoundOptions);
    private readonly ComboBox _appDisplay = Kit.Combo(DisplayOptions);
    private readonly ToggleSwitch _commentText = new("Show the comment text in notifications");
    private readonly ToggleSwitch _showLogo = new("Show the Zendesk Watcher logo");
    private readonly ToggleSwitch _groupByType = new("Group notifications by type in the notification center");
    private readonly Button _testNew = Theme.SecondaryButton("New ticket");
    private readonly Button _testMine = Theme.SecondaryButton("My ticket");
    private readonly Button _testHelp = Theme.SecondaryButton("Colleague");
    private readonly Button _testBurst = Theme.SecondaryButton("Several");
    private readonly ToolTip _tips = new();

    public AppearancePage(SettingsContext context)
        : base(context, "Look & sound", "How notifications look and sound. The test buttons use your current choices, even before saving.")
    {
        var style = new Card("Style", "Custom popups slide in at the screen corner and stack. While you present or run a full-screen app, " +
            "Windows notifications are used instead, so nothing pops up over your screen.", Kit.CardWidth);
        // Two rows of three tiles.
        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Kit.CardWidth - 44, 0), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 12) };
        tiles.Controls.AddRange(_themeTiles);
        style.Body.Controls.Add(tiles);
        style.Body.Controls.Add(Kit.Row(Theme.Label("Popup position"), _position));
        Panel.Controls.Add(style);

        var sound = new Card("Sound and display", "App messages: update ready, welcome and group not found; \"Sign-in required\" uses " +
            "their sound but always stays until dismissed. Replies to your own clicks (e.g. \"No updates\") stay quiet.", Kit.CardWidth);
        var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, BackColor = Theme.Surface };
        grid.Controls.Add(Theme.Label(""), 0, 0);
        grid.Controls.Add(Theme.Label("Sound", Theme.Small, Theme.TextMuted), 1, 0);
        grid.Controls.Add(Theme.Label("Display", Theme.Small, Theme.TextMuted), 3, 0);
        AddStyleRow(grid, 1, "New tickets", _newSound, _newDisplay);
        AddStyleRow(grid, 2, "My tickets", _mySound, _myDisplay);
        AddStyleRow(grid, 3, "CC / following", _ccSound, _ccDisplay);
        AddStyleRow(grid, 4, "Colleagues' tickets", _helpSound, _helpDisplay);
        AddStyleRow(grid, 5, "Start summary", _summarySound, _summaryDisplay);
        AddStyleRow(grid, 6, "App messages", _appSound, _appDisplay);
        sound.Body.Controls.Add(grid);
        Panel.Controls.Add(sound);

        var content = new Card("Content", "Replies and notes on your tickets, CC / following tickets and colleagues' tickets.", Kit.CardWidth);
        content.Body.Controls.Add(_commentText);
        var privacy = Theme.Label("When off, notifications only say who replied or added a note. Windows notifications are kept in " +
            "the Windows notification center; the custom popups are never stored.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44);
        privacy.Margin = new Padding(50, 0, 0, 0);
        content.Body.Controls.Add(privacy);
        Panel.Controls.Add(content);

        var windows = new Card("Windows notifications", "Used with the Windows style, and for every style while you present or use a full-screen app.", Kit.CardWidth);
        windows.Body.Controls.Add(_showLogo);
        windows.Body.Controls.Add(_groupByType);
        Panel.Controls.Add(windows);

        var test = new Card("Try it", "Sends sample notifications; clicking them does nothing.", Kit.CardWidth);
        test.Body.Controls.Add(Kit.Row(_testNew, _testMine, _testHelp, _testBurst));
        Panel.Controls.Add(test);

        _testNew.Click += (_, _) => SendTest(NotificationKind.NewTicket);
        _testMine.Click += (_, _) => SendTest(NotificationKind.MyTicket);
        _testHelp.Click += (_, _) => SendTest(NotificationKind.HelpColleague);
        _testBurst.Click += (_, _) => SendTest(null);
        foreach (var tile in _themeTiles)
            tile.Click += (_, _) => SelectTheme(tile.Value);
        TrackChanges(_position, _newSound, _newDisplay, _mySound, _myDisplay, _ccSound, _ccDisplay, _helpSound, _helpDisplay,
            _summarySound, _summaryDisplay, _appSound, _appDisplay, _commentText, _showLogo, _groupByType);
    }

    public override bool IsSubPage => true; // under Notifications: how they look and sound

    public override void Load(Settings settings)
    {
        Kit.Select(_newSound, settings.NewTicketStyle.Sound);
        Kit.Select(_newDisplay, settings.NewTicketStyle.Display);
        Kit.Select(_mySound, settings.MyTicketStyle.Sound);
        Kit.Select(_myDisplay, settings.MyTicketStyle.Display);
        Kit.Select(_helpSound, settings.HelpStyle.Sound);
        Kit.Select(_helpDisplay, settings.HelpStyle.Display);
        Kit.Select(_ccSound, settings.CcTicketStyle.Sound);
        Kit.Select(_ccDisplay, settings.CcTicketStyle.Display);
        Kit.Select(_summarySound, settings.SummaryStyle.Sound);
        Kit.Select(_summaryDisplay, settings.SummaryStyle.Display);
        Kit.Select(_appSound, settings.AppMessageStyle.Sound);
        Kit.Select(_appDisplay, settings.AppMessageStyle.Display);
        _commentText.Checked = settings.ShowCommentText;
        _showLogo.Checked = settings.ShowAppLogo;
        SelectTheme(settings.Theme);
        Kit.Select(_position, settings.Position);
        _groupByType.Checked = settings.GroupNotificationsByType;
    }

    public override void Apply(Settings s)
    {
        s.NewTicketStyle = Style(_newSound, _newDisplay);
        s.MyTicketStyle = Style(_mySound, _myDisplay);
        s.HelpStyle = Style(_helpSound, _helpDisplay);
        s.CcTicketStyle = Style(_ccSound, _ccDisplay);
        s.SummaryStyle = Style(_summarySound, _summaryDisplay);
        s.AppMessageStyle = Style(_appSound, _appDisplay);
        s.ShowCommentText = _commentText.Checked;
        s.ShowAppLogo = _showLogo.Checked;
        s.Theme = _themeTiles.FirstOrDefault(t => t.Selected)?.Value ?? NotificationTheme.NeonDark;
        s.Position = Kit.Selected<PopupPosition>(_position);
        s.GroupNotificationsByType = _groupByType.Checked;
    }

    private static NotificationStyle Style(ComboBox sound, ComboBox display) =>
        new() { Sound = Kit.Selected<ToastSound>(sound), Display = Kit.Selected<ToastDisplay>(display) };

    /// <summary>Selects a style, and previews it on the whole window right away (only kept if saved).</summary>
    public void SelectTheme(NotificationTheme theme)
    {
        foreach (var tile in _themeTiles)
            tile.Selected = tile.Value == theme;
        _position.Enabled = theme != NotificationTheme.WindowsNative;

        var from = Theme.Use(theme);
        if (from != Theme.Current)
            Theme.Recolor(Context.Window, from, Theme.Current);
        Changed();
    }

    /// <summary>Sends samples of one kind, or several of both kinds when <paramref name="kind"/> is null.</summary>
    private void SendTest(NotificationKind? kind)
    {
        try
        {
            if (kind is { } single)
                Context.Notifier.ShowTest(Context.Current(), single);
            else
                Context.Notifier.ShowTestBurst(Context.Current());
        }
        catch (Exception ex)
        {
            Log.Error("Test notification failed", ex);
            ThemedDialog.Inform(Context.Window, "Test notification failed", ex.Message, ThemedDialog.Kind.Error);
        }
    }

    private void AddStyleRow(TableLayoutPanel grid, int row, string label, ComboBox sound, ComboBox display)
    {
        var text = Theme.Label(label);
        text.Anchor = AnchorStyles.Left;
        text.Margin = new Padding(0, 4, 20, 4);
        sound.Width = display.Width = 160;
        sound.Margin = new Padding(0, 3, 4, 3);

        // ▶ plays the selected sound, without showing a notification.
        var play = Theme.SecondaryButton("▶");
        play.AutoSize = false;
        play.MinimumSize = Size.Empty;
        play.Padding = Padding.Empty;
        play.Font = new Font("Segoe UI Symbol", 9f);
        play.Size = new Size(32, sound.Height);
        play.Margin = new Padding(0, 3, 10, 3);
        play.Anchor = AnchorStyles.Left;
        _tips.SetToolTip(play, "Play this sound");
        play.Click += (_, _) => PopupManager.PlaySound(Kit.Selected<ToastSound>(sound));

        grid.Controls.Add(text, 0, row);
        grid.Controls.Add(sound, 1, row);
        grid.Controls.Add(play, 2, row);
        grid.Controls.Add(display, 3, row);
    }
}
