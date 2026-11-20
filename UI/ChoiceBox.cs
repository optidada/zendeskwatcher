using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// A drop-down drawn by the app: a box with the current choice that opens a themed menu. Replaces the Windows
/// drop-down list, which took 70–95 ms each for Windows to create (measured), so pages with several opened slowly.
/// Keys like a drop-down: Up/Down change the choice, Space, Enter, F4 or Alt+Down open the list.
/// </summary>
public sealed class ChoiceBox : Control
{
    private const int PadX = 9;
    private int _selected = -1;
    private bool _hover, _open;

    /// <summary>The choices (an option's text is its ToString).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public List<object> Items { get; } = [];

    /// <summary>Items shown greyed that can't be chosen.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int, bool>? IsItemDisabled { get; set; }

    public event EventHandler? SelectedIndexChanged;

    public ChoiceBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        Font = Theme.Body;
        BackColor = Theme.Surface;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ComboBox;
        Height = BoxHeight();
        Theme.WheelScrollsPage(this); // the wheel scrolls the page, never the choice by accident
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var index = Items.Count == 0 ? -1 : Math.Clamp(value, -1, Items.Count - 1);
            if (index == _selected)
                return;
            _selected = index;
            AccessibleName = SelectedItem?.ToString();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem => _selected >= 0 && _selected < Items.Count ? Items[_selected] : null;

    /// <summary>The text's height plus room above and below, like the Windows drop-down it replaces.</summary>
    private int BoxHeight() => TextRenderer.MeasureText("Ag", Font).Height + (int)(18 * DeviceDpi / 96f); // the old row height plus its borders

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = BoxHeight();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = BoxHeight();
    }

    /// <summary>Pages built at 100% are scaled to the display: the width scales, the height follows the font (already right).</summary>
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified & ~BoundsSpecified.Height);
        Height = BoxHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = DeviceDpi / 96f;
        var bounds = ClientRectangle;
        using (var back = new SolidBrush(Theme.Surface))
            g.FillRectangle(back, bounds);
        var active = Enabled && (Focused || _open || _hover);
        using (var border = new Pen(active ? Theme.Accent : Theme.Border))
            g.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

        var arrowWidth = (int)(24 * s);
        if (SelectedItem is { } item)
        {
            var pad = (int)(PadX * s);
            TextRenderer.DrawText(g, item.ToString(), Font, new Rectangle(pad, 0, bounds.Width - pad - arrowWidth, bounds.Height),
                Enabled ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = bounds.Right - arrowWidth / 2f;
        var cy = bounds.Height / 2f;
        using var pen = new Pen(Enabled ? Theme.TextMuted : Theme.Border, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, [new PointF(cx - 4 * s, cy - 2 * s), new PointF(cx, cy + 2 * s), new PointF(cx + 4 * s, cy - 2 * s)]);
    }

    /// <summary>The list as a themed menu under the box, the current choice ticked.</summary>
    private void Open()
    {
        if (!Enabled || Items.Count == 0)
            return;
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        for (var i = 0; i < Items.Count; i++)
        {
            var index = i;
            var entry = new ToolStripMenuItem(Items[i].ToString()) { Checked = i == _selected, Enabled = IsItemDisabled?.Invoke(i) != true };
            entry.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(entry);
        }
        ThemedMenu.Apply(menu, Theme.Current);
        ThemedMenu.StretchTo(menu, Width);
        _open = true;
        menu.Closed += (_, _) =>
        {
            _open = false;
            Invalidate();
            menu.BeginInvoke(menu.Dispose); // after the click is handled
        };
        Invalidate();
        menu.Show(this, new Point(0, Height));
        if (_selected >= 0 && _selected < menu.Items.Count)
            menu.Items[_selected].Select(); // the keyboard starts at the current choice
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        Focus();
        Open();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
        {
            Open();
            e.Handled = e.SuppressKeyPress = true;
            return;
        }
        if (!Enabled || e.KeyCode is not (Keys.Down or Keys.Up or Keys.Home or Keys.End))
            return;
        // Like a drop-down: the arrows change the choice, skipping greyed ones.
        var allowed = Enumerable.Range(0, Items.Count).Where(i => IsItemDisabled?.Invoke(i) != true).ToList();
        int? next = e.KeyCode switch
        {
            Keys.Down => allowed.Where(i => i > _selected).Cast<int?>().FirstOrDefault(),
            Keys.Up => allowed.Where(i => i < _selected).Cast<int?>().LastOrDefault(),
            Keys.Home => allowed.Cast<int?>().FirstOrDefault(),
            _ => allowed.Cast<int?>().LastOrDefault()
        };
        if (next is { } index)
            SelectedIndex = index;
        e.Handled = true;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}
