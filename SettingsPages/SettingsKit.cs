using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>A choice in a drop-down: the stored value and the text shown.</summary>
internal sealed record Option<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Building blocks shared by the Settings pages: page layout, rows and themed controls.</summary>
internal static class Kit
{
    /// <summary>Width of the cards on every page.</summary>
    public const int CardWidth = 556;

    /// <summary>A page: title, subtitle, then its cards from top to bottom.</summary>
    public static FlowLayoutPanel Page(string title, string subtitle)
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

    public static TableLayoutPanel Row(params Control[] controls)
    {
        // A one-row table, so labels are vertically centered next to taller controls (e.g. the drop-downs).
        var row = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Surface, Margin = Padding.Empty,
            RowCount = 1, ColumnCount = controls.Length
        };
        for (var i = 0; i < controls.Length; i++)
        {
            var c = controls[i];
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            c.Anchor = AnchorStyles.Left;
            if (c is Label label)
                label.Margin = new Padding(4, 0, 6, 0);
            row.Controls.Add(c, i, 0);
        }
        return row;
    }

    public static CheckBox Check(string text) => new ThemedCheckBox()
    {
        Text = text, AutoSize = true, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Surface,
        Margin = new Padding(0, 3, 0, 3), Cursor = Cursors.Hand
    };

    public static RadioButton Radio(string text) => new ThemedRadioButton()
    {
        Text = text, AutoSize = true, Font = Theme.Body, ForeColor = Theme.Text, BackColor = Theme.Surface,
        Margin = new Padding(0, 3, 0, 3), Cursor = Cursors.Hand
    };

    /// <summary>A small clickable text in the accent color.</summary>
    public static Label LinkLabel(string text, Font? font = null)
    {
        var label = Theme.Label(text, font ?? Theme.Body, Theme.Accent);
        label.Cursor = Cursors.Hand;
        label.Margin = new Padding(0, 6, 12, 4);
        return label;
    }

    public static ComboBox Combo<T>(Option<T>[] options, int width = 180)
    {
        var combo = new RoomyComboBox { Width = width, Margin = new Padding(0, 3, 10, 3) };
        combo.Items.AddRange(options.Cast<object>().ToArray());
        combo.SelectedIndex = 0;
        return combo;
    }

    public static ComboBox HourCombo(int from, int to)
    {
        var combo = new RoomyComboBox { Width = 90, Margin = new Padding(0, 3, 6, 3) };
        for (var hour = from; hour <= to; hour++)
            combo.Items.Add($"{hour:00}:00");
        combo.SelectedIndex = 0;
        return combo;
    }

    /// <summary>Selects the option with this value; the first option if there's none (e.g. an edited settings file).</summary>
    public static void Select<T>(ComboBox combo, T value) where T : notnull
    {
        var index = combo.Items.Cast<Option<T>>().ToList().FindIndex(o => o.Value.Equals(value));
        combo.SelectedIndex = Math.Max(0, index);
    }

    /// <summary>Selects the option with this value, or the fallback (e.g. a value from an edited settings file).</summary>
    public static void Select<T>(ComboBox combo, T value, T fallback) where T : notnull
    {
        var values = combo.Items.Cast<Option<T>>().Select(o => o.Value).ToList();
        combo.SelectedIndex = Math.Max(0, values.Contains(value) ? values.IndexOf(value) : values.IndexOf(fallback));
    }

    public static T Selected<T>(ComboBox combo) => ((Option<T>)combo.SelectedItem!).Value;
}
