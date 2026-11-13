using AppKit;
using Foundation;
using ZendeskWatcher.Tracking;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Mac;

/// <summary>
/// The menu bar icon with the unassigned count, its menu (account, groups, sign-in, feedback) and the check loop: the
/// same TicketWatcher as on Windows, with the settings file in ~/Library/Application Support/ZendeskWatcher.
/// </summary>
public sealed class AppDelegate : NSApplicationDelegate
{
    private Settings _settings = Settings.Load();
    private readonly WebSession _session = new();
    private MacNotifier _notifier = null!;
    private NSStatusItem _item = null!;
    private readonly NSMenu _groupsMenu = new("Groups");
    private readonly NSMenuItem _status = new("Starting…") { Enabled = false };
    private readonly NSMenuItem _signIn = new("Sign in…");
    private CancellationTokenSource? _loopCts;
    private readonly SemaphoreSlim _wakeUp = new(0, 1);
    private List<Group> _allGroups = [];

    public override void DidFinishLaunching(NSNotification notification)
    {
        _notifier = new MacNotifier(() => _settings);
        _item = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Variable);
        _item.Button.Image = NSImage.GetSystemSymbol("ticket", "Zendesk Watcher");
        _item.Button.ImagePosition = NSCellImagePosition.ImageLeading;
        _item.Menu = BuildMenu();
        SetCount(null);
        Start();
    }

    private NSMenu BuildMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(_status);
        menu.AddItem(Item("Check now", () => Wake()));
        menu.AddItem(Item("Open Zendesk", () => { if (_settings.IsConnectionConfigured) MacNotifier.Open(_settings.BaseUrl); }));
        menu.AddItem(NSMenuItem.SeparatorItem);

        var groups = new NSMenuItem("Groups to watch") { Submenu = _groupsMenu };
        menu.AddItem(groups);
        RebuildGroupsMenu();
        menu.AddItem(Item("Zendesk account…", AskSubdomain));
        _signIn.Activated += (_, _) => _ = SignInOrOutAsync();
        menu.AddItem(_signIn);
        menu.AddItem(NSMenuItem.SeparatorItem);

        menu.AddItem(Item("Send feedback…", () => MacFeedback.Send(_notifier)));
        menu.AddItem(Item("Open log folder", () => NSWorkspace.SharedWorkspace.OpenUrl(NSUrl.FromFilename(AppPaths.DataDir))));
        menu.AddItem(Item($"Quit Zendesk Watcher {AppInfo.Version}", () => NSApplication.SharedApplication.Terminate(this)));
        return menu;
    }

    private static NSMenuItem Item(string title, Action action)
    {
        var item = new NSMenuItem(title);
        item.Activated += (_, _) => action();
        return item;
    }

    private void SetCount(int? unassigned)
    {
        _item.Button.Title = unassigned is > 0 ? $" {unassigned}" : "";
        _item.Button.ToolTip = $"Zendesk Watcher · {_status.Title}";
    }

    private void SetStatus(string text, int? unassigned = null)
    {
        _status.Title = text;
        SetCount(unassigned);
    }

    // ------------------------------------------------------------------ loop

    private void Start()
    {
        _loopCts?.Cancel();
        _loopCts = null;
        if (!_settings.IsConnectionConfigured)
        {
            SetStatus("Not set up: choose Zendesk account…");
            _signIn.Title = "Sign in…";
            return;
        }
        _session.Configure(_settings.BaseUrl);
        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(_loopCts.Token);
    }

    private void Wake()
    {
        if (_wakeUp.CurrentCount == 0)
            _wakeUp.Release();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var zendesk = new ZendeskClient(_settings, _session);
        var watcher = new TicketWatcher(_settings, zendesk, _notifier);
        var initialized = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    SetStatus("Connecting…");
                    await watcher.InitializeAsync(ct);
                    initialized = true;
                    _signIn.Title = $"Sign out ({watcher.Me?.Name})";
                    await LoadGroupsAsync(zendesk, ct);
                    if (_settings.WatchedGroups.Count == 0)
                        _notifier.Info("Choose your groups", "Signed in. Pick the groups to watch: menu bar icon → Groups to watch.");
                }

                await watcher.PollAsync(ct);
                WatchStatus.Update(watcher.Snapshot());
                SetStatus($"{watcher.UnassignedCount} unassigned · {watcher.MyOpenCount} mine · {DateTime.Now:HH:mm}", watcher.UnassignedCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (AuthRequiredException)
            {
                Log.Info("Sign-in required");
                SetStatus("Sign-in required");
                _signIn.Title = "Sign in…";
                WatchStatus.Stopped("Not checking: sign-in required.");
                _notifier.Info("Sign in to Zendesk", "Your Zendesk session has expired. Menu bar icon → Sign in…");
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Poll failed", ex);
                SetStatus($"Error, retrying: {ex.Message}");
                WatchStatus.Failed($"The last check failed and is retried: {ex.Message}");
            }

            try
            {
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), ct), _wakeUp.WaitAsync(ct));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SignInOrOutAsync()
    {
        if (_signIn.Title.StartsWith("Sign out", StringComparison.Ordinal))
        {
            _loopCts?.Cancel();
            _loopCts = null;
            _session.SignOut();
            _signIn.Title = "Sign in…";
            SetStatus("Signed out");
            WatchStatus.Stopped("Signed out.");
            return;
        }
        if (!_settings.IsConnectionConfigured)
        {
            AskSubdomain();
            return;
        }
        try
        {
            _session.Configure(_settings.BaseUrl);
            await _session.SignInAsync(null, CancellationToken.None);
            Start();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Not signed in");
        }
        catch (Exception ex)
        {
            Log.Error("Sign-in failed", ex);
            _notifier.Info("Sign-in didn't work", ex.Message);
        }
    }

    // ------------------------------------------------------------------ settings in the menu

    private void AskSubdomain()
    {
        var input = new NSTextField(new CoreGraphics.CGRect(0, 0, 260, 24))
        {
            StringValue = _settings.Subdomain,
            PlaceholderString = "yourcompany"
        };
        var alert = new NSAlert
        {
            MessageText = "Zendesk account",
            InformativeText = "The first part of your Zendesk address: yourcompany for yourcompany.zendesk.com.",
            AccessoryView = input
        };
        alert.AddButton("Save");
        alert.AddButton("Cancel");
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        alert.Window.InitialFirstResponder = input;
        if (alert.RunModal() != (nint)(long)NSAlertButtonReturn.First)
            return;

        var subdomain = Settings.NormalizeSubdomain(input.StringValue);
        if (subdomain == _settings.Subdomain)
            return;
        _settings.Subdomain = subdomain;
        _settings.WatchedGroups.Clear();
        _settings.Save();
        _allGroups = [];
        RebuildGroupsMenu();
        _ = SignInOrOutAsync();
    }

    private async Task LoadGroupsAsync(ZendeskClient zendesk, CancellationToken ct)
    {
        try
        {
            _allGroups = (await zendesk.GetGroupsAsync(ct)).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or AuthRequiredException))
        {
            Log.Error("Could not load groups", ex);
        }
        RebuildGroupsMenu();
    }

    private void RebuildGroupsMenu()
    {
        _groupsMenu.RemoveAllItems();
        var known = _allGroups.Select(g => (g.Id, g.Name)).ToList();
        foreach (var watched in _settings.WatchedGroups.Where(w => known.All(k => k.Id != w.Id)))
            known.Add((watched.Id, watched.Name)); // saved, not loaded (yet)
        known.Add((WatchedGroup.NoGroupId, WatchedGroup.NoGroupName));

        if (known.Count == 1 && _allGroups.Count == 0)
            _groupsMenu.AddItem(new NSMenuItem("Sign in to load your groups") { Enabled = false });

        foreach (var (id, name) in known)
        {
            var item = Item(name, () => ToggleGroup(id, name));
            item.State = _settings.WatchedGroups.Any(g => g.Id == id) ? NSCellStateValue.On : NSCellStateValue.Off;
            _groupsMenu.AddItem(item);
        }
    }

    private void ToggleGroup(long id, string name)
    {
        if (_settings.WatchedGroups.RemoveAll(g => g.Id == id) == 0)
            _settings.WatchedGroups.Add(new WatchedGroup { Id = id, Name = name });
        _settings.Save();
        RebuildGroupsMenu();
        Start(); // a fresh watcher with the new groups
    }
}
