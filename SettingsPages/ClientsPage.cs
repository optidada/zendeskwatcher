using ZendeskWatcher.UI;
using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// Clients (under Groups): the clients of the monitored groups, all Normal by default. A click on a client sets
/// Important, Only urgent, Mute (for a while) or its own sound; "Mute ▾" on a notification does the same.
/// </summary>
internal sealed class ClientsPage : SettingsPage
{
    private const int ListWidth = Kit.CardWidth - 44;

    private readonly GroupsPage _groups;
    private readonly ClientsView _clients = new(ListWidth);
    private readonly InputBox _filter = new(ListWidth, "Find a client…  (Enter: next)", searchIcon: true);
    private readonly Label _hint = Theme.Label("", Theme.Small, Theme.TextMuted);
    private readonly Button _reload = Theme.SecondaryButton("Reload");

    public ClientsPage(SettingsContext context, GroupsPage groups)
        : base(context, "Clients", "Click a client to make it important, hear only its urgent tickets, mute it (for a while) or give it its own sound. " +
            "Untick to mute. Everyone is normal by default, new clients too.")
    {
        _groups = groups;
        var card = new Card("Clients of your monitored groups",
            $"From each group's tickets of the last {ZendeskClient.ClientLookBackDays} days. Muted and \u201conly urgent\u201d apply to new tickets " +
            "and colleagues' tickets; your own and CC'd tickets always notify. Important ones always notify and stay until closed.", Kit.CardWidth);
        _clients.Host.Size = new Size(ListWidth, 280);
        _clients.Host.Margin = new Padding(0, 10, 0, 10);
        _clients.Host.Visible = true;
        card.Body.Controls.Add(_filter);
        card.Body.Controls.Add(_clients.Host);

        // Full card width, so the Reload button sits at the right edge.
        var footer = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, MinimumSize = new Size(ListWidth, 0), MaximumSize = new Size(ListWidth, 0),
            BackColor = Theme.Surface, Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _hint.Anchor = AnchorStyles.Left;
        _reload.Margin = Padding.Empty;
        _reload.Anchor = AnchorStyles.Right;
        footer.Controls.Add(_hint, 0, 0);
        footer.Controls.Add(_reload, 1, 0);
        card.Body.Controls.Add(footer);
        Panel.Controls.Add(card);

        _filter.TextChanged += (_, _) => Find(next: false);
        _filter.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true; // no beep
            Find(next: true);
        };
        _clients.Changed += Changed;
        _clients.HintChanged += () => _hint.Text = _clients.Hint;
        _reload.Click += (_, _) =>
        {
            ClientsView.ClearCache();
            Refresh();
        };
        context.SessionUpdated += reload =>
        {
            if (reload)
                ClientsView.ClearCache();
            if (Panel.Visible)
                Refresh();
        };
    }

    public override bool IsSubPage => true; // under Groups

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
        Refresh();
    }

    private async void Refresh()
    {
        try
        {
            _reload.Enabled = Context.Client is not null;
            _reload.SetBusy(true, "Loading\u2026");
            await _clients.ShowAsync(_groups.MonitoredGroups(), Context.Client, Context.Current().Subdomain);
            if (_filter.Text.Trim().Length > 0)
                Find(next: false);
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

    /// <summary>Goes to a matching client and highlights it; the text turns orange when nothing matches.</summary>
    private void Find(bool next) => _filter.Box.ForeColor = _clients.Find(_filter.Text, next) ? Theme.Text : Theme.Warning;

    /// <summary>After Save or Discard: clients move to their sections (Customized, groups, Muted).</summary>
    public void Resort() => _clients.Resort();

    /// <summary>A choice made from a notification while Settings is open.</summary>
    public void SetChoice(ClientChoice choice) => _clients.SetChoice(choice);
}
