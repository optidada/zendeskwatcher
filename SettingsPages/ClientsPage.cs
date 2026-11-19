using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// Clients (under Groups): the clients of the monitored groups, all Normal by default. A click on a client sets
/// Important, Only urgent, Mute (for a while) or its own sound; "Options ▾" on a notification does the same.
/// </summary>
internal sealed class ClientsPage : SettingsPage
{
    private const int ListWidth = Kit.CardWidth - 44;

    private readonly GroupsPage _groups;
    private readonly ClientsView _clients = new(ListWidth);
    private readonly InputBox _filter = new(ListWidth, "Filter by client or product…", searchIcon: true);

    /// <summary>Which clients show (a view, like the name filter): "worked with" = a ticket of mine in the group in 90 days.</summary>
    private readonly ChoicePills _worked = new("All", "Worked with", "Not worked with") { Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _hint = Theme.Label("", Theme.Small, Theme.TextMuted);
    private readonly Button _reload = Theme.SecondaryButton("Reload");
    private readonly Label _clear = Kit.LinkLabel("Clear customizations", Theme.Small);

    public ClientsPage(SettingsContext context, GroupsPage groups)
        : base(context, "Clients", "Untick a client, or a whole group, to mute it. Options: important, only urgent, its own sound or mute for a while.")
    {
        _groups = groups;
        var card = new Card("Clients of your monitored groups",
            $"Clients from the last {ZendeskClient.ClientLookBackDays} days of tickets. Muting never affects your own or " +
                $"CC'd tickets. Important clients notify even outside your hours and for priorities you left out, and stay until " +
                $"closed.", Kit.CardWidth);
        _clients.Host.Size = new Size(ListWidth, 280);
        _clients.Host.Margin = new Padding(0, 10, 0, 10);
        _clients.Host.Visible = true;
        card.Body.Controls.Add(_filter);
        card.Body.Controls.Add(_worked);
        card.Body.Controls.Add(_clients.Host);

        // Full card width, so the Reload button sits at the right edge.
        var footer = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, MinimumSize = new Size(ListWidth, 0), MaximumSize = new Size(ListWidth, 0),
            BackColor = Theme.Surface, Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _clear.Anchor = AnchorStyles.Right;
        _clear.Margin = new Padding(8, 0, 12, 0);
        _clear.Visible = false;
        _hint.Anchor = AnchorStyles.Left;
        _reload.Margin = Padding.Empty;
        _reload.Anchor = AnchorStyles.Right;
        footer.Controls.Add(_hint, 0, 0);
        footer.Controls.Add(_clear, 1, 0);
        footer.Controls.Add(_reload, 2, 0);
        card.Body.Controls.Add(footer);
        Panel.Controls.Add(card);

        _filter.TextChanged += (_, _) => _clients.Filter(_filter.Text);
        _worked.SelectedIndexChanged += (_, _) => _clients.FilterWorked((ClientsView.Worked)_worked.SelectedIndex);
        _clients.Changed += Changed;
        _clients.HintChanged += () =>
        {
            _hint.Text = _clients.Hint;
            _clear.Visible = _clients.CustomizedCount > 0;
        };
        _clear.Click += (_, _) =>
        {
            var count = _clients.CustomizedCount;
            if (count > 0 && ThemedDialog.Confirm(Context.Window, "Clear customizations?",
                    $"{count} client{(count == 1 ? "" : "s")} go back to normal (no mutes, important or own sounds). " +
                    "Discard undoes it until you save.", "Clear"))
                _clients.ClearAll();
        };
        _reload.Click += (_, _) =>
        {
            ClientsView.ClearCache();
            Refresh();
        };
        UseSignedOutStrip(SetReadOnly);
        context.SessionUpdated += reload =>
        {
            if (reload)
                ClientsView.ClearCache();
            // Also while the page is hidden: signed in, the clients load in the background when they're missing or
            // over 15 minutes old (only what changed: usually 1 request per group), so the page is ready when opened.
            if (Panel.Visible || Context.Client is not null)
                Refresh();
        };
    }

    public override bool IsSubPage => true; // under Groups

    private void SetReadOnly(bool readOnly)
    {
        _clients.ReadOnly = readOnly;
        _clear.Enabled = !readOnly;
    }

    public override void Load(Settings settings)
    {
        _clients.Load(settings);
        if (Panel.Visible)
            Refresh();
    }

    public override void Apply(Settings settings) => _clients.Apply(settings);

    /// <summary>Monitored groups may have changed on the Groups page; the list fills the window, only it scrolls.</summary>
    public override void OnShowing()
    {
        FitList();
        Refresh();
    }

    public override void OnWindowResized() => FitList();

    /// <summary>The list as tall as the window allows (at least 120 px), so the page itself doesn't scroll.</summary>
    private void FitList()
    {
        var content = Context.Content;
        Panel.PerformLayout();
        var available = content.ClientSize.Height - content.Padding.Vertical;
        var minimum = (int)(120 * Context.Window.DeviceDpi / 96f);
        var height = Math.Max(minimum, _clients.Host.Height + available - Panel.PreferredSize.Height);
        if (height != _clients.Host.Height)
        {
            _clients.Host.Height = height;
            Panel.PerformLayout();
        }
    }

    private async void Refresh()
    {
        try
        {
            _reload.Enabled = Context.Client is not null;
            _reload.SetBusy(true, "Loading\u2026");
            _clients.MeId = Context.Me?.Id;
            await _clients.ShowAsync(_groups.MonitoredGroups(), Context.Client, Context.Current().Subdomain);
        }
        catch (Exception ex)
        {
            Log.Error("Settings: could not show the clients", ex);
        }
        finally
        {
            _reload.SetBusy(false);
        }
    }

    /// <summary>After Save or Discard: clients move to their sections (Customized, groups, Muted).</summary>
    public void Resort() => _clients.Resort();

    /// <summary>After Save or Discard: the filter starts empty again (all clients, in their new sections).</summary>
    public void ClearFind() => _filter.Text = "";

    /// <summary>For the screenshot tool: the list filtered by this text.</summary>
    internal void ShowFilter(string text) => _filter.Text = text;

    /// <summary>A choice made from a notification while Settings is open.</summary>
    public void SetChoice(ClientChoice choice) => _clients.SetChoice(choice);
}
