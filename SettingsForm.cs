using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher;

/// <summary>Settings window: account and sign-in, monitored groups, and notification options.</summary>
public sealed class SettingsForm : Form
{
    private const int CardWidth = 556;
    private const string Author = "Claude + Daniel Dahlin @ Optimizely";

    private readonly Func<Settings, IZendeskSession> _sessionFactory;
    private readonly Notifier _notifier;
    private readonly bool _knownSignedOut;

    // Navigation
    private readonly List<(NavItem Nav, Control Page)> _pages = [];

    // Account
    private readonly InputBox _subdomain = new(220, "e.g. episerver");
    private readonly StatusDot _accountDot = new();
    private readonly Label _account = Theme.Label("Not signed in", maxWidth: CardWidth - 70);
    private readonly Button _signIn = Theme.PrimaryButton("Sign in");
    private readonly Button _signOut = Theme.SecondaryButton("Sign out");

    // Groups
    private readonly InputBox _groupFilter = new(CardWidth - 44, "Filter groups…", searchIcon: true);
    private readonly SmoothListView _groups = new()
    {
        Width = CardWidth - 44, Height = 280, View = View.Details, CheckBoxes = true, HeaderStyle = ColumnHeaderStyle.None,
        FullRowSelect = true, MultiSelect = false, ShowGroups = true, BorderStyle = BorderStyle.None,
        BackColor = Theme.Surface, Font = Theme.Body, Margin = new Padding(0, 10, 0, 10)
    };
    private readonly Label _groupsLoading = new()
    {
        Text = "Loading your groups…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Font = Theme.Body, ForeColor = Theme.TextMuted, BackColor = Theme.Surface, Visible = false
    };
    private readonly ListViewGroup _monitoredSection = new("Monitored");
    private readonly ListViewGroup _otherSection = new("Other groups");
    private readonly Label _groupsHint = Theme.Label("Sign in to load your groups.", Theme.Small, Theme.TextMuted);
    private readonly Button _reloadGroups = Theme.SecondaryButton("Reload");
    private List<GroupItem> _allGroups = [];
    private readonly HashSet<long> _selectedGroups = [];
    private bool _suppressGroupEvents;

    // Notifications
    private readonly ToggleSwitch _notifyUnassigned = new("New unassigned tickets in my monitored groups");
    private readonly NumericUpDown _interval = new() { Minimum = 30, Maximum = 3600, Increment = 15, Width = 80, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle };
    private readonly ToggleSwitch _trackMine = new("Changes on tickets assigned to me");
    private readonly ToggleSwitch _trackCc = new("Changes on tickets where I'm CC or a follower");
    private readonly CheckBox _replies = Check("Public replies");
    private readonly CheckBox _notes = Check("Internal notes");
    private readonly CheckBox _status = Check("Status changes");
    private readonly CheckBox _assignment = Check("Reassignment and group moves");
    private readonly CheckBox _priority = Check("Priority changes");
    private readonly ToggleSwitch _startup = new("Start Zendesk Watcher when I sign in to Windows");

    // Appearance
    private readonly ComboBox _newSound = Combo(SoundOptions);
    private readonly ComboBox _newDisplay = Combo(DisplayOptions);
    private readonly ComboBox _mySound = Combo(SoundOptions);
    private readonly ComboBox _myDisplay = Combo(DisplayOptions);
    private readonly ToggleSwitch _showLogo = new("Show the Zendesk Watcher logo");
    private readonly ToggleSwitch _groupByType = new("Group notifications by type in the notification center");
    private readonly Button _testNew = Theme.SecondaryButton("Test new ticket");
    private readonly Button _testMine = Theme.SecondaryButton("Test my ticket");
    private readonly Button _testBurst = Theme.SecondaryButton("Test several");
    private readonly ThemeTile[] _themeTiles =
    [
        new(NotificationTheme.NeonDark, "Neon dark"), new(NotificationTheme.Terminal, "Terminal"),
        new(NotificationTheme.CleanLight, "Clean light"), new(NotificationTheme.WindowsNative, "Windows")
    ];
    private readonly ComboBox _position = Combo<PopupPosition>([new(PopupPosition.BottomRight, "Bottom right"), new(PopupPosition.TopRight, "Top right")]);

