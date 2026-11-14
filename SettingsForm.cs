using ZendeskWatcher.SettingsPages;
using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher;

/// <summary>
/// Settings window: the sidebar, the Save/Close footer and unsaved-changes tracking. The pages themselves are in
/// SettingsPages/ (one class each); every page shows its part of <see cref="Settings"/> and writes it back.
/// </summary>
public sealed class SettingsForm : Form
{
    private static readonly string AppVersion = typeof(SettingsForm).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>"13 Nov 2026": the day this version was built (released), or null if the build didn't record it.</summary>
    internal static readonly string? ReleaseDate =
        typeof(SettingsForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildDate")?.Value is { } value
        && DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date)
            ? date.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    private readonly bool _knownSignedOut;
    private readonly User? _signedInAs;
    private readonly SettingsContext _context;
    private readonly AccountPage _account;
    private readonly GroupsPage _groups;

    /// <summary>
    /// The pages, built the first time they're shown (Account and Groups right away: the others depend on them).
    /// Opening the window builds only those two, which keeps it fast and small. A page that isn't built yet shows
    /// the saved settings, so it adds nothing to <see cref="Current"/>.
    /// </summary>
    private readonly SettingsPage?[] _pages;
    private readonly Func<SettingsPage>[] _makePage;

    /// <summary>The window has scaled itself to the display; pages built later must be scaled when they're built.</summary>
    private bool _autoScaled;

    /// <summary>The sidebar, known before the pages exist.</summary>
    private static readonly (string Title, bool Sub)[] NavItems =
    [
        ("Account", false), ("Groups", false), ("Clients", true), ("Notifications", false), ("Colleagues", true),
        ("Behavior", true), ("Look & sound", true), ("SLA", true), ("Insights", false), ("General", false), ("How it works", false)
    ];
    private const int ClientsIndex = 2, HelpIndex = 4, GeneralIndex = 9;

    private ClientsPage? BuiltClients => _pages[ClientsIndex] as ClientsPage;
    private GeneralPage? BuiltGeneral => _pages[GeneralIndex] as GeneralPage;
    private HelpColleaguesPage Help => (HelpColleaguesPage)Page(HelpIndex);
    private GeneralPage General => (GeneralPage)Page(GeneralIndex);
    private (string Text, bool Enabled)? _updateState;
    private readonly List<NavItem> _nav = [];

