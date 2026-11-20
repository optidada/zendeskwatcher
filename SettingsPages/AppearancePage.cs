using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Appearance: notification style and position, sounds, content, and test notifications.</summary>
internal sealed class AppearancePage : SettingsPage
{
    // Silent and the soft chime first, then by kind: Normal (Windows sounds), Retro (short chiptune blips), Nostalgic (chiptune songs, 4–8 s).
    internal static readonly Option<ToastSound>[] SoundOptions =
    [
        new(ToastSound.Silent, "Silent"), new(ToastSound.SoftChime, "Soft chime"),
        new(ToastSound.Default, "Normal – Default"), new(ToastSound.Mail, "Normal – Mail"), new(ToastSound.InstantMessage, "Normal – Instant message"),
        new(ToastSound.Reminder, "Normal – Reminder"), new(ToastSound.Sms, "Normal – SMS"),
        new(ToastSound.ChipBlip, "Retro – Blip"), new(ToastSound.ChipArpeggio, "Retro – Arpeggio"),
        new(ToastSound.ChipBounce, "Retro – Bounce"), new(ToastSound.ChipAlert, "Retro – Alert"),
        new(ToastSound.ChipFanfare, "Retro – Fanfare"),
        new(ToastSound.SongBouncy, "Nostalgic – Bouncy"), new(ToastSound.SongGroove, "Nostalgic – Groove"),
        new(ToastSound.SongCelebration, "Nostalgic – Celebration"),
        new(ToastSound.SongQuest, "Nostalgic – Quest"), new(ToastSound.SongTurbo, "Nostalgic – Turbo"),
        new(ToastSound.SongStarfield, "Nostalgic – Starfield"),
        new(ToastSound.AmericanaBanjoRoad, "Americana – Banjo road"), new(ToastSound.AmericanaSlide, "Americana – Slide"),
        new(ToastSound.AmericanaHollow, "Americana – Hollow"),
        new(ToastSound.NatureStream, "Nature – Stream"), new(ToastSound.NatureWindChimes, "Nature – Wind chimes"),
        new(ToastSound.NatureBirdsong, "Nature – Birdsong")
    ];

    private readonly ThemeTile[] _themeTiles =
    [
        new(NotificationTheme.Optimizely, "Optimizely"), new(NotificationTheme.NeonDark, "Neon dark"), new(NotificationTheme.Terminal, "Terminal"),
        new(NotificationTheme.CleanLight, "Clean light"), new(NotificationTheme.CozyPurple, "Cozy"), new(NotificationTheme.Night, "Night"), new(NotificationTheme.Retro, "Retro"),
        new(NotificationTheme.WindowsNative, "Native")
    ];
    private readonly RadioButton _likeApp = Kit.Radio("Same as the app theme");
    private readonly RadioButton _ownStyle = Kit.Radio("A different style for notifications:");
    private readonly ChoiceBox _position = Kit.Combo<PopupPosition>([new(PopupPosition.BottomRight, "Bottom right"), new(PopupPosition.TopRight, "Top right"),
        new(PopupPosition.RightCenter, "Right center"), new(PopupPosition.TopCenter, "Top center"), new(PopupPosition.BottomCenter, "Bottom center"),
        new(PopupPosition.LeftCenter, "Left center"), new(PopupPosition.Center, "Center")], width: PlacementWidth);
    private readonly ChoiceBox _stacking = Kit.Combo<PopupStacking>([new(PopupStacking.List, "Below each other"), new(PopupStacking.Deck, "Stacked like cards"), new(PopupStacking.Peek, "Peek from the edge"),
        new(PopupStacking.Pill, "A pill at the top"), new(PopupStacking.Ticker, "A ticker at the screen edge"), new(PopupStacking.Achievement, "Achievement unlocked")], width: PlacementWidth);
    private readonly Label _peekNote = Theme.Label("", Theme.Small, Theme.TextMuted, Kit.CardWidth - 60);