    private static readonly Option<ToastSound>[] SoundOptions =
    [
        new(ToastSound.Default, "Default"), new(ToastSound.Mail, "Mail"), new(ToastSound.InstantMessage, "Instant message"),
        new(ToastSound.Reminder, "Reminder"), new(ToastSound.Sms, "SMS"), new(ToastSound.Silent, "Silent")
    ];

    private static readonly Option<ToastDisplay>[] DisplayOptions =
    [
        new(ToastDisplay.Standard, "Standard (~5 s)"), new(ToastDisplay.Long, "Long (~25 s)"), new(ToastDisplay.UntilDismissed, "Stay until dismissed")
    ];

    // Footer
    private readonly Button _save = Theme.PrimaryButton("Save");
    private readonly Button _close = Theme.SecondaryButton("Close");
    private readonly Label _saveState = Theme.Label("", Theme.Small, Theme.TextMuted);

    private readonly Panel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(32, 26, 20, 10), BackColor = Theme.Window };
    private bool _loading = true;
    private bool _signingIn;
    private string? _loadedFor;
    private string _savedSnapshot = "";

    /// <summary>The last saved settings; the original settings if nothing was saved.</summary>
    public Settings Result { get; private set; }

    public SettingsForm(Settings settings, Func<Settings, IZendeskSession> sessionFactory, Notifier notifier, bool knownSignedOut = false)
    {
        _sessionFactory = sessionFactory;
        _notifier = notifier;
        _knownSignedOut = knownSignedOut;
        Result = settings;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Zendesk Watcher – Settings";
        Font = Theme.Body;
        BackColor = Theme.Window;
        ClientSize = new Size(840, 620);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.CreateIcon(AppIcon.NeonGreen, 32);

        BuildLayout();
        LoadValues(settings);
        WireEvents();
        SelectPage(0);

        ResumeLayout(false);
        PerformLayout();
        Load += (_, _) => { foreach (var (_, page) in _pages) CreateHandles(page); };

        _savedSnapshot = Snapshot();
        _loading = false;
        UpdateSaveState();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        // The sidebar registers the navigation entries that the pages attach to, so it is built first.
        var sidebar = BuildSidebar();
        var footer = BuildFooter();

        var content = _content;
        content.Controls.Add(BuildAppearancePage());
        content.Controls.Add(BuildNotificationsPage());
        content.Controls.Add(BuildGroupsPage());
        content.Controls.Add(BuildAccountPage());

        // Docking order: the Fill control is added first so the sidebar and footer claim their space before it.
        Controls.Add(content);
        Controls.Add(footer);
        Controls.Add(sidebar);
    }

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Theme.Sidebar };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar };

        var title = Theme.Label("Zendesk Watcher", Theme.AppTitle, Color.White);
        title.Margin = new Padding(24, 26, 0, 2);
        var subtitle = Theme.Label("Settings", Theme.Small, Theme.Neutral);
        subtitle.Margin = new Padding(24, 0, 0, 28);
        stack.Controls.Add(title);
        stack.Controls.Add(subtitle);

        foreach (var name in new[] { "Account", "Groups", "Notifications", "Appearance" })
        {
            var nav = new NavItem(name);
            var index = _pages.Count;
            nav.Click += (_, _) => SelectPage(index);
            stack.Controls.Add(nav);
            _pages.Add((nav, null!));
        }

        var version = typeof(SettingsForm).Assembly.GetName().Version;
        var about = new Label
        {
            Text = $"Author: {Author}\n\nVersion {version?.ToString(3)}",
            Font = Theme.Small,
            ForeColor = Theme.Neutral,
            BackColor = Theme.Sidebar,
            Dock = DockStyle.Bottom,
            Height = 96,
            Padding = new Padding(24, 0, 12, 18),
            TextAlign = ContentAlignment.BottomLeft
        };

        // Fill first, then the bottom-docked label, so the label keeps its space.
        sidebar.Controls.Add(stack);
        sidebar.Controls.Add(about);
        return sidebar;
    }

    private Control BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Surface, Padding = new Padding(32, 14, 24, 14) };
        footer.Paint += (_, e) => { using var pen = new Pen(Theme.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

        _save.Click += (_, _) => Save();
        _close.Click += (_, _) => Close();
        AcceptButton = _save;
        CancelButton = _close;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Theme.Surface };
        _close.Margin = Padding.Empty;
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_save);

        _saveState.Dock = DockStyle.Left;
        _saveState.Padding = new Padding(0, 9, 0, 0);

        footer.Controls.Add(_saveState);
        footer.Controls.Add(buttons);
        return footer;
    }

    private Control BuildAccountPage()
    {
        var page = Page("Account", "Connect to Zendesk with your own agent account.");

        var account = new Card("Zendesk account",
            "The address you use to open Zendesk. You sign in as usual (Okta) in a built-in browser window; " +
            "the app then reads tickets with your own agent permissions.", CardWidth);
        account.Body.Controls.Add(Row(_subdomain, Theme.Label(".zendesk.com", color: Theme.TextMuted)));
        page.Controls.Add(account);

        var status = new Card("Status", null, CardWidth);
        status.Body.Controls.Add(Row(_accountDot, _account));
        var buttons = Row(_signIn, _signOut);
        buttons.Margin = new Padding(0, 12, 0, 0);
        status.Body.Controls.Add(buttons);
        page.Controls.Add(status);

        _pages[0] = (_pages[0].Nav, page);
        return page;
    }

    private Control BuildGroupsPage()
    {
        var page = Page("Groups", "The groups to monitor for unassigned tickets. Their count shows on the tray icon.");

        var card = new Card("Monitored groups",
            $"Only groups visible to your account are listed. \"{WatchedGroup.NoGroupName}\" covers tickets that have neither a group nor an assignee. " +
            "Whether you get notifications is set under Notifications.", CardWidth);
        _groups.Columns.Add("Group");
        _groups.Groups.AddRange([_monitoredSection, _otherSection]);
        var listHost = new Panel { Size = _groups.Size, Margin = _groups.Margin, BackColor = Theme.Surface };
        _groups.Dock = DockStyle.Fill;
        listHost.Controls.Add(_groupsLoading); // overlay, shown while loading
        listHost.Controls.Add(_groups);
        card.Body.Controls.Add(_groupFilter);
        card.Body.Controls.Add(listHost);

        // Full card width, so the Reload button sits at the right edge.
        var footer = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, MinimumSize = new Size(CardWidth - 44, 0), MaximumSize = new Size(CardWidth - 44, 0),
            BackColor = Theme.Surface, Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _groupsHint.Anchor = AnchorStyles.Left;
        _reloadGroups.Margin = Padding.Empty;
        _reloadGroups.Anchor = AnchorStyles.Right;
        footer.Controls.Add(_groupsHint, 0, 0);
        footer.Controls.Add(_reloadGroups, 1, 0);
        card.Body.Controls.Add(footer);
        page.Controls.Add(card);

        _pages[1] = (_pages[1].Nav, page);
        return page;
    }

    private Control BuildNotificationsPage()
    {
        var page = Page("Notifications", "Choose what you want to hear about and how often to check.");

        var unassigned = new Card("New tickets", "A notification for each new unassigned ticket in the groups you monitor.", CardWidth);
        unassigned.Body.Controls.Add(_notifyUnassigned);
        page.Controls.Add(unassigned);

        var mine = new Card("My tickets", "The change types below apply to both. Changes you make yourself never trigger a notification.", CardWidth);
        mine.Body.Controls.Add(_trackMine);
        mine.Body.Controls.Add(_trackCc);
        var options = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(50, 4, 0, 0) };
        options.Controls.AddRange([_replies, _notes, _status, _assignment, _priority]);
        mine.Body.Controls.Add(options);
        page.Controls.Add(mine);

        var checking = new Card("Checking", "Zendesk search can lag 1–3 minutes behind, so very short intervals don't help much.", CardWidth);
        checking.Body.Controls.Add(Row(Theme.Label("Check Zendesk every"), _interval, Theme.Label("seconds")));
        page.Controls.Add(checking);

        var general = new Card("General", null, CardWidth);
        general.Body.Controls.Add(_startup);
        page.Controls.Add(general);

        _pages[2] = (_pages[2].Nav, page);
        return page;
    }

    private Control BuildAppearancePage()
    {
        var page = Page("Appearance", "How notifications look and sound. The test buttons use your current choices, even before saving.");

        var style = new Card("Style", "Custom popups slide in at the screen corner and stack. While you present or run a full-screen app, " +
            "Windows notifications are used instead, so nothing pops up over your screen.", CardWidth);
        var tiles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 12) };
        tiles.Controls.AddRange(_themeTiles);
        style.Body.Controls.Add(tiles);
        style.Body.Controls.Add(Row(Theme.Label("Popup position"), _position));
        page.Controls.Add(style);

        var sound = new Card("Sound and display", null, CardWidth);
        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Theme.Surface };
        grid.Controls.Add(Theme.Label(""), 0, 0);
        grid.Controls.Add(Theme.Label("Sound", Theme.Small, Theme.TextMuted), 1, 0);
        grid.Controls.Add(Theme.Label("Display", Theme.Small, Theme.TextMuted), 2, 0);
        AddStyleRow(grid, 1, "New tickets", _newSound, _newDisplay);
        AddStyleRow(grid, 2, "My tickets", _mySound, _myDisplay);
        sound.Body.Controls.Add(grid);
        page.Controls.Add(sound);

        var windows = new Card("Windows notifications", "Used with the Windows style, and for every style while you present or use a full-screen app.", CardWidth);
        windows.Body.Controls.Add(_showLogo);
        windows.Body.Controls.Add(_groupByType);
        page.Controls.Add(windows);

        var test = new Card("Try it", "Sends sample notifications; clicking them does nothing.", CardWidth);
        test.Body.Controls.Add(Row(_testNew, _testMine, _testBurst));
        page.Controls.Add(test);

        _pages[3] = (_pages[3].Nav, page);
        return page;
    }

    private void SelectTheme(NotificationTheme theme)
    {
        foreach (var tile in _themeTiles)
            tile.Selected = tile.Value == theme;
        _position.Enabled = theme != NotificationTheme.WindowsNative;
        UpdateSaveState();
    }

    private NotificationTheme SelectedTheme => _themeTiles.First(t => t.Selected).Value;

    private void SelectPage(int index)
    {
        // Swap pages with drawing turned off, then repaint once, so a page never appears piece by piece.
        var redraw = _content.IsHandleCreated;
        if (redraw)
            SendMessage(_content.Handle, WmSetRedraw, 0, 0);

        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Nav.Selected = i == index;
            _pages[i].Page.Visible = i == index;
        }

        if (redraw)
        {
            SendMessage(_content.Handle, WmSetRedraw, 1, 0);
            _content.Invalidate(true);
            _content.Update();
        }
    }

    private const int WmSetRedraw = 0x000B;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    /// <summary>Creates the window handles of all pages up front, so opening a page the first time is instant.</summary>
    private static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls)
            CreateHandles(child);
    }

    // ------------------------------------------------------------------ values

    private void LoadValues(Settings settings)
    {
        _subdomain.Text = settings.Subdomain;

        // Show the saved groups until the real list is loaded, so they are not lost when offline.
        foreach (var group in settings.WatchedGroups)
            _selectedGroups.Add(group.Id);
        SetGroups(settings.WatchedGroups.Where(g => !g.IsNoGroup).Select(g => new GroupItem(g.Id, g.Name)));

        _notifyUnassigned.Checked = settings.NotifyUnassigned;
        _interval.Value = Math.Clamp(settings.PollIntervalSeconds, 30, 3600);
        _trackMine.Checked = settings.TrackMyTickets;
        _trackCc.Checked = settings.TrackCcTickets;
        _replies.Checked = settings.NotifyPublicReplies;
        _notes.Checked = settings.NotifyInternalNotes;
        _status.Checked = settings.NotifyStatusChanges;
        _assignment.Checked = settings.NotifyAssignmentChanges;
        _priority.Checked = settings.NotifyPriorityChanges;
        _startup.Checked = StartupRegistration.IsEnabled;
        Select(_newSound, settings.NewTicketStyle.Sound);
        Select(_newDisplay, settings.NewTicketStyle.Display);
        Select(_mySound, settings.MyTicketStyle.Sound);
        Select(_myDisplay, settings.MyTicketStyle.Display);
        _showLogo.Checked = settings.ShowAppLogo;
        SelectTheme(settings.Theme);
        Select(_position, settings.Position);
        _groupByType.Checked = settings.GroupNotificationsByType;
        UpdateMyTicketOptions();

        _reloadGroups.Enabled = false;
        SetAccount("Not signed in", AccountState.SignedOut);
    }

    private void WireEvents()
    {
        _subdomain.Leave += async (_, _) => await RefreshAccountAsync();
        _trackMine.CheckedChanged += (_, _) => UpdateMyTicketOptions();
        _trackCc.CheckedChanged += (_, _) => UpdateMyTicketOptions();
        _signIn.Click += async (_, _) => await SignInAsync();
        _signOut.Click += (_, _) => SignOut();
        _reloadGroups.Click += async (_, _) => await RefreshAccountAsync(force: true);
        _groupFilter.TextChanged += (_, _) => ShowGroupList();
        _groups.ItemChecked += (_, e) =>
        {
            if (_suppressGroupEvents)
                return;
            var group = (GroupItem)e.Item.Tag!;
            if (e.Item.Checked) _selectedGroups.Add(group.Id); else _selectedGroups.Remove(group.Id);

            // Rebuild after the event so the group moves to its section.
            BeginInvoke(() => { ShowGroupList(scrollTo: group.Id); UpdateSaveState(); });
        };
        _groups.MouseClick += (_, e) =>
        {
            // Clicking anywhere on the row toggles it, not just the checkbox.
            var hit = _groups.HitTest(e.Location);
            if (hit.Item is not null && hit.Location != ListViewHitTestLocations.StateImage)
                hit.Item.Checked = !hit.Item.Checked;
        };

        // Track unsaved changes.
        _subdomain.TextChanged += (_, _) => UpdateSaveState();
        _interval.ValueChanged += (_, _) => UpdateSaveState();
        foreach (var combo in new[] { _newSound, _newDisplay, _mySound, _myDisplay })
            combo.SelectedIndexChanged += (_, _) => UpdateSaveState();
        _testNew.Click += (_, _) => SendTest(NotificationKind.NewTicket);
        _testMine.Click += (_, _) => SendTest(NotificationKind.MyTicket);
        _testBurst.Click += (_, _) => SendTest(null);
        foreach (var tile in _themeTiles)
            tile.Click += (_, _) => SelectTheme(tile.Value);
        _position.SelectedIndexChanged += (_, _) => UpdateSaveState();
        foreach (var box in new CheckBox[] { _notifyUnassigned, _trackMine, _trackCc, _replies, _notes, _status, _assignment, _priority, _startup, _showLogo, _groupByType })
            box.CheckedChanged += (_, _) => UpdateSaveState();

        Shown += async (_, _) =>
        {
            if (_knownSignedOut && Current() is { IsConnectionConfigured: true } connection)
            {
                // The app just found no active session; don't make the user wait for another check.
                _loadedFor = connection.Subdomain;
                SetAccount($"Not signed in to {connection.Subdomain}.zendesk.com. Click Sign in.", AccountState.SignedOut);
                _signIn.Focus();
                return;
            }
            await RefreshAccountAsync();
        };
        FormClosing += OnFormClosing;
    }

    /// <summary>Settings built from the fields as they are right now (possibly not saved yet).</summary>
    private Settings Current()
    {
        var names = _allGroups.ToDictionary(g => g.Id, g => g.Name);
        names[WatchedGroup.NoGroupId] = WatchedGroup.NoGroupName;

        var s = Result.Clone();
        s.Subdomain = Settings.NormalizeSubdomain(_subdomain.Text);
        s.WatchedGroups = _selectedGroups
            .Select(id => new WatchedGroup { Id = id, Name = names.GetValueOrDefault(id, $"Group {id}") })
            .OrderBy(g => g.Id == WatchedGroup.NoGroupId ? 0 : 1)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        s.NotifyUnassigned = _notifyUnassigned.Checked;
        s.PollIntervalSeconds = (int)_interval.Value;
        s.TrackMyTickets = _trackMine.Checked;
        s.TrackCcTickets = _trackCc.Checked;
        s.NotifyPublicReplies = _replies.Checked;
        s.NotifyInternalNotes = _notes.Checked;
        s.NotifyStatusChanges = _status.Checked;
        s.NotifyAssignmentChanges = _assignment.Checked;
        s.NotifyPriorityChanges = _priority.Checked;
        s.NewTicketStyle = new NotificationStyle { Sound = Selected<ToastSound>(_newSound), Display = Selected<ToastDisplay>(_newDisplay) };
        s.MyTicketStyle = new NotificationStyle { Sound = Selected<ToastSound>(_mySound), Display = Selected<ToastDisplay>(_myDisplay) };
        s.ShowAppLogo = _showLogo.Checked;
        s.Theme = SelectedTheme;
        s.Position = Selected<PopupPosition>(_position);
        s.GroupNotificationsByType = _groupByType.Checked;
        return s;
    }

    // ------------------------------------------------------------------ account

    private async Task RefreshAccountAsync(bool force = false)
    {
        if (_signingIn)
            return;

        var connection = Current();
        var key = connection.Subdomain;
        if (!force && key == _loadedFor)
            return;

        _loadedFor = key;
        _reloadGroups.Enabled = false;

        if (!connection.IsConnectionConfigured)
        {
            SetAccount("Enter your Zendesk subdomain, then sign in.", AccountState.SignedOut);
            return;
        }

        SetAccount("Checking your Zendesk session…", AccountState.Checking);
        SetGroupsLoading(true);
        UseWaitCursor = true;
        try
        {
            var client = new ZendeskClient(connection, _sessionFactory(connection));
            var me = await client.GetMeAsync(CancellationToken.None);
            if (key != _loadedFor)
                return; // the subdomain changed meanwhile

            SetAccount($"Signed in to {connection.Subdomain}.zendesk.com as {me.Name} ({me.Email}) · {me.Role}", AccountState.SignedIn);

            var groups = await client.GetGroupsAsync(CancellationToken.None);
            SetGroups(groups.Select(g => new GroupItem(g.Id, g.Name)));
            _reloadGroups.Enabled = true;
        }
        catch (AuthRequiredException)
        {
            SetAccount($"Not signed in to {connection.Subdomain}.zendesk.com. Click Sign in.", AccountState.SignedOut);
        }
        catch (Exception ex)
        {
            Log.Error("Settings: could not load account", ex);
            SetAccount($"Could not connect: {ex.Message}", AccountState.Error);
            _reloadGroups.Enabled = true;
        }
        finally
        {
            SetGroupsLoading(false);
            UseWaitCursor = false;
        }
    }

    private async Task SignInAsync()
    {
        var connection = Current();
        if (!connection.IsConnectionConfigured)
        {
            SelectPage(0);
            MessageBox.Show(this, "Enter your Zendesk subdomain first (for example: episerver).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Log.Info($"Sign-in started: {connection.BaseUrl}");
        _signingIn = true;
        SetAccount("Sign in in the window that opened…", AccountState.Checking);

        try
        {
            await _sessionFactory(connection).SignInAsync(this, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SetAccount("Sign-in was cancelled.", AccountState.SignedOut);
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in failed", ex);
            SetAccount("Sign-in failed.", AccountState.SignedOut);
            MessageBox.Show(this, ex.Message, "Sign-in failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        finally
        {
            _signingIn = false;
            Activate();
        }

        await RefreshAccountAsync(force: true);
    }

    private void SignOut()
    {
        _sessionFactory(Current()).SignOut();
        _reloadGroups.Enabled = false;
        SetAccount("Signed out.", AccountState.SignedOut);
    }

    private enum AccountState { SignedOut, Checking, SignedIn, Error }

    /// <summary>Shows the account status; only the buttons that make sense for the state are visible.</summary>
    private void SetAccount(string text, AccountState state)
    {
        _account.Text = text;
        _accountDot.DotColor = state switch
        {
            AccountState.SignedIn => Theme.Success,
            AccountState.Checking => Theme.Warning,
            AccountState.Error => Theme.Danger,
            _ => Theme.Neutral
        };

        _signIn.Visible = state != AccountState.SignedIn;
        _signIn.Enabled = state != AccountState.Checking;
        _signOut.Visible = state is AccountState.SignedIn or AccountState.Error;
    }

    // ------------------------------------------------------------------ groups

    /// <summary>Sets the available groups; the "(No group)" entry is always listed first.</summary>
    private void SetGroups(IEnumerable<GroupItem> groups)
    {
        _allGroups = groups
            .Where(g => g.Id != WatchedGroup.NoGroupId)
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(new GroupItem(WatchedGroup.NoGroupId, WatchedGroup.NoGroupName))
            .ToList();
        ShowGroupList();
    }

    /// <summary>Monitored groups first (highlighted), then the others; both filtered by the search box.</summary>
    private void ShowGroupList(long? scrollTo = null)
    {
        var filter = _groupFilter.Text.Trim();
        var visible = _allGroups.Where(g => filter.Length == 0 || g.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var monitored = visible.Where(g => _selectedGroups.Contains(g.Id)).ToList();
        var others = visible.Where(g => !_selectedGroups.Contains(g.Id)).ToList();

        _suppressGroupEvents = true;
        _groups.BeginUpdate();
        _groups.Items.Clear();
        foreach (var group in monitored)
            _groups.Items.Add(new ListViewItem(group.Name) { Tag = group, Checked = true, Group = _monitoredSection, BackColor = Theme.AccentSoft, Font = Theme.BodyBold });
        foreach (var group in others)
            _groups.Items.Add(new ListViewItem(group.Name) { Tag = group, Checked = false, Group = _otherSection, BackColor = Theme.Surface, Font = Theme.Body });
        _monitoredSection.Header = $"Monitored · {monitored.Count}";
        _otherSection.Header = $"Other groups · {others.Count}";
        _groups.EndUpdate();
        _groups.FitColumn();
        _suppressGroupEvents = false;

        if (scrollTo is { } id && _groups.Items.Cast<ListViewItem>().FirstOrDefault(i => ((GroupItem)i.Tag!).Id == id) is { } item)
            item.EnsureVisible();

        UpdateGroupsHint();
    }

    private void SetGroupsLoading(bool loading)
    {
        _groupsLoading.Visible = loading;
        _groups.Visible = !loading;
        if (loading)
            _groupsLoading.BringToFront();
        _groupFilter.Enabled = !loading;
        if (loading)
            _groupsHint.Text = "Loading…";
        else
            UpdateGroupsHint();
    }

    private void UpdateGroupsHint()
    {
        var realGroups = _allGroups.Count - 1; // minus "(No group)"
        _groupsHint.Text = realGroups <= 0
            ? $"{_selectedGroups.Count} monitored · sign in to load your groups"
            : $"{_selectedGroups.Count} monitored · {realGroups} groups available";
    }

    // ------------------------------------------------------------------ save

    private void UpdateMyTicketOptions()
    {
        foreach (var box in new[] { _replies, _notes, _status, _assignment, _priority })
            box.Enabled = _trackMine.Checked || _trackCc.Checked;
    }

    private string Snapshot() => $"{Current().ToJson()}|startup={_startup.Checked}";

    private bool HasUnsavedChanges => Snapshot() != _savedSnapshot;

    private void UpdateSaveState()
    {
        if (_loading)
            return;

        var dirty = HasUnsavedChanges;
        _save.Enabled = dirty;
        if (dirty)
        {
            _saveState.Text = "Unsaved changes";
            _saveState.ForeColor = Theme.Warning;
        }
        else if (_saveState.Text == "Unsaved changes")
        {
            _saveState.Text = "";
        }
    }

    /// <summary>Saves the settings (applied when the window closes); the window stays open.</summary>
    private bool Save()
    {
        var result = Current();

        if (result.IsConnectionConfigured && result.WatchedGroups.Count == 0 && !result.TrackMyTickets && !result.TrackCcTickets &&
            MessageBox.Show(this, "No groups are monitored and ticket tracking is off, so nothing will be watched. Save anyway?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return false;
        }

        try
        {
            result.Save();
            StartupRegistration.Set(_startup.Checked);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            MessageBox.Show(this, ex.Message, "Could not save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        Result = result;
        _savedSnapshot = Snapshot();
        _saveState.Text = $"✓ Saved at {DateTime.Now:HH:mm}";
        _saveState.ForeColor = Theme.Success;
        UpdateSaveState();
        Log.Info("Settings saved");
        return true;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!HasUnsavedChanges)
            return;

        var answer = MessageBox.Show(this, "You have unsaved changes. Save them before closing?", Text,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

        if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save()))
            e.Cancel = true;
    }

    // ------------------------------------------------------------------ helpers

    private static FlowLayoutPanel Page(string title, string subtitle)
    {
        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Window
        };
        page.Controls.Add(Theme.Label(title, Theme.PageTitle));
        var sub = Theme.Label(subtitle, Theme.Body, Theme.TextMuted, CardWidth);
        sub.Margin = new Padding(0, 0, 0, 20);
        page.Controls.Add(sub);
        return page;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
        foreach (var c in controls)
        {
            if (c is Label label)
            {
                label.Margin = new Padding(4, 5, 6, 0);
            }
            row.Controls.Add(c);
        }
        return row;
    }

    /// <summary>Sends samples of one kind, or several of both kinds when <paramref name="kind"/> is null.</summary>
    private void SendTest(NotificationKind? kind)
    {
        try
        {
            if (kind is { } single)
                _notifier.ShowTest(Current(), single);
            else
                _notifier.ShowTestBurst(Current());
        }
        catch (Exception ex)
        {
            Log.Error("Test notification failed", ex);
            MessageBox.Show(this, ex.Message, "Test notification failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void AddStyleRow(TableLayoutPanel grid, int row, string label, ComboBox sound, ComboBox display)
    {
        var text = Theme.Label(label);
        text.Anchor = AnchorStyles.Left;
        text.Margin = new Padding(0, 4, 20, 4);
        grid.Controls.Add(text, 0, row);
        grid.Controls.Add(sound, 1, row);
        grid.Controls.Add(display, 2, row);
    }

    private static ComboBox Combo<T>(Option<T>[] options)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Theme.Body,
            Width = 170, Margin = new Padding(0, 3, 10, 3), BackColor = Theme.Surface
        };
        combo.Items.AddRange(options.Cast<object>().ToArray());
        combo.SelectedIndex = 0;
        return combo;
    }

    private static void Select<T>(ComboBox combo, T value) where T : struct, Enum
    {
        var index = combo.Items.Cast<Option<T>>().ToList().FindIndex(o => o.Value.Equals(value));
        combo.SelectedIndex = Math.Max(0, index);
    }

    private static T Selected<T>(ComboBox combo) where T : struct, Enum => ((Option<T>)combo.SelectedItem!).Value;

    private sealed record Option<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    private static CheckBox Check(string text) => new()
    {
        Text = text, AutoSize = true, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Surface,
        Margin = new Padding(0, 3, 0, 3), Cursor = Cursors.Hand
    };

    private sealed record GroupItem(long Id, string Name)
    {
        public override string ToString() => Name;
    }
}
