using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.SettingsPages;

/// <summary>What the Settings pages share: the window, the signed-in session, and the "something changed" signal.</summary>
internal sealed class SettingsContext(Form window, Panel content, Notifier notifier, Func<Settings, IZendeskSession> sessionFactory)
{
    /// <summary>The Settings window: owner of message boxes, and what a theme change recolors.</summary>
    public Form Window { get; } = window;

    /// <summary>The scrolling area the pages are shown in.</summary>
    public Panel Content { get; } = content;

    public Notifier Notifier { get; } = notifier;
    public Func<Settings, IZendeskSession> SessionFactory { get; } = sessionFactory;

    /// <summary>Settings built from all pages as they are right now (possibly not saved yet).</summary>
    public Func<Settings> Current { get; set; } = () => new Settings();

    /// <summary>Call after any change, so the window can update "Unsaved changes".</summary>
    public Action Changed { get; set; } = () => { };

    /// <summary>The signed-in session, for loading groups and group members; null when not signed in.</summary>
    public ZendeskClient? Client { get; private set; }

    public User? Me { get; private set; }

    /// <summary>Signed in or out. True when the user asked to reload, so cached Zendesk data is fetched again.</summary>
    public event Action<bool>? SessionUpdated;

    public void SetSession(ZendeskClient? client, User? me, bool reload = false)
    {
        if (client is null && Client is null)
            return; // still signed out: nothing to refresh
        Client = client;
        Me = client is null ? null : me;
        SessionUpdated?.Invoke(reload);
    }
}

/// <summary>
/// One page of the Settings window. It builds its controls, shows the values from <see cref="Settings"/>
/// (<see cref="Load"/>), and writes the user's choices back (<see cref="Apply"/>); the window does the saving.
/// </summary>
internal abstract class SettingsPage(SettingsContext context, string title, string subtitle)
{
    protected SettingsContext Context { get; } = context;

    /// <summary>The name in the sidebar (same as the page title).</summary>
    public string Title { get; } = title;

    public FlowLayoutPanel Panel { get; } = Kit.Page(title, subtitle);

    /// <summary>Shows these values: the saved ones after all pages are built, and the defaults on Reset.</summary>
    public abstract void Load(Settings settings);

    /// <summary>Writes this page's choices into <paramref name="settings"/>.</summary>
    public abstract void Apply(Settings settings);

    /// <summary>The page is about to be shown (e.g. to refresh what depends on other pages).</summary>
    public virtual void OnShowing() { }

    protected void Changed() => Context.Changed();

    /// <summary>Reports a change whenever one of these controls changes.</summary>
    protected void TrackChanges(params Control[] controls)
    {
        foreach (var control in controls)
        {
            switch (control)
            {
                case CheckBox box: box.CheckedChanged += (_, _) => Changed(); break;
                case RadioButton radio: radio.CheckedChanged += (_, _) => Changed(); break;
                case ComboBox combo: combo.SelectedIndexChanged += (_, _) => Changed(); break;
                case NumericUpDown number: number.ValueChanged += (_, _) => Changed(); break;
                default: control.TextChanged += (_, _) => Changed(); break;
            }
        }
    }
}