    /// <summary>The same width for the position, stacking and size choices, so they line up.</summary>
    private const int PlacementWidth = 200;
    private readonly ChoiceBox _size = Kit.Combo<int>([new(100, "100%"), new(115, "115%"), new(130, "130%"), new(150, "150%"), new(175, "175%")], width: PlacementWidth);
    private readonly ChoiceBox _newSound = Kit.Combo(SoundOptions);
    private readonly ChoiceBox _mySound = Kit.Combo(SoundOptions);
    private readonly ChoiceBox _helpSound = Kit.Combo(SoundOptions);
    private readonly ChoiceBox _ccSound = Kit.Combo(SoundOptions);
    private readonly ChoiceBox _summarySound = Kit.Combo(SoundOptions);
    private readonly ChoiceBox _appSound = Kit.Combo(SoundOptions);
    /// <summary>The app's volume for all its sounds: segmented buttons above the list, so it doesn't look like one more kind of notification.</summary>
    private readonly ChoicePills _volume = new("25%", "50%", "75%", "100%");
    private static readonly int[] VolumeSteps = [25, 50, 75, 100];
    private int SelectedVolume => VolumeSteps[Math.Clamp(_volume.SelectedIndex, 0, VolumeSteps.Length - 1)];
    private readonly ToggleSwitch _commentText = new("Show the comment text in notifications");
    private readonly ToggleSwitch _edgeGlow = new("Glow at the screen edge for P1, important clients and SLA breaches");
    private readonly Button _testNew = Theme.SecondaryButton("New ticket");
    private readonly Button _testMine = Theme.SecondaryButton("My ticket");
    private readonly Button _testHelp = Theme.SecondaryButton("Colleague");
    private readonly Button _testBurst = Theme.SecondaryButton("Several");
    private readonly ThemedToolTip _tips = new(); // in the app's colors

    /// <summary>The ▶ buttons; the one whose sound plays shows ■ until it ends (checked while one plays).</summary>
    private readonly List<PlayButton> _playButtons = [];
    private readonly System.Windows.Forms.Timer _soundWatch = new() { Interval = 150 };

    public AppearancePage(SettingsContext context)
        : base(context, "Look & sound", "How notifications look and sound. Try it uses your choices, even before saving.")
    {
        var style = new Card("Notification style", "While you present or use a full-screen app, notifications wait quietly until you're done. " +
            "The app theme is set under [[General → App theme|General/App theme]].", Kit.CardWidth);
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
        foreach (var (text, combo) in new[] { ("Position on screen", _position), ("How they appear", _stacking), ("Size", _size) })
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
        _edgeGlow.Margin = new Padding(0, 8, 0, 0);
        style.Body.Controls.Add(_edgeGlow);
        TrackChanges(_edgeGlow);
        // Peek waits at a screen edge: Center has none, so it's greyed out (and left) while Peek is chosen.
        _position.IsItemDisabled = i =>
            Kit.Selected<PopupStacking>(_stacking) == PopupStacking.Peek && _position.Items[i] is Option<PopupPosition> { Value: PopupPosition.Center };
        _stacking.SelectedIndexChanged += (_, _) => UpdatePeekNote(userChoice: true);
        _position.SelectedIndexChanged += (_, _) => UpdatePeekNote(userChoice: false);
        Panel.Controls.Add(style);

        var sound = new Card("Sound", "App messages are things like \"Update ready\" or \"Sign in again\".", Kit.CardWidth);
        // Volume first, for all sounds (this app only, like its slider in Windows' Volume mixer); a line, then each kind's sound.
        var volumeLabel = Theme.Label("Volume");
        volumeLabel.Margin = new Padding(0, 0, 12, 0);
        var volumeNote = Theme.Label("all sounds, this app only", Theme.Small, Theme.TextMuted);
        volumeNote.Margin = new Padding(10, 0, 0, 0);
        var volumeRow = Kit.Row(volumeLabel, _volume, volumeNote);
        volumeRow.Margin = new Padding(0, 4, 0, 10);
        sound.Body.Controls.Add(volumeRow);
        var line = new Panel { Height = 1, Width = Kit.CardWidth - 44, BackColor = Theme.Border, Margin = new Padding(0, 0, 0, 8) };
        sound.Body.Controls.Add(line);
        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Theme.Surface };
        AddSoundRow(grid, 0, "New tickets", _newSound);
        AddSoundRow(grid, 1, "My tickets", _mySound);
        AddSoundRow(grid, 2, "CC / following", _ccSound);
        AddSoundRow(grid, 3, "Colleagues' tickets", _helpSound);
        AddSoundRow(grid, 4, "Catch up", _summarySound);
        AddSoundRow(grid, 5, "App messages", _appSound);
        sound.Body.Controls.Add(grid);
        Panel.Controls.Add(sound);

