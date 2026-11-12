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

    private readonly bool _knownSignedOut;
    private readonly SettingsContext _context;
    private readonly AccountPage _account;
    private readonly GroupsPage _groups;
    private readonly NotificationsPage _notifications;
    private readonly HelpColleaguesPage _help;
    private readonly AppearancePage _appearance;
    private readonly StatisticsPage _statistics;
    private readonly GeneralPage _general;
    private readonly SettingsPage[] _pages;
    private readonly List<NavItem> _nav = [];

    private readonly Button _save = Theme.PrimaryButton("Save");
    private readonly Button _close = Theme.SecondaryButton("Close");
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
    public event Action? SendFeedbackClicked;
    public event Action? OpenLogFolderClicked;

    /// <summary>Mirrors the tray menu's update item ("Checking for updates…", "Restart to update to …").</summary>
    public void ShowUpdateState(string text, bool enabled) => _general.ShowUpdateState(text, enabled);

    public SettingsForm(Settings settings, Func<Settings, IZendeskSession> sessionFactory, Notifier notifier, bool knownSignedOut = false)
        : this(UseThemeOf(settings), sessionFactory, notifier, knownSignedOut, 0)
    {
    }

    /// <summary>
    /// Applies the saved appearance before any control exists: field initializers create controls, and they run
    /// before the constructor body, so this has to happen in the arguments of the chained constructor.
    /// </summary>
    private static Settings UseThemeOf(Settings settings)
    {
        Theme.Use(settings.Theme);
        return settings;
    }

    private SettingsForm(Settings settings, Func<Settings, IZendeskSession> sessionFactory, Notifier notifier, bool knownSignedOut, int _)
    {
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

        _context = new SettingsContext(this, _content, notifier, sessionFactory) { Current = Current, Changed = UpdateSaveState };
        _groups = new GroupsPage(_context);
        _account = new AccountPage(_context, _groups);
        _notifications = new NotificationsPage(_context);
        _help = new HelpColleaguesPage(_context, _groups);
        _appearance = new AppearancePage(_context);
        _statistics = new StatisticsPage(_context);
        _general = new GeneralPage(_context, AppVersion);
        _pages = [_account, _groups, _notifications, _help, _appearance, _statistics, _general];

        _account.SessionChanging += () => SessionChanging?.Invoke();
        _account.SessionChanged += () => SessionChanged?.Invoke();
        _account.SignedIn += SaveConnection;
        _general.CheckForUpdatesClicked += () => CheckForUpdatesClicked?.Invoke();
        _general.SendFeedbackClicked += () => SendFeedbackClicked?.Invoke();
        _general.OpenLogFolderClicked += () => OpenLogFolderClicked?.Invoke();
        _general.ResetRequested += ResetToDefaults;

        BuildLayout();
        foreach (var page in _pages)
            page.Load(settings);
        SelectPage(0);

        ResumeLayout(false);
        PerformLayout();
        Load += (_, _) =>
        {
            foreach (var page in _pages) CreateHandles(page.Panel);
            Theme.ApplyWindowChrome(this, Theme.Current.Dark);
        };
        Shown += async (_, _) => await _account.OnWindowShownAsync(_knownSignedOut);
        FormClosing += OnFormClosing;

        _savedSnapshot = Snapshot();
        _loading = false;
        UpdateSaveState();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var sidebar = BuildSidebar();
        var footer = BuildFooter();

        // Docked to the top in reverse, so the first page ends up on top (only one is visible at a time anyway).
        foreach (var page in _pages.Reverse())
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

        foreach (var page in _pages)
        {
            var nav = new NavItem(page.Title);
            var index = _nav.Count;
            nav.Click += (_, _) => SelectPage(index);
            stack.Controls.Add(nav);
            _nav.Add(nav);
        }

        // Just the version; the author is on the General page.
        var about = new Label
        {
            Text = $"Version {AppVersion}",
            Font = Theme.Small,
            ForeColor = Theme.Neutral,
            BackColor = Theme.Sidebar,
            Dock = DockStyle.Bottom,
            Height = 48,
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

    private void SelectPage(int index)
    {
        // Swap pages with drawing turned off, then repaint once, so a page never appears piece by piece.
        var redraw = _content.IsHandleCreated;
        if (redraw)
            SendMessage(_content.Handle, WmSetRedraw, 0, 0);

        for (var i = 0; i < _pages.Length; i++)
        {
            _nav[i].Selected = i == index;
            _pages[i].Panel.Visible = i == index;
        }
        _pages[index].OnShowing();

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

    // ------------------------------------------------------------------ save

    /// <summary>Settings built from all pages as they are right now (possibly not saved yet).</summary>
    private Settings Current()
    {
        var settings = Result.Clone();
        foreach (var page in _pages)
            page.Apply(settings);
        return settings;
    }

    /// <summary>
    /// Shows the default settings, keeping the account and monitored groups (and "Start with Windows", a Windows setting).
    /// It's an unsaved change like any other: Save keeps it, Close offers to discard it.
    /// </summary>
    internal void ResetToDefaults()
    {
        var current = Current();
        var defaults = new Settings { Subdomain = current.Subdomain, WatchedGroups = current.WatchedGroups };
        foreach (var page in _pages.Where(p => p is not AccountPage and not GroupsPage and not GeneralPage))
            page.Load(defaults);
        _general.ResetStartSummary();
        Log.Info("Settings: reset to defaults (not saved yet)");
        UpdateSaveState();
    }

    /// <summary>For tests: what the pages show, written into fresh settings (catches a setting a page doesn't load or save).</summary>
    internal Settings PagesAsSettings()
    {
        var settings = new Settings();
        foreach (var page in _pages)
            page.Apply(settings);
        return settings;
    }

    private string Snapshot() => $"{Current().ToJson()}|startup={_general.StartWithWindows}";

    private bool HasUnsavedChanges => Snapshot() != _savedSnapshot;

    private void UpdateSaveState()
    {
        if (_loading)
            return;

        var dirty = HasUnsavedChanges;
        _save.Enabled = dirty;
        _save.Visible = dirty; // only offered when there's something to save (a disabled flat button still looks clickable)
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
            StartupRegistration.Set(_general.StartWithWindows);
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
        return true;
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
        foreach (var theme in Enum.GetValues<NotificationTheme>())
        {
            var settings = new Settings
            {
                Subdomain = "example", Theme = NotificationTheme.CleanLight,
                HelpColleagues = true, HelpSameAsMonitored = true, HelpGroups = [new WatchedGroup { Id = 4, Name = "Onboarding" }],
                HelpExcludedAssignees = [new Colleague { Id = 9, Name = "Alex Example" }],
                WatchedGroups = [new WatchedGroup { Id = 1, Name = "Support Tier 2" }, new WatchedGroup { Id = 2, Name = "Premium Support" }]
            };
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
            form._help.Card.ShowSample(new()
            {
                [1] = [new(9, "Alex Example", "Europe/Stockholm"), new(10, "Maria Svensson", "Europe/Stockholm"), new(11, "Jordan Lee", "America/New_York"),
                       new(12, "Priya Raman", "Asia/Kolkata"), new(13, "Sam Taylor", "Australia/Sydney")],
                [2] = [new(14, "Chris Morgan", "America/Los_Angeles"), new(15, "Nguyen Van An", "Asia/Ho_Chi_Minh"), new(16, "Robin Berg", null)]
            }, TimeRegion.EuropeAfrica);
            form._appearance.SelectTheme(theme);
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
        using var form = new SettingsForm(settings, _ => new SignedOutSession(), new Notifier((_, _) => { }) { Stats = StatisticsPage.SampleStats() });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-5000, -5000); // off-screen: the mouse and keyboard can't reach it
        form.ShowInTaskbar = false;
        form.Show();
        form.SelectPage(page);
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var counter = new MessageCounter();
        Application.AddMessageFilter(counter);
        form._content.Layout += (_, e) => counter.Count($"layout:content({e.AffectedProperty}/{e.AffectedControl?.GetType().Name})");
        form._pages[page].Panel.SizeChanged += (_, _) => counter.Count("size:page");
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
        public Task SignInAsync(IWin32Window? owner, CancellationToken ct) => Task.FromCanceled(new CancellationToken(true));
        public void SignOut() { }
    }
}
