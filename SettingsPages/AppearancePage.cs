using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Appearance: notification style and position, sounds, content, and test notifications.</summary>
internal sealed class AppearancePage : SettingsPage
{
    // Silent first, then by kind: Normal (Windows sounds), Retro (short chiptune blips), Nostalgic (chiptune songs, 4–8 s).
    internal static readonly Option<ToastSound>[] SoundOptions =
    [
        new(ToastSound.Silent, "Silent"),
        new(ToastSound.Default, "Normal – Default"), new(ToastSound.Mail, "Normal – Mail"), new(ToastSound.InstantMessage, "Normal – Instant message"),
        new(ToastSound.Reminder, "Normal – Reminder"), new(ToastSound.Sms, "Normal – SMS"),
        new(ToastSound.ChipBlip, "Retro – Blip"), new(ToastSound.ChipArpeggio, "Retro – Arpeggio"),
        new(ToastSound.ChipBounce, "Retro – Bounce"), new(ToastSound.ChipAlert, "Retro – Alert"),
        new(ToastSound.ChipFanfare, "Retro – Fanfare"),
        new(ToastSound.SongBouncy, "Nostalgic – Bouncy"), new(ToastSound.SongGroove, "Nostalgic – Groove"),
        new(ToastSound.SongCelebration, "Nostalgic – Celebration"),
        new(ToastSound.SongQuest, "Nostalgic – Quest"), new(ToastSound.SongTurbo, "Nostalgic – Turbo"),
        new(ToastSound.SongStarfield, "Nostalgic – Starfield")
    ];

