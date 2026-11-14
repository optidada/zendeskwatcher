using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>Colors, fonts and small owner-drawn controls for a clean, modern look.</summary>
public static class Theme
{
    /// <summary>The colors in use; set by <see cref="Use"/> from the chosen appearance.</summary>
    public static UiPalette Current { get; private set; } = UiPalette.For(NotificationTheme.CleanLight);

    public static Color Window => Current.Window;          // page background
    public static Color Surface => Current.Surface;        // cards
    public static Color Border => Current.Border;
    public static Color Text => Current.Text;
    public static Color TextMuted => Current.TextMuted;
    public static Color Accent => Current.Accent;
    public static Color AccentHover => Current.AccentHover;
    public static Color AccentSoft => Current.AccentSoft;
    public static Color OnAccent => Current.OnAccent;      // text on Accent
    public static Color SidebarTitle => Current.SidebarTitle; // app title and the selected navigation entry
    public static Color Success => Current.Success;
    public static Color Warning => Current.Warning;
    public static Color Danger => Current.Danger;
    public static Color Neutral => Current.Neutral;
    public static Color Sidebar => Current.Sidebar;
    public static Color SidebarHover => Current.SidebarHover;
    public static Color SidebarText => Current.SidebarText;

    /// <summary>Switches the palette. Returns the previous one (for <see cref="Recolor"/>).</summary>
    public static UiPalette Use(NotificationTheme theme)
    {
        var previous = Current;
        Current = UiPalette.For(theme);
        return previous;
    }

    /// <summary>
    /// Recolors already created controls from one palette to another: every color that is a palette role in
    /// <paramref name="from"/> becomes the same role in <paramref name="to"/>. Owner-drawn controls read the palette
    /// when painting, so they just repaint.
    /// </summary>
    public static void Recolor(Control root, UiPalette from, UiPalette to)
    {
        var map = new Dictionary<int, Color>();
        var fromRoles = from.Roles;
        var toRoles = to.Roles;
        for (var i = 0; i < fromRoles.Length; i++)
            map.TryAdd(fromRoles[i].ToArgb(), toRoles[i]);
        Color Map(Color c) => c.A == 0 ? c : map.GetValueOrDefault(c.ToArgb(), c);

        void Walk(Control c)
        {
            switch (c)
            {
                case TextBoxBase or NumericUpDown or ComboBox or ListView:
                    c.BackColor = to.Surface;
                    c.ForeColor = to.Text;
                    break;
                default:
                    c.BackColor = Map(c.BackColor);
                    c.ForeColor = Map(c.ForeColor);
                    break;
            }

            if (c is Button button)
            {
                button.FlatAppearance.BorderColor = Map(button.FlatAppearance.BorderColor);
                button.FlatAppearance.MouseOverBackColor = Map(button.FlatAppearance.MouseOverBackColor);
                button.FlatAppearance.MouseDownBackColor = Map(button.FlatAppearance.MouseDownBackColor);
            }
            else if (c is ListView list)
            {
                foreach (ListViewItem item in list.Items)
                {
                    item.BackColor = Map(item.BackColor);
                    item.ForeColor = Map(item.ForeColor);
                }
            }
            else if (c is StatusDot dot)
            {
                dot.DotColor = Map(dot.DotColor);
            }

            foreach (Control child in c.Controls)
                Walk(child);
        }

        root.SuspendLayout();
        Walk(root);
        root.ResumeLayout();
        if (root is Form form)
            ApplyWindowChrome(form, to.Dark);
        root.Invalidate(invalidateChildren: true);
    }