    private readonly Button _save = Theme.PrimaryButton("Save");
    private readonly Button _close = Theme.SecondaryButton("Close");
    private readonly Button _saveAndClose = Theme.SecondaryButton("Save & Close");
    private readonly Button _discard = Theme.SecondaryButton("Discard");
    private readonly Label _saveState = Theme.Label("", Theme.Small, Theme.TextMuted);
    private readonly Panel _content = new RepaintOnScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(32, 26, 20, 10), BackColor = Theme.Window };
    private bool _loading = true;
    private string _savedSnapshot = "";

    /// <summary>The last saved settings; the original settings if nothing was saved.</summary>
    public Settings Result { get; private set; }

    /// <summary>Settings were saved; the app applies them right away (checking keeps running while this window is open).</summary>
    public event Action<Settings>? Saved;

    /// <summary>A sign-in or sign-out is about to start: the app pauses checking until <see cref="SessionChanged"/>.</summary>
    public event Action? SessionChanging;

    /// <summary>The sign-in or sign-out has finished.</summary>
    public event Action? SessionChanged;

    /// <summary>General page buttons; the tray app does the same as its menu items.</summary>
    public event Action? CheckForUpdatesClicked;

    /// <summary>Starts Catch up (the Account page's button); done when it has run.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Task>? CatchUp
    {
        get => _account.CatchUp;
        set => _account.CatchUp = value;
    }
    public event Action? SendFeedbackClicked;
    public event Action? OpenLogFolderClicked;

    /// <summary>Mirrors the tray menu's update item ("Checking for updates…", "Restart to update to …").</summary>
    public void ShowUpdateState(string text, bool enabled)
    {
        _updateState = (text, enabled);
        BuiltGeneral?.ShowUpdateState(text, enabled);
    }

    public SettingsForm(Settings settings, Func<Settings, IZendeskSession> sessionFactory, Notifier notifier, bool knownSignedOut = false, User? signedInAs = null)
        : this(UseThemeOf(settings), sessionFactory, notifier, knownSignedOut, 0, signedInAs)
    {
    }

    /// <summary>
    /// Applies the saved appearance before any control exists: field initializers create controls, and they run
    /// before the constructor body, so this has to happen in the arguments of the chained constructor.
    /// </summary>
    private static Settings UseThemeOf(Settings settings)
    {
        Theme.Use(settings.AppTheme);
        return settings;
    }

    private SettingsForm(Settings settings, Func<Settings, IZendeskSession> sessionFactory, Notifier notifier, bool knownSignedOut, int _, User? signedInAs = null)
    {
        _knownSignedOut = knownSignedOut;
        _signedInAs = signedInAs;
        Result = settings;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Zendesk Watcher – Settings";
        Font = Theme.Body;
        BackColor = Theme.Window;
        ClientSize = new Size(840, 680); // tall enough for the Account page with Catch up, without a scrollbar
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.CreateIcon(AppIcon.NeonGreen, 32);

        _context = new SettingsContext(this, _content, notifier, sessionFactory) { Current = Current, Changed = UpdateSaveState };
        _context.ShowAccount = () => SelectPage(Array.IndexOf(_pages!, _account));
        _groups = new GroupsPage(_context);
        _account = new AccountPage(_context, _groups);
        _makePage =
        [
            () => _account, () => _groups, () => new ClientsPage(_context, _groups), () => new NotificationsPage(_context),
            () => new HelpColleaguesPage(_context, _groups), () => new BehaviorPage(_context), () => new AppearancePage(_context),
            () => new SlaPage(_context), () => new StatisticsPage(_context), MakeGeneralPage, () => new HowItWorksPage(_context)
        ];
        _pages = new SettingsPage?[_makePage.Length];
        _pages[0] = _account;
        _pages[1] = _groups;

        _account.SessionChanging += () => SessionChanging?.Invoke();
        _account.SessionChanged += () => SessionChanged?.Invoke();
        _account.SignedIn += SaveConnection;

        BuildLayout();
        _account.Load(settings);
        _groups.Load(settings);
        SelectPage(0);

        ResumeLayout(false);
        PerformLayout();
        _autoScaled = true; // pages built from now on scale themselves (Page)
        Load += (_, _) =>
        {
            // Never taller than the screen (small or highly scaled displays): then the pages scroll instead.
            var area = Screen.FromControl(this).WorkingArea;
            if (Height > area.Height)
                Height = area.Height;
            foreach (var page in BuiltPages) CreateHandles(page.Panel);
            Theme.ApplyWindowChrome(this, Theme.Current.Dark); // again, now for the scroll bars and lists too
        };
        // Invisible until the first frame is painted: otherwise Windows shows the empty (white) window for a moment.
        Opacity = 0;
        Shown += async (_, _) =>
        {
            Update();    // paint everything once, still invisible
            Opacity = 1;
            await _account.OnWindowShownAsync(_knownSignedOut, _signedInAs);
        };
        FormClosing += OnFormClosing;

        _savedSnapshot = Snapshot();
        _loading = false;
        UpdateSaveState();
    }

    /// <summary>The dark title bar and scroll bars before the window is first shown, so it never starts light.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this, Theme.Current.Dark);
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var sidebar = BuildSidebar();
        var footer = BuildFooter();

        // Only one page is visible at a time; the others are added when they're first shown.
        foreach (var page in BuiltPages)
            _content.Controls.Add(page.Panel);

        // Docking order: the Fill control is added first so the sidebar and footer claim their space before it.
        Controls.Add(_content);
        Controls.Add(footer);
        Controls.Add(sidebar);
    }

    private Control BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Theme.Sidebar };
        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar };

        var title = Theme.Label("Zendesk Watcher", Theme.AppTitle, Theme.SidebarTitle);
        title.Margin = new Padding(24, 26, 0, 2);
        var subtitle = Theme.Label("Settings", Theme.Small, Theme.Neutral);
        subtitle.Margin = new Padding(24, 0, 0, 28);
        stack.Controls.Add(title);
        stack.Controls.Add(subtitle);

        foreach (var (navTitle, sub) in NavItems)
        {
            var nav = new NavItem(navTitle) { Sub = sub };
            var index = _nav.Count;
            nav.Click += (_, _) => SelectPage(index);
            stack.Controls.Add(nav);
            _nav.Add(nav);
        }

        // The version and its release date; the author is on the General page.
        var about = new Label
        {
            Text = ReleaseDate is null ? $"Version {AppVersion}" : $"Version {AppVersion}\n{ReleaseDate}",
            Font = Theme.Small,
            ForeColor = Theme.Neutral,
            BackColor = Theme.Sidebar,
            Dock = DockStyle.Bottom,
            Height = 64,
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
        _saveAndClose.Click += (_, _) => { if (Save()) Close(); };
        _discard.Click += (_, _) => Discard();
        AcceptButton = _save;

        // With unsaved changes: Discard · Save & Close · Save. Without: Close. (Esc closes either way, asking first.)
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Theme.Surface };
        _close.Margin = Padding.Empty;
        buttons.Controls.Add(_close);
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_saveAndClose);
        buttons.Controls.Add(_discard);

        _saveState.Dock = DockStyle.Left;
        _saveState.Padding = new Padding(0, 9, 0, 0);

        footer.Controls.Add(_saveState);
        footer.Controls.Add(buttons);
        return footer;
    }

    private void SelectPage(int index)
    {
        // Swap pages with drawing turned off, then repaint once, so a page never appears piece by piece.
        var redraw = _content.IsHandleCreated;
        if (redraw)
            SendMessage(_content.Handle, WmSetRedraw, 0, 0);

        var shown = Page(index); // built now if it's the first time
        for (var i = 0; i < _pages.Length; i++)
        {
            _nav[i].Selected = i == index;
            if (_pages[i] is { } page)
                page.Panel.Visible = page == shown;
        }
        shown.OnShowing();

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

    private IEnumerable<SettingsPage> BuiltPages => _pages.OfType<SettingsPage>();

    /// <summary>The page, built (with the saved settings) the first time it's needed.</summary>
    private SettingsPage Page(int index)
    {
        if (_pages[index] is { } built)
            return built;
        var page = _makePage[index]();
        _pages[index] = page;
        // Built after the window scaled itself to the display (e.g. 125%): sizes in the page are for 100%, so scale
        // them the same way (fonts are in points and already right). Otherwise the cards end short of the right edge.
        if (_autoScaled && DeviceDpi != 96)
            page.Panel.Scale(new SizeF(DeviceDpi / 96f, DeviceDpi / 96f));
        var wasLoading = _loading;
        _loading = true; // loading the saved values isn't a change
        page.Panel.Visible = false;
        _content.Controls.Add(page.Panel);
        page.Load(Result);
        _loading = wasLoading;
        if (IsHandleCreated)
        {
            CreateHandles(page.Panel);
            Theme.ApplyWindowChrome(page.Panel, Theme.Current.Dark); // dark scroll bars and drop-down lists in dark themes
        }
        return page;
    }

    private GeneralPage MakeGeneralPage()
    {
        var general = new GeneralPage(_context, ReleaseDate is null ? AppVersion : $"{AppVersion}, released {ReleaseDate}");
        general.CheckForUpdatesClicked += () => CheckForUpdatesClicked?.Invoke();
        general.SendFeedbackClicked += () => SendFeedbackClicked?.Invoke();
        general.OpenLogFolderClicked += () => OpenLogFolderClicked?.Invoke();
        general.ResetRequested += ResetToDefaults;
        if (_updateState is { } state)
            general.ShowUpdateState(state.Text, state.Enabled);
        return general;
    }

    /// <summary>Creates the window handles of the built pages up front, so showing them is instant.</summary>
    private static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls)
            CreateHandles(child);
    }

    // ------------------------------------------------------------------ save

    /// <summary>Settings built from all pages as they are right now (possibly not saved yet).</summary>
    private Settings Current()
    {
        var settings = Result.Clone();
        foreach (var page in BuiltPages) // pages not built yet show the saved values: nothing to add
            page.Apply(settings);
        return settings;
    }

    /// <summary>
    /// Shows the default settings, keeping the account, monitored groups and clients (and "Start with Windows", a Windows setting).
    /// It's an unsaved change like any other: Save keeps it, Close offers to discard it.
    /// </summary>
    internal void ResetToDefaults()
    {
        var current = Current();
        var defaults = new Settings { Subdomain = current.Subdomain, WatchedGroups = current.WatchedGroups };
        for (var i = 0; i < _pages.Length; i++)
            Page(i); // all of them, to show the defaults (rare enough)
        foreach (var page in BuiltPages.Where(p => p is not AccountPage and not GroupsPage and not ClientsPage and not GeneralPage))
            page.Load(defaults);
        General.ResetStartSummary();
        General.SelectAppTheme(defaults.AppTheme);
        Log.Info("Settings: reset to defaults (not saved yet)");
        UpdateSaveState();
    }

    /// <summary>For tests: what the pages show, written into fresh settings (catches a setting a page doesn't load or save).</summary>
    internal Settings PagesAsSettings()
    {
        var settings = new Settings();
        for (var i = 0; i < _pages.Length; i++)
            Page(i).Apply(settings);
        return settings;
    }

    private string Snapshot() => $"{Current().ToJson()}|startup={BuiltGeneral?.StartWithWindows ?? StartupRegistration.IsEnabled}";

    private bool HasUnsavedChanges => Snapshot() != _savedSnapshot;

    private void UpdateSaveState()
    {
        if (_loading)
            return;

        var dirty = HasUnsavedChanges;
        _save.Enabled = dirty;
        _save.Visible = _saveAndClose.Visible = _discard.Visible = dirty; // only offered when there's something to save
        _close.Visible = !dirty;
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

    /// <summary>Saves the settings and applies them right away; the window stays open.</summary>
    private bool Save()
    {
        var result = Current();

        if (result.IsConnectionConfigured && result.WatchedGroups.Count == 0 && !result.TrackMyTickets && !result.TrackCcTickets &&
            !ThemedDialog.Confirm(this, "Nothing will be watched",
                "No groups are monitored and ticket tracking is off, so Zendesk Watcher won't notify you about anything.", "Save anyway"))
        {
            return false;
        }

        try
        {
            result.Save();
            if (BuiltGeneral is { } general)
                StartupRegistration.Set(general.StartWithWindows);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            ThemedDialog.Inform(this, "Could not save settings", ex.Message, ThemedDialog.Kind.Error);
            return false;
        }

        Result = result;
        _savedSnapshot = Snapshot();
        _saveState.Text = $"✓ Saved at {DateTime.Now:HH:mm}";
        _saveState.ForeColor = Theme.Success;
        UpdateSaveState();
        Log.Info("Settings saved");
        Saved?.Invoke(result);
        // A batch of choices is done: ticked groups and changed clients move to their sections now.
        _groups.Resort();
        BuiltClients?.Resort();
        return true;
    }

    /// <summary>
    /// A client choice was made from a notification (already saved): the Clients page shows it, and it counts as
    /// saved, so it's neither an unsaved change nor lost when other changes are saved.
    /// </summary>
    internal void ClientChoiceSaved(ClientChoice choice)
    {
        var wasDirty = HasUnsavedChanges;
        Result = Result.Clone();
        Result.SetClientChoice(choice);
        BuiltClients?.SetChoice(choice); // not built yet: it shows Result when it is
        if (!wasDirty)
            _savedSnapshot = Snapshot();
        UpdateSaveState();
    }

    /// <summary>Back to the saved settings on every page (and the saved app theme); the window stays open.</summary>
    private void Discard()
    {
        _loading = true;
        foreach (var page in BuiltPages)
            page.Load(Result);
        _loading = false;
        _groups.Resort();
        BuiltClients?.Resort();
        _saveState.Text = "Changes discarded";
        _saveState.ForeColor = Theme.TextMuted;
        Log.Info("Settings: unsaved changes discarded");
        UpdateSaveState();
    }

    /// <summary>Esc closes the window (asking about unsaved changes), whichever buttons are showing.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// After a successful sign-in, saves the subdomain right away (only that), so the app watches this Zendesk even if
    /// the window is closed without Save. Other unsaved changes stay unsaved.
    /// </summary>
    private void SaveConnection()
    {
        var subdomain = _account.Subdomain;
        if (subdomain == Result.Subdomain && !Settings.IsFirstRun)
            return;

        var saved = Result.Clone();
        saved.Subdomain = subdomain;
        try
        {
            saved.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Could not save the subdomain after sign-in", ex);
            return;
        }
        Result = saved;

        // The saved snapshot gets the new subdomain too, so other pending changes still count as unsaved.
        var bar = _savedSnapshot.LastIndexOf('|');
        var snapshot = Settings.FromJson(_savedSnapshot[..bar]);
        snapshot.Subdomain = subdomain;
        _savedSnapshot = snapshot.ToJson() + _savedSnapshot[bar..];
        UpdateSaveState();

        Log.Info($"Settings: subdomain {subdomain} saved after sign-in");
        Saved?.Invoke(saved);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!HasUnsavedChanges)
            return;

        var answer = ThemedDialog.Show(this, "Save your changes?", "You have changes that aren't saved yet.", ThemedDialog.Kind.Question,
            new ThemedDialog.Choice("Save", DialogResult.Yes, Primary: true),
            new ThemedDialog.Choice("Don't save", DialogResult.No),
            new ThemedDialog.Choice("Cancel", DialogResult.Cancel));

        if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save()))
            e.Cancel = true;
    }

    // ------------------------------------------------------------------ screenshots (developer tool)

    /// <summary>
    /// Developer helper: opens the window in Clean light, switches to each appearance the way a tile click does
    /// (so the live recoloring is exercised), and saves each page as a PNG.
    /// </summary>
    internal static void RenderSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        Tracking.WatchStatus.Update(Tracking.WatchSnapshot.Sample()); // Insights' "Right now" card
        foreach (var theme in Enum.GetValues<NotificationTheme>())
        {
            var settings = new Settings
            {
                Subdomain = "example", Theme = NotificationTheme.CleanLight,
                HelpColleagues = true, HelpSameAsMonitored = true, HelpGroups = [new WatchedGroup { Id = 4, Name = "Onboarding" }],
                HelpExcludedAssignees = [new Colleague { Id = 9, Name = "Alex Example" }],
                WatchedGroups = [new WatchedGroup { Id = 1, Name = "Support Tier 2" }, new WatchedGroup { Id = 2, Name = "Premium Support" }],
                Clients = [new ClientChoice { Id = 23, Name = "Fabrikam", GroupId = 1, Mode = ClientMode.Important, IsMuted = true },
                    new ClientChoice { Id = 21, Name = "Contoso", GroupId = 1, Mode = ClientMode.Important, Sound = ToastSound.ChipFanfare },
                    new ClientChoice { Id = 25, Name = "Tailspin Toys", GroupId = 1, Mode = ClientMode.OnlyUrgent }]
            };
            ClientsView.SetSample(settings.Subdomain, new()
            {
                [1] = [new(21, "Contoso", 41), new(22, "Northwind Traders", 17), new(23, "Fabrikam", 9), new(24, "Adventure Works", 6),
                       new(25, "Tailspin Toys", 3), new(ClientChoice.NoClientId, ClientChoice.NoClientName, 4)],
                [2] = [new(21, "Contoso", 12), new(26, "Wide World Importers", 8), new(27, "Litware", 2)]
            });
            using var form = new SettingsForm(settings, _ => new SignedOutSession(), new Notifier((_, _) => { }) { Stats = StatisticsPage.SampleStats() });
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-5000, -5000); // off-screen; controls only paint once the window is shown
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            form._groups.ShowSample(new() { [3] = new Suggestion(false, 2), [4] = new Suggestion(true, 6) },
                [new GroupItem(1, "Support Tier 2"), new GroupItem(2, "Premium Support"), new GroupItem(3, "Billing"), new GroupItem(4, "Onboarding"), new GroupItem(5, "Partner Support")]);

            // Shown as if signed in, with sample group members (cached, so nothing is requested).
            form._context.SetSession(new ZendeskClient(settings, new SignedOutSession()), new User { Id = 99, Name = "Sample Agent" });
            form.Help.Card.ShowSample(new()
            {
                [1] = [new(9, "Alex Example", "Europe/Stockholm"), new(10, "Maria Svensson", "Europe/Stockholm"), new(11, "Jordan Lee", "America/New_York"),
                       new(12, "Priya Raman", "Asia/Kolkata"), new(13, "Sam Taylor", "Australia/Sydney")],
                [2] = [new(14, "Chris Morgan", "America/Los_Angeles"), new(15, "Nguyen Van An", "Asia/Ho_Chi_Minh"), new(16, "Robin Berg", null)]
            }, TimeRegion.EuropeAfrica);
            form.General.SelectAppTheme(theme);
            for (var page = 0; page < form._pages.Length; page++)
            {
                form.SelectPage(page);
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(dir, $"{theme}-{page}.png"));

                // And the bottom of long pages.
                if (form._content.VerticalScroll.Visible)
                {
                    form._content.AutoScrollPosition = new Point(0, form._content.DisplayRectangle.Height / 2);
                    Application.DoEvents();
                    using var middle = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(middle, new Rectangle(Point.Empty, middle.Size));
                    middle.Save(Path.Combine(dir, $"{theme}-{page}-middle.png"));

                    form._content.AutoScrollPosition = new Point(0, form._content.DisplayRectangle.Height);
                    Application.DoEvents();
                    using var bottom = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bottom, new Rectangle(Point.Empty, bottom.Size));
                    bottom.Save(Path.Combine(dir, $"{theme}-{page}-bottom.png"));
                    form._content.AutoScrollPosition = Point.Empty;
                }
            }
            // Groups, Clients and Colleagues while signed out: the strip, read-only lists.
            form._context.SetSignedOut(true);
            foreach (var index in new[] { 1, ClientsIndex, HelpIndex })
            {
                form.SelectPage(index);
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(dir, $"{theme}-{index}-signedout.png"));
            }
            form._context.SetSignedOut(false);
            form.Hide();
        }
    }

    /// <summary>
    /// Developer helper: shows the window on <paramref name="page"/>, leaves it idle for <paramref name="seconds"/> and
    /// writes the CPU time it used to <paramref name="file"/> (to catch pages that keep repainting).
    /// </summary>
    internal static void MeasureIdle(int page, int seconds, string file)
    {
        var settings = new Settings { Subdomain = "example", HelpColleagues = true, WatchedGroups = [new WatchedGroup { Id = 1, Name = "Support Tier 2" }] };
        var opening = System.Diagnostics.Stopwatch.StartNew();
        using var form = new SettingsForm(settings, _ => new SignedOutSession(), new Notifier((_, _) => { }) { Stats = StatisticsPage.SampleStats() });
        var built = opening.ElapsedMilliseconds;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-5000, -5000); // off-screen: the mouse and keyboard can't reach it
        form.ShowInTaskbar = false;
        form.Show();
        Application.DoEvents();
        var shown = opening.ElapsedMilliseconds;
        static long MemoryMb() { GC.Collect(); GC.WaitForPendingFinalizers(); using var me = System.Diagnostics.Process.GetCurrentProcess(); return me.PrivateMemorySize64 / 1024 / 1024; }
        var memoryOpen = MemoryMb();
        // How long switching to each page takes (built, laid out and painted).
        var switches = new List<string>();
        for (var i = 0; i < form._pages.Length; i++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            form.SelectPage(i);
            Application.DoEvents();
            switches.Add($"{i}:{clock.ElapsedMilliseconds}");
        }
        // Opening it again in the same run, as in the app after the first time (code already loaded).
        var again = System.Diagnostics.Stopwatch.StartNew();
        using (var second = new SettingsForm(settings, _ => new SignedOutSession(), new Notifier((_, _) => { })) { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false })
        {
            second.Show();
            Application.DoEvents();
            second.Hide();
        }
        var memoryAll = MemoryMb();
        File.AppendAllText(file, $"window built {built} ms, shown {shown} ms, again {again.ElapsedMilliseconds} ms; page switch ms {string.Join(" ", switches)}; " +
            $"private memory {memoryOpen} MB open, {memoryAll} MB after every page{Environment.NewLine}");
        form.SelectPage(page);
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var counter = new MessageCounter();
        Application.AddMessageFilter(counter);
        form._content.Layout += (_, e) => counter.Count($"layout:content({e.AffectedProperty}/{e.AffectedControl?.GetType().Name})");
        form.Page(page).Panel.SizeChanged += (_, _) => counter.Count("size:page");
        form._content.Scroll += (_, _) => counter.Count("scroll");
        var start = DateTime.UtcNow;
        TimeSpan? cpuStart = null;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(seconds + 3))
        {
            Application.DoEvents();
            Thread.Sleep(10);
            if (cpuStart is null && DateTime.UtcNow - start > TimeSpan.FromSeconds(3)) // after start-up
            {
                process.Refresh();
                cpuStart = process.TotalProcessorTime;
                counter.Counts.Clear();
            }
        }
        process.Refresh();
        var used = process.TotalProcessorTime - cpuStart!.Value;
        File.AppendAllText(file, $"page {page}: {used.TotalMilliseconds:0} ms CPU in {seconds} s = {used.TotalMilliseconds / 10 / seconds:0.00}% of one core; " +
            string.Join(", ", counter.Counts.OrderByDescending(c => c.Value).Take(6).Select(c => $"{c.Key} {c.Value}")) + Environment.NewLine);
        form.Hide();
    }

    /// <summary>For <see cref="MeasureIdle"/>: counts paint and timer messages per control type.</summary>
    private sealed class MessageCounter : IMessageFilter
    {
        public Dictionary<string, int> Counts { get; } = [];

        public void Count(string key) => Counts[key] = Counts.GetValueOrDefault(key) + 1;

        public bool PreFilterMessage(ref Message m)
        {
            var kind = m.Msg switch { 0x000F => "paint", 0x0113 => "timer", _ => null };
            if (kind is not null)
            {
                var key = $"{kind}:{Control.FromHandle(m.HWnd)?.GetType().Name ?? "?"}";
                Counts[key] = Counts.GetValueOrDefault(key) + 1;
            }
            return false;
        }
    }

    /// <summary>For <see cref="RenderSamples"/>: always signed out, so nothing is requested or logged.</summary>
    private sealed class SignedOutSession : IZendeskSession
    {
        public bool MightBeSignedIn => false;
        public Task<ApiResponse> GetAsync(string url, CancellationToken ct) => Task.FromResult(new ApiResponse(401, "", null));
        public Task<ApiResponse> SendAsync(string method, string url, string jsonBody, CancellationToken ct) => GetAsync(url, ct);
        public Task<bool> TryRecoverAsync(CancellationToken ct) => Task.FromResult(false);
        public Task SignInAsync(object? owner, CancellationToken ct) => Task.FromCanceled(new CancellationToken(true));
        public void SignOut() { }
    }
}