    private readonly ThemeTile[] _themeTiles =
    [
        new(NotificationTheme.Optimizely, "Optimizely"), new(NotificationTheme.NeonDark, "Neon dark"), new(NotificationTheme.Terminal, "Terminal"),
        new(NotificationTheme.CleanLight, "Clean light"), new(NotificationTheme.CozyPurple, "Cozy"), new(NotificationTheme.Night, "Night"), new(NotificationTheme.Retro, "Retro"),
        new(NotificationTheme.WindowsNative, "Windows")
    ];
    private readonly RadioButton _likeApp = Kit.Radio("Same as the app theme (General page)");
    private readonly RadioButton _ownStyle = Kit.Radio("A different style for notifications:");
    private readonly ComboBox _position = Kit.Combo<PopupPosition>([new(PopupPosition.BottomRight, "Bottom right"), new(PopupPosition.TopRight, "Top right"),
        new(PopupPosition.RightCenter, "Right center"), new(PopupPosition.TopCenter, "Top center"), new(PopupPosition.BottomCenter, "Bottom center"),
        new(PopupPosition.LeftCenter, "Left center"), new(PopupPosition.Center, "Center")], width: PlacementWidth);
    private readonly ComboBox _stacking = Kit.Combo<PopupStacking>([new(PopupStacking.List, "Below each other"), new(PopupStacking.Deck, "Stacked like cards"), new(PopupStacking.Peek, "Peek from the edge")], width: PlacementWidth);
    private readonly Label _peekNote = Theme.Label("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 60);

    /// <summary>The same width for the position, stacking and size choices, so they line up.</summary>
    private const int PlacementWidth = 200;
    private readonly ComboBox _size = Kit.Combo<int>([new(100, "100%"), new(115, "115%"), new(130, "130%"), new(150, "150%"), new(175, "175%")], width: PlacementWidth);
    private readonly ComboBox _newSound = Kit.Combo(SoundOptions);
    private readonly ComboBox _mySound = Kit.Combo(SoundOptions);
    private readonly ComboBox _helpSound = Kit.Combo(SoundOptions);
    private readonly ComboBox _ccSound = Kit.Combo(SoundOptions);
    private readonly ComboBox _summarySound = Kit.Combo(SoundOptions);
    private readonly ComboBox _appSound = Kit.Combo(SoundOptions);
    private readonly ToggleSwitch _commentText = new("Show the comment text in notifications");
    private readonly ToggleSwitch _showLogo = new("Show the Zendesk Watcher logo");
    private readonly ToggleSwitch _groupByType = new("Group notifications by type in the notification center");
    private readonly Button _testNew = Theme.SecondaryButton("New ticket");
    private readonly Button _testMine = Theme.SecondaryButton("My ticket");
    private readonly Button _testHelp = Theme.SecondaryButton("Colleague");
    private readonly Button _testBurst = Theme.SecondaryButton("Several");
    private readonly ToolTip _tips = new();

    public AppearancePage(SettingsContext context)
        : base(context, "Look & sound", "How notifications look and sound. Try it uses your choices, even before saving.")
    {
        var style = new Card("Notification style", "Zendesk Watcher's own styles appear where you choose below. While you present or use a full-screen app " +
            "they wait, silently, and appear when you're done.", Kit.CardWidth);
        style.Body.Controls.Add(_likeApp);
        style.Body.Controls.Add(_ownStyle);
        // Two rows of three tiles.
        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(Kit.CardWidth - 44, 0), BackColor = Theme.Surface, Margin = new Padding(20, 4, 0, 12) };
        tiles.Controls.AddRange(_themeTiles);
        style.Body.Controls.Add(tiles);
        // Labels in one column, choices lined up in the next.
        var placement = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 2, 0, 0) };
        placement.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        placement.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (text, combo) in new[] { ("Position on screen", _position), ("Several at once", _stacking), ("Size", _size) })
        {
            var label = Theme.Label(text);
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 0, 16, 0);
            placement.Controls.Add(label);
            placement.Controls.Add(combo);
        }
        style.Body.Controls.Add(placement);
        _peekNote.Margin = new Padding(0, 6, 0, 0);
        _peekNote.Visible = false;
        style.Body.Controls.Add(_peekNote);
        // Peek waits at a screen edge: Center has none, so it's greyed out (and left) while Peek is chosen.
        ((RoomyComboBox)_position).IsItemDisabled = i =>
            Kit.Selected<PopupStacking>(_stacking) == PopupStacking.Peek && _position.Items[i] is Option<PopupPosition> { Value: PopupPosition.Center };
        _stacking.SelectedIndexChanged += (_, _) => UpdatePeekNote(userChoice: true);
        _position.SelectedIndexChanged += (_, _) => UpdatePeekNote(userChoice: false);
        Panel.Controls.Add(style);

        var sound = new Card("Sound", "App messages: update ready, welcome, group not found, sign-in required. " +
            "Answers to your own clicks (e.g. \"No updates\") stay quiet.", Kit.CardWidth);
        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Theme.Surface };
        AddSoundRow(grid, 0, "New tickets", _newSound);
        AddSoundRow(grid, 1, "My tickets", _mySound);
        AddSoundRow(grid, 2, "CC / following", _ccSound);
        AddSoundRow(grid, 3, "Colleagues' tickets", _helpSound);
        AddSoundRow(grid, 4, "Start summary", _summarySound);
        AddSoundRow(grid, 5, "App messages", _appSound);
        sound.Body.Controls.Add(grid);
        Panel.Controls.Add(sound);

        var content = new Card("Content", "Replies and notes on your tickets, CC / following tickets and colleagues' tickets.", Kit.CardWidth);
        content.Body.Controls.Add(_commentText);
        var privacy = Theme.Label("When off, notifications only say who replied or added a note. Windows keeps its notifications in " +
            "the notification center; Zendesk Watcher's own styles store nothing.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44 - 50); // minus the indent below
        privacy.Margin = new Padding(50, 0, 0, 0);
        content.Body.Controls.Add(privacy);
        Panel.Controls.Add(content);

        var windows = new Card("Windows notifications", "Used with the Windows style. While you present or use a full-screen app, Windows decides (Do Not Disturb), without sound.", Kit.CardWidth);
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
            tile.Click += (_, _) => { SelectNotificationTheme(tile.Value); Changed(); };
        foreach (var radio in new[] { _likeApp, _ownStyle })
            radio.CheckedChanged += (_, _) => { UpdateStyleOptions(); Changed(); };
        TrackChanges(_position, _stacking, _size, _newSound, _mySound, _ccSound, _helpSound,
            _summarySound, _appSound, _commentText, _showLogo, _groupByType);
    }

    public override bool IsSubPage => true; // under Notifications: how they look and sound

    public override void Load(Settings settings)
    {
        Kit.Select(_newSound, settings.NewTicketStyle.Sound);
        Kit.Select(_mySound, settings.MyTicketStyle.Sound);
        Kit.Select(_helpSound, settings.HelpStyle.Sound);
        Kit.Select(_ccSound, settings.CcTicketStyle.Sound);
        Kit.Select(_summarySound, settings.SummaryStyle.Sound);
        Kit.Select(_appSound, settings.AppMessageStyle.Sound);
        _commentText.Checked = settings.ShowCommentText;
        _showLogo.Checked = settings.ShowAppLogo;
        _likeApp.Checked = settings.NotificationsLikeApp;
        _ownStyle.Checked = !settings.NotificationsLikeApp;
        SelectNotificationTheme(settings.Theme);
        Kit.Select(_stacking, settings.Stacking); // first: with Peek, Center can't be chosen
        Kit.Select(_position, settings.Position);
        UpdatePeekNote(userChoice: false);
        Kit.Select(_size, settings.NotificationSize, fallback: 100);
        _groupByType.Checked = settings.GroupNotificationsByType;
    }

    /// <summary>
    /// With Peek: a short line on how it works; choosing Peek while on Center moves to Top center and says so.
    /// </summary>
    private void UpdatePeekNote(bool userChoice)
    {
        var peek = Kit.Selected<PopupStacking>(_stacking) == PopupStacking.Peek;
        var moved = false;
        if (peek && Kit.Selected<PopupPosition>(_position) == PopupPosition.Center)
        {
            Kit.Select(_position, PopupPosition.TopCenter);
            moved = userChoice;
        }
        _peekNote.Text = !peek ? ""
            : moved ? "Peek needs a screen edge: the position is now Top center."
            : "Notifications wait as tabs at the screen edge; point at one to slide it out. Not with Center.";
        _peekNote.Visible = peek;
        _position.Invalidate();
    }

    public override void Apply(Settings s)
    {
        // Only the sound: how long they stay is set on the Behavior page.
        s.NewTicketStyle.Sound = Kit.Selected<ToastSound>(_newSound);
        s.MyTicketStyle.Sound = Kit.Selected<ToastSound>(_mySound);
        s.HelpStyle.Sound = Kit.Selected<ToastSound>(_helpSound);
        s.CcTicketStyle.Sound = Kit.Selected<ToastSound>(_ccSound);
        s.SummaryStyle.Sound = Kit.Selected<ToastSound>(_summarySound);
        s.AppMessageStyle.Sound = Kit.Selected<ToastSound>(_appSound);
        s.ShowCommentText = _commentText.Checked;
        s.ShowAppLogo = _showLogo.Checked;
        s.NotificationsLikeApp = _likeApp.Checked;
        if (_themeTiles.FirstOrDefault(t => t.Selected) is { } tile)
            s.Theme = tile.Value;
        s.Position = Kit.Selected<PopupPosition>(_position);
        s.Stacking = Kit.Selected<PopupStacking>(_stacking);
        s.NotificationSize = Kit.Selected<int>(_size);
        s.GroupNotificationsByType = _groupByType.Checked;
    }

    public override void OnShowing() => UpdateStyleOptions(); // the app theme may have changed on the General page

    private void SelectNotificationTheme(NotificationTheme theme)
    {
        foreach (var tile in _themeTiles)
            tile.Selected = tile.Value == theme;
        UpdateStyleOptions();
    }

    /// <summary>The tiles only count with "a different style"; the position only for popups (not Windows notifications).</summary>
    private void UpdateStyleOptions()
    {
        foreach (var tile in _themeTiles)
            tile.Enabled = _ownStyle.Checked;
        _position.Enabled = _stacking.Enabled = _size.Enabled = Context.Current().ActiveNotificationTheme != NotificationTheme.WindowsNative;
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

    private void AddSoundRow(TableLayoutPanel grid, int row, string label, ComboBox sound)
    {
        var text = Theme.Label(label);
        text.Anchor = AnchorStyles.Left;
        text.Margin = new Padding(0, 4, 10, 4);
        sound.Width = 182; // fits "Nostalgic – Celebration"
        sound.Margin = new Padding(0, 3, 4, 3);

        // ▶ plays the selected sound, without showing a notification.
        // The triangle is drawn, not a ▶ character: fonts draw that one differently (emoji, off-center) per theme.
        var play = Theme.SecondaryButton("");
        play.AutoSize = false;
        play.MinimumSize = Size.Empty;
        play.Padding = Padding.Empty;
        play.Size = new Size(32, sound.Height);
        if (play is RoundButton round)
            round.Glyph = (g, color, area) =>
            {
                var s = play.DeviceDpi / 96f;
                float cx = area.Width / 2f + 1 * s, cy = area.Height / 2f, h = 5f * s;
                using var brush = new SolidBrush(color);
                g.FillPolygon(brush, new[] { new PointF(cx - h * 0.8f, cy - h), new PointF(cx - h * 0.8f, cy + h), new PointF(cx + h, cy) });
            };
        play.Margin = new Padding(0, 3, 10, 3);
        play.Anchor = AnchorStyles.Left | AnchorStyles.Top; // level with the dropdown next to it, and as tall
        sound.SizeChanged += (_, _) => play.Height = sound.Height;
        _tips.SetToolTip(play, "Play this sound");
        play.Click += (_, _) => PopupManager.PlaySound(Kit.Selected<ToastSound>(sound));

        grid.Controls.Add(text, 0, row);
        grid.Controls.Add(sound, 1, row);
        grid.Controls.Add(play, 2, row);
    }
}