    /// <summary>Dark title bar and dark scrollbars for dark palettes (Windows 10 2004+ / 11).</summary>
    public static void ApplyWindowChrome(Control root, bool dark)
    {
        if (root is Form form && form.IsHandleCreated)
        {
            var value = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
        }

        void Walk(Control c)
        {
            if (c.IsHandleCreated && (c is ScrollableControl { AutoScroll: true } or ListView or ComboBox))
                _ = SetWindowTheme(c.Handle, c switch
                {
                    ComboBox => dark ? "DarkMode_CFD" : "CFD",
                    ListView => dark ? "DarkMode_ItemsView" : "ItemsView", // light group headings on dark
                    _ => dark ? "DarkMode_Explorer" : "Explorer"
                }, null);
            foreach (Control child in c.Controls)
                Walk(child);
        }
        Walk(root);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// The mouse wheel over this control scrolls the page instead of changing the value, so scrolling through
    /// Settings can't change a setting by accident. <paramref name="allowWhen"/> lets the control keep the wheel
    /// (e.g. an open drop-down list).
    /// </summary>
    public static void WheelScrollsPage(Control control, Func<bool>? allowWhen = null)
    {
        control.MouseWheel += (_, e) =>
        {
            if (allowWhen?.Invoke() == true || e is not HandledMouseEventArgs handled)
                return;
            handled.Handled = true;

            // Hand the wheel to the nearest scrolling container (the page).
            for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is ScrollableControl { AutoScroll: true } page && page.VerticalScroll.Visible)
                {
                    var step = SystemInformation.MouseWheelScrollLines * Body.Height;
                    var y = Math.Clamp(-page.AutoScrollPosition.Y - e.Delta / 120 * step, 0, Math.Max(0, page.DisplayRectangle.Height - page.ClientSize.Height));
                    page.AutoScrollPosition = new Point(-page.AutoScrollPosition.X, y);
                    page.Invalidate(true); // repaint fully: no smears behind see-through labels
                    break;
                }
            }
        };
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    public const string FontFamily = "Segoe UI";
    public static readonly Font Body = new(FontFamily, 9.75f);
    public static readonly Font BodyBold = new(FontFamily, 9.75f, FontStyle.Bold);
    public static readonly Font Small = new(FontFamily, 8.75f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 11f);
    public static readonly Font PageTitle = new("Segoe UI Semibold", 17f);
    public static readonly Font AppTitle = new("Segoe UI Semibold", 13f);

    public static Label Label(string text, Font? font = null, Color? color = null, int maxWidth = 0) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        BackColor = Color.Transparent,
        MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 0, 0, 4),
        UseMnemonic = false // show "&" as is (names, group names) instead of as a shortcut underline
    };

    public static Button PrimaryButton(string text) => StyledButton(text, Accent, OnAccent, Accent, AccentHover);

    /// <summary>Shows a spinner on a themed button while something loads (<see cref="RoundButton.SetBusy"/>); other buttons ignore it.</summary>
    public static void SetBusy(this Button button, bool busy, string? text = null)
    {
        if (button is RoundButton round)
            round.SetBusy(busy, text);
    }

    public static Button SecondaryButton(string text) => StyledButton(text, Surface, Text, Border, Window);

    private static Button StyledButton(string text, Color back, Color fore, Color border, Color hover)
    {
        var button = new RoundButton
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(96, 34),
            Padding = new Padding(12, 2, 12, 2),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = fore,
            Font = BodyBold,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0, 0, 8, 0)
        };
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = hover;
        return button;
    }

    public static TextBox TextBox(int width, string placeholder = "") => new()
    {
        Width = width,
        Font = Body,
        BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = placeholder,
        Margin = new Padding(0, 2, 0, 2)
    };

    /// <summary>The current theme's button corner radius for a button of this height (pixels at this scale).</summary>
    public static float ButtonRadius(float height, float scale) =>
        Current.ButtonRadius == UiPalette.Pill ? height / 2 : Math.Min(Current.ButtonRadius * scale, height / 2);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A white rounded card with a title and an optional description.</summary>
public sealed class Card : Panel
{
    public FlowLayoutPanel Body { get; }

    public Card(string title, string? description = null, int width = 540)
    {
        DoubleBuffered = true;
        BackColor = Theme.Window; // corners show the page color
        Padding = new Padding(20, 16, 20, 18);
        Margin = new Padding(0, 0, 0, 14);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);

        Body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface,
            Dock = DockStyle.Fill
        };
        Body.Controls.Add(Theme.Label(title, Theme.CardTitle));
        if (description is not null)
        {
            var desc = Theme.Label(description, Theme.Small, Theme.TextMuted, width - 44);
            desc.Margin = new Padding(0, 0, 0, 12);
            Body.Controls.Add(desc);
        }
        Controls.Add(Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10);
        using var fill = new SolidBrush(Theme.Surface);
        using var pen = new Pen(Theme.Border);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }
}