        var content = new Card("Content", "For replies and notes.", Kit.CardWidth);
        content.Body.Controls.Add(_commentText);
        var privacy = Theme.Label("When off, notifications only say who replied.",
            Theme.Small, Theme.TextMuted, Kit.CardWidth - 44 - 50); // minus the indent below
        privacy.Margin = new Padding(50, 0, 0, 0);
        content.Body.Controls.Add(privacy);
        Panel.Controls.Add(content);


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
            _summarySound, _appSound, _commentText);
        _volume.SelectedIndexChanged += (_, _) => Changed();
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
        // The nearest step (a value from an edited file, e.g. 60, shows as 50%).
        _volume.SelectedIndex = Array.IndexOf(VolumeSteps, VolumeSteps.MinBy(v => Math.Abs(v - settings.SoundVolume)));
        _commentText.Checked = settings.ShowCommentText;
        _edgeGlow.Checked = settings.EdgeGlow;
        _likeApp.Checked = settings.NotificationsLikeApp;
        _ownStyle.Checked = !settings.NotificationsLikeApp;
        SelectNotificationTheme(settings.Theme);
        Kit.Select(_stacking, settings.Stacking); // first: with Peek, Center can't be chosen
        Kit.Select(_position, settings.Position);
        UpdatePeekNote(userChoice: false);
        Kit.Select(_size, settings.NotificationSize, fallback: 100);
    }

    /// <summary>
    /// Peek, the pill, the ticker and achievements: a short line on how it works; choosing Peek while on Center moves to Top center and says so.
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
        _peekNote.Text = Kit.Selected<PopupStacking>(_stacking) switch
        {
            PopupStacking.Peek => moved ? "Peek needs a screen edge: the position is now Top center."
                : "Notifications wait as tabs at the screen edge; point at one to slide it out. Not with Center.",
            PopupStacking.Pill => "A small pill sums them up; point at it to see them. Always at the top center.",
            PopupStacking.Ticker => "They scroll along the bottom of the screen (the top with a top position); click one to open it, right-click for more.",
            PopupStacking.Achievement => "Game style, one at a time, with a short tune unless the sound is Silent. Always at the bottom center.",
            _ => ""
        };
        _peekNote.Visible = _peekNote.Text.Length > 0;
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
        s.EdgeGlow = _edgeGlow.Checked;
        s.NotificationsLikeApp = _likeApp.Checked;
        if (_themeTiles.FirstOrDefault(t => t.Selected) is { } tile)
            s.Theme = tile.Value;
        s.Position = Kit.Selected<PopupPosition>(_position);
        s.Stacking = Kit.Selected<PopupStacking>(_stacking);
        s.NotificationSize = Kit.Selected<int>(_size);
        s.SoundVolume = SelectedVolume;
    }

    public override void OnShowing() => UpdateStyleOptions(); // the app theme may have changed on the General page

    private void SelectNotificationTheme(NotificationTheme theme)
    {
        foreach (var tile in _themeTiles)
            tile.Selected = tile.Value == theme;
        UpdateStyleOptions();
    }

    /// <summary>The tiles only count with "a different style".</summary>
    private void UpdateStyleOptions()
    {
        foreach (var tile in _themeTiles)
            tile.Enabled = _ownStyle.Checked;
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

    private void AddSoundRow(TableLayoutPanel grid, int row, string label, ChoiceBox sound)
    {
        var text = Theme.Label(label);
        text.Anchor = AnchorStyles.Left;
        text.Margin = new Padding(0, 4, 10, 4);
        sound.Width = 182; // fits "Nostalgic – Celebration"
        sound.Margin = new Padding(0, 3, 6, 3);
        sound.Anchor = AnchorStyles.Left; // the label, the drop-down and ▶ all centered in the row

        // ▶ plays the selected sound, without showing a notification; round, as tall as the drop-down.
        var play = new PlayButton { Margin = new Padding(0, 3, 10, 3), Anchor = AnchorStyles.Left };
        play.MatchHeight(sound);
        _tips.SetToolTip(play, "Play this sound");
        play.PlayingChanged += (_, _) => _tips.SetToolTip(play, play.Playing ? "Stop" : "Play this sound");
        _playButtons.Add(play);
        play.Click += (_, _) =>
        {
            var stop = play.Playing;
            if (stop)
                PopupManager.StopSound();
            foreach (var other in _playButtons)
                other.Playing = false;
            if (stop)
                return;
            SoundVolume.Apply(SelectedVolume); // as chosen, also before saving
            PopupManager.PlaySound(Kit.Selected<ToastSound>(sound));
            play.Playing = PopupManager.IsSoundPlaying;
            _soundWatch.Enabled = play.Playing;
        };
        if (_playButtons.Count == 1)
            Panel.Disposed += (_, _) => _soundWatch.Dispose();
        if (_playButtons.Count == 1)
            _soundWatch.Tick += (_, _) =>
            {
                if (PopupManager.IsSoundPlaying)
                    return;
                foreach (var button in _playButtons)
                    button.Playing = false;
                _soundWatch.Stop();
            };

        grid.Controls.Add(text, 0, row);
        grid.Controls.Add(sound, 1, row);
        grid.Controls.Add(play, 2, row);
    }
}