/// <summary>An on/off switch (a CheckBox drawn as a toggle).</summary>
public sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch(string text)
    {
        Text = text;
        AutoSize = false;
        Font = Theme.Body;
        ForeColor = Theme.Text;
        BackColor = Theme.Surface;
        Cursor = Cursors.Hand;
        Height = 30;
        Width = 480;
        Margin = new Padding(0, 2, 0, 2);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var trackHeight = 20f;
        var track = new RectangleF(0, (Height - trackHeight) / 2, 38, trackHeight);
        var on = Checked;
        var trackColor = !Enabled ? Theme.Border : on ? Theme.Accent : Theme.Neutral;

        using (var path = Theme.RoundedRect(track, trackHeight / 2))
        using (var brush = new SolidBrush(trackColor))
            g.FillPath(brush, path);

        var knob = trackHeight - 6;
        var knobX = on ? track.Right - knob - 3 : track.X + 3;
        g.FillEllipse(Brushes.White, knobX, track.Y + 3, knob, knob);

        TextRenderer.DrawText(g, Text, Font, new Rectangle(50, 0, Width - 50, Height),
            Enabled ? ForeColor : Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}

/// <summary>A small colored status indicator.</summary>
public sealed class StatusDot : Control
{
    private Color _color = Theme.Neutral;

    public StatusDot()
    {
        Size = new Size(12, 12);
        Margin = new Padding(0, 5, 8, 0);
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_color);
        e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>An entry in the left navigation.</summary>
public sealed class NavItem : Control
{
    private bool _selected;
    private bool _hover;

    public NavItem(string text)
    {
        Text = text;
        Font = Theme.Body;
        Size = new Size(200, 42);
        Margin = Padding.Empty;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    /// <summary>A sub-page, shown indented (and a little lower) under the page before it.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Sub
    {
        get => _sub;
        set { _sub = value; Height = value ? 34 : 42; Invalidate(); }
    }

    private bool _sub;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_selected || _hover ? Theme.SidebarHover : Theme.Sidebar);
        if (_selected)
        {
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, 0, 8, 4, Height - 16);
        }

        var indent = _sub ? 40 : 24;
        TextRenderer.DrawText(g, Text, _selected ? Theme.BodyBold : Font, new Rectangle(indent, 0, Width - indent, Height),
            _selected ? Theme.SidebarTitle : Theme.SidebarText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}

/// <summary>
/// A text input with inner padding, a rounded border that turns blue on focus, and an optional search icon.
/// <see cref="Text"/> and <see cref="Control.TextChanged"/> forward to the inner TextBox.
/// </summary>
public sealed class InputBox : Panel
{
    private const int PadX = 10;
    private const int PadY = 7;
    private const int IconSpace = 22;

    private readonly bool _searchIcon;
    private bool _focused;

    public TextBox Box { get; }

    public InputBox(int width, string placeholder = "", bool searchIcon = false)
    {
        _searchIcon = searchIcon;
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Margin = new Padding(0, 2, 0, 2);
        Cursor = Cursors.IBeam;

        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.Body,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface,
            PlaceholderText = placeholder
        };
        Controls.Add(Box);

        Size = new Size(width, Box.PreferredHeight + PadY * 2);
        LayoutBox();

        Box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        Box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        Box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Click += (_, _) => Box.Focus();
    }

    [AllowNull]
    public override string Text
    {
        get => Box.Text;
        set => Box.Text = value;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutBox();
        Invalidate();
    }

    // Also after layout: when Windows scales the window (e.g. 125%), it stretches the inner box after OnResize,
    // which made it cover the right border.
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        LayoutBox();
    }

    private void LayoutBox()
    {
        if (Box is null)
            return;
        var s = DeviceDpi / 96f;
        var left = (int)((PadX + (_searchIcon ? IconSpace : 0)) * s);
        var right = (int)(PadX * s);
        var bounds = new Rectangle(left, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - left - right), Box.PreferredHeight);
        if (Box.Bounds != bounds)
            Box.Bounds = bounds;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 6))
        using (var fill = new SolidBrush(Theme.Surface))
        using (var pen = new Pen(_focused ? Theme.Accent : Theme.Border, _focused ? 1.5f : 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        if (_searchIcon)
        {
            // A small magnifying glass.
            using var pen = new Pen(Theme.TextMuted, 1.6f);
            var cy = Height / 2f;
            g.DrawEllipse(pen, PadX, cy - 6, 10, 10);
            g.DrawLine(pen, PadX + 8.5f, cy + 2.5f, PadX + 13, cy + 7);
        }
    }
}

/// <summary>
/// A drop-down list with breathing room: taller rows, text inset from the border, and colors from the current
/// palette (also in the open list, so dark themes stay dark).
/// </summary>
public sealed class RoomyComboBox : ComboBox
{
    private const int PadX = 9;

    public RoomyComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        DrawMode = DrawMode.OwnerDrawFixed;
        // The closed box is drawn by OnPaint (theme colors, own arrow); Windows still draws the open list.
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = Theme.Body;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        UpdateItemHeight();
        Theme.WheelScrollsPage(this, allowWhen: () => DroppedDown); // an open list still scrolls with the wheel
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateItemHeight();
    }

    private void UpdateItemHeight() => ItemHeight = TextRenderer.MeasureText("Ag", Font).Height + (int)(12 * DeviceDpi / 96f);

    /// <summary>Items shown greyed that can't be chosen (picking one goes back to the previous choice).</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<int, bool>? IsItemDisabled { get; set; }

    private int _lastAllowed = -1;

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        if (SelectedIndex >= 0 && IsItemDisabled?.Invoke(SelectedIndex) == true && _lastAllowed >= 0 && _lastAllowed != SelectedIndex)
        {
            SelectedIndex = _lastAllowed; // raises the event again, for the allowed item
            return;
        }
        _lastAllowed = SelectedIndex;
        base.OnSelectedIndexChanged(e);
        Invalidate(); // the closed box shows the new choice
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;

        var inEdit = (e.State & DrawItemState.ComboBoxEdit) != 0;
        var highlighted = !inEdit && (e.State & DrawItemState.Selected) != 0;
        var disabled = !inEdit && IsItemDisabled?.Invoke(e.Index) == true;
        if (disabled)
            highlighted = false;
        var back = highlighted ? Theme.Accent : Theme.Surface;
        var fore = highlighted ? Theme.OnAccent : Enabled && !disabled ? Theme.Text : Theme.TextMuted;

        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, e.Bounds);

        var pad = (int)(PadX * DeviceDpi / 96f) - (inEdit ? 3 : 0); // the edit area already has a small inset
        var text = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - pad, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, text, fore,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// The closed box drawn in the theme's colors: Windows paints its arrow button in its own grey, lighter than dark
    /// themes. The open list stays owner-drawn (<see cref="OnDrawItem"/>).
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var s = DeviceDpi / 96f;
        var bounds = ClientRectangle;
        using (var back = new SolidBrush(Theme.Surface))
            g.FillRectangle(back, bounds);
        using (var border = new Pen(Focused || DroppedDown ? Theme.Accent : Theme.Border))
            g.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

        var arrowWidth = (int)(24 * s);
        if (SelectedIndex >= 0)
        {
            var pad = (int)(PadX * s);
            var text = new Rectangle(pad, 0, bounds.Width - pad - arrowWidth, bounds.Height);
            TextRenderer.DrawText(g, GetItemText(Items[SelectedIndex]), Font, text, Enabled ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var cx = bounds.Right - arrowWidth / 2f;
        var cy = bounds.Height / 2f;
        using var pen = new Pen(Enabled ? Theme.TextMuted : Theme.Border, 1.6f * s)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        g.DrawLines(pen, [new PointF(cx - 4 * s, cy - 2 * s), new PointF(cx, cy + 2 * s), new PointF(cx + 4 * s, cy - 2 * s)]);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

/// <summary>A ListView that paints without flicker and whose single column always fills the width.</summary>
public sealed class SmoothListView : ListView
{
    public SmoothListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    public void FitColumn()
    {
        if (Columns.Count == 1)
            Columns[0].Width = Math.Max(50, VisibleWidth is { } visible ? visible : ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
    }

    /// <summary>When the list is wider than what shows (its own scrollbar cut off): the width the column fits in.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int? VisibleWidth
    {
        get => _visibleWidth;
        set { _visibleWidth = value; FitColumn(); }
    }

    private int? _visibleWidth;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitColumn();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitColumn();
    }

    // ---- For a themed scrollbar (ThinScrollBar) instead of the Windows one, and themed tick boxes (set by the owner).

    /// <summary>The list scrolled (or its content changed so the scroll range did).</summary>
    public event Action? ScrollChanged;

    private (int, int, int, int)? _lastScroll;

    /// <summary>Windows applied a new visual style and reset the tick boxes: set the themed ones again.</summary>
    public event Action? ThemeChanged;

    /// <summary>Vertical scroll range, page and position (pixels in group view); null without a handle.</summary>
    public (int Min, int Max, int Page, int Pos)? VerticalScrollState()
    {
        if (!IsHandleCreated)
            return null;
        var info = new SCROLLINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<SCROLLINFO>(), fMask = 0x17 /* SIF_ALL */ };
        if (!GetScrollInfo(Handle, 1 /* SB_VERT */, ref info))
            return (0, 0, 0, 0); // no scrollbar: nothing to scroll
        return (info.nMin, info.nMax, (int)info.nPage, info.nPos);
    }

    public void ScrollToPosition(int position)
    {
        if (VerticalScrollState() is not { } s)
            return;
        var target = Math.Clamp(position, s.Min, Math.Max(s.Min, s.Max - s.Page + 1));
        if (target != s.Pos)
            SendMessage(Handle, 0x1014 /* LVM_SCROLL */, IntPtr.Zero, target - s.Pos);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // A new visual style (SetWindowTheme for dark/light lists, or Windows' own theme): Windows replaces the tick
        // boxes with its own then, faint on dark themes. The owner puts its themed ones back (after this message).
        if (m.Msg == 0x031A /* WM_THEMECHANGED */ && CheckBoxes && IsHandleCreated)
            BeginInvoke(() => ThemeChanged?.Invoke());

        // Every way of scrolling ends in a repaint, so that's where the scrollbar learns about it.
        if (m.Msg is 0x000F or 0x0115 /* WM_VSCROLL */ or 0x020A /* WM_MOUSEWHEEL */)
        {
            var state = VerticalScrollState();
            var key = state is { } s ? (s.Min, s.Max, s.Page, s.Pos) : ((int, int, int, int)?)null;
            if (key != _lastScroll)
            {
                _lastScroll = key;
                ScrollChanged?.Invoke();
            }
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius) => Theme.RoundedRect(r, radius);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SCROLLINFO
    {
        public uint cbSize, fMask;
        public int nMin, nMax;
        public uint nPage;
        public int nPos, nTrackPos;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref SCROLLINFO info);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, int lParam);
}

/// <summary>A selectable tile with a miniature preview of a notification theme.</summary>
public sealed class ThemeTile : Control
{
    private bool _selected;
    private bool _hover;

    public NotificationTheme Value { get; }

    public ThemeTile(NotificationTheme value, string label)
    {
        Value = value;
        Text = label;
        Font = Theme.Small;
        Size = new Size(112, 94);    // four per row: all themes on two rows
        Margin = new Padding(0, 0, 8, 8);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    /// <summary>A small muted second line under the name (e.g. "Fewer features"); null: none.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Note { get; set; }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = DeviceDpi / 96f;

        // Frame
        var frame = new RectangleF(1, 1, Width - 3, Height - 3);
        using (var path = Theme.RoundedRect(frame, 8 * s))
        using (var pen = new Pen(_selected ? Theme.Accent : _hover ? Theme.Neutral : Theme.Border, _selected ? 2f : 1f))
            g.DrawPath(pen, path);

        // Miniature popup
        var mini = new RectangleF(8 * s, 8 * s, Width - 16 * s, 50 * s);
        if (Value == NotificationTheme.WindowsNative)
            PaintNative(g, mini, s);
        else
            PaintCustom(g, mini, s, ZendeskWatcher.UI.Popups.PopupPalette.For(Value));

        if (Note is null)
            TextRenderer.DrawText(g, Text, _selected ? Theme.BodyBold : Theme.Body, new Rectangle(0, (int)(62 * s), Width, (int)(26 * s)),
                Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        else
        {
            TextRenderer.DrawText(g, Text, _selected ? Theme.BodyBold : Theme.Body, new Rectangle(0, (int)(59 * s), Width, (int)(20 * s)),
                Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Note, Theme.Small, new Rectangle(0, (int)(75 * s), Width, (int)(14 * s)),
                Theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Not in use (e.g. notifications follow the app theme): dimmed.
        if (!Enabled)
        {
            using var veil = new SolidBrush(Color.FromArgb(150, Theme.Surface));
            g.FillRectangle(veil, ClientRectangle);
        }
    }

    private static void PaintCustom(Graphics g, RectangleF r, float s, ZendeskWatcher.UI.Popups.PopupPalette p)
    {
        var accent = p.Accent("high", NotificationKind.NewTicket);
        using (var path = p.Terminal ? Rect(r) : Theme.RoundedRect(r, 5 * s))
        using (var bg = new SolidBrush(p.Background))
        using (var border = new Pen(p.Terminal ? p.Border : Color.FromArgb(60, p.Muted)))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        if (p.Stripe)
        {
            using var stripe = new SolidBrush(accent);
            g.FillRectangle(stripe, r.X, r.Y + (p.Terminal ? 0 : 3 * s), 3 * s, r.Height - (p.Terminal ? 0 : 6 * s));
        }

        var x = r.X + (p.Stripe ? 9 : 7) * s;
        using var headingBrush = new SolidBrush(p.Terminal ? p.Muted : accent);
        using var textBrush = new SolidBrush(p.Text);
        using var mutedBrush = new SolidBrush(Color.FromArgb(160, p.Muted));
        g.FillRectangle(headingBrush, x, r.Y + 7 * s, 40 * s, 3 * s);
        g.FillRectangle(textBrush, x, r.Y + 15 * s, r.Width - 26 * s, 5 * s);
        g.FillRectangle(mutedBrush, x, r.Y + 25 * s, r.Width - 34 * s, 3 * s);
        g.FillRectangle(mutedBrush, x, r.Y + 31 * s, r.Width - 46 * s, 3 * s);

        var button = new RectangleF(r.Right - 30 * s, r.Bottom - 15 * s, 22 * s, 9 * s);
        if (p.Terminal)
        {
            using var pen = new Pen(p.Text);
            g.DrawRectangle(pen, button.X, button.Y, button.Width, button.Height);
        }
        else
        {
            using var path = Theme.RoundedRect(button, 4.5f * s);
            using var fill = new SolidBrush(accent);
            g.FillPath(fill, path);
        }
    }

    private static void PaintNative(Graphics g, RectangleF r, float s)
    {
        using (var path = Theme.RoundedRect(r, 4 * s))
        using (var bg = new SolidBrush(Color.FromArgb(243, 243, 243)))
        using (var border = new Pen(Color.FromArgb(210, 210, 210)))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }
        using var logo = new SolidBrush(Theme.Accent);
        g.FillEllipse(logo, r.X + 7 * s, r.Y + 8 * s, 12 * s, 12 * s);
        using var dark = new SolidBrush(Color.FromArgb(60, 60, 60));
        using var light = new SolidBrush(Color.FromArgb(150, 150, 150));
        g.FillRectangle(dark, r.X + 25 * s, r.Y + 9 * s, r.Width - 40 * s, 4 * s);
        g.FillRectangle(light, r.X + 25 * s, r.Y + 18 * s, r.Width - 34 * s, 3 * s);
        g.FillRectangle(light, r.X + 25 * s, r.Y + 24 * s, r.Width - 48 * s, 3 * s);
        using var buttonBrush = new SolidBrush(Color.FromArgb(225, 225, 225));
        g.FillRectangle(buttonBrush, r.X + 7 * s, r.Bottom - 14 * s, r.Width - 14 * s, 9 * s);
    }

    private static GraphicsPath Rect(RectangleF r)
    {
        var path = new GraphicsPath();
        path.AddRectangle(r);
        return path;
    }
}
