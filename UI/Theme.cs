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
                case TextBoxBase or NumericUpDown or ListView:
                    c.BackColor = to.Surface;
                    c.ForeColor = to.Text;
                    break;
                default:
                    c.BackColor = Map(c.BackColor);
                    c.ForeColor = Map(c.ForeColor);
                    break;
            }

            if (c is LinkLabel link)
            {
                link.LinkColor = to.Link; // not a role (often equals Accent), so set, not mapped
                link.ActiveLinkColor = to.LinkHover;
                link.VisitedLinkColor = to.Link;
            }
            else if (c is LinkText)
                c.ForeColor = to.Link;

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
            if (c.IsHandleCreated && (c is ScrollableControl { AutoScroll: true } or ListView))
                _ = SetWindowTheme(c.Handle, c switch
                {
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

    /// <summary>A label; a text that names another Settings page (see <see cref="SettingsLinks"/>) gets links to it.</summary>
    public static Label Label(string text, Font? font = null, Color? color = null, int maxWidth = 0) =>
        SettingsLinks.HasLinks(text) ? Linked(text, font, color, maxWidth) : PlainLabel(text, font, color, maxWidth);

    /// <summary>A label that links page references, also in text set later (hints and messages that change).</summary>
    public static Label Linked(string text, Font? font = null, Color? color = null, int maxWidth = 0) => new LinkedLabel
    {
        Text = text,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 0, 0, 4)
    };

    private static Label PlainLabel(string text, Font? font, Color? color, int maxWidth) => new()
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

    /// <summary>The card's title (links to a card scroll to it by this).</summary>
    public string Title { get; }

    public Card(string title, string? description = null, int width = 540)
    {
        Title = title;
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

    /// <summary>The page has unsaved changes: a small dot after its name.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Unsaved
    {
        get => _unsaved;
        set { if (_unsaved == value) return; _unsaved = value; Invalidate(); }
    }

    private bool _unsaved;

    /// <summary>Signed out, the page can only be looked at (its lists come from Zendesk): greyed, with a lock after its name.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Locked
    {
        get => _locked;
        set { if (_locked == value) return; _locked = value; Invalidate(); }
    }

    private bool _locked;

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
        var font = _selected ? Theme.BodyBold : Font;
        TextRenderer.DrawText(g, Text, font, new Rectangle(indent, 0, Width - indent, Height),
            _locked ? Theme.Neutral : _selected ? Theme.SidebarTitle : Theme.SidebarText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        var s = DeviceDpi / 96f;
        var textWidth = TextRenderer.MeasureText(g, Text, font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
        if (_locked)
        {
            var lockX = indent + textWidth + (int)(8 * s);
            TextRenderer.DrawText(g, "🔒", Font, new Rectangle(lockX, 0, Width - lockX, Height), Theme.Neutral,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            textWidth += TextRenderer.MeasureText(g, "🔒", Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + (int)(8 * s);
        }
        if (_unsaved)
        {
            // In the "Unsaved changes" color, just after the name.
            var r = 3.5f * s;
            var x = indent + textWidth + 8 * s;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var dot = new SolidBrush(Theme.Warning);
            g.FillEllipse(dot, x, Height / 2f - r, 2 * r, 2 * r);
        }
    }
}

/// <summary>
/// A text input with inner padding, a rounded border that turns blue on focus, and an optional search icon. A search
/// box also gets an ✕ that empties it while it has text (Esc does the same).
/// <see cref="Text"/> and <see cref="Control.TextChanged"/> forward to the inner TextBox.
/// </summary>
public sealed class InputBox : Panel
{
    private const int PadX = 10;
    private const int PadY = 7;
    private const int IconSpace = 22;
    private const int ClearSpace = 22;

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

        Box.TextChanged += (_, _) =>
        {
            OnTextChanged(EventArgs.Empty);
            if (_searchIcon)
                Invalidate(); // the ✕ comes and goes
        };

        Box.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        Box.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        Click += (_, _) => Box.Focus();
    }

    /// <summary>Esc empties a search box with text, before the window sees it (Esc closes Settings).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _searchIcon && Box.TextLength > 0)
        {
            Box.Text = "";
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Where the ✕ sits (search boxes with text only).</summary>
    private Rectangle ClearArea
    {
        get
        {
            var s = DeviceDpi / 96f;
            var size = (int)(ClearSpace * s);
            return _searchIcon && Box.TextLength > 0 ? new Rectangle(Width - size - (int)(4 * s), 0, size, Height) : Rectangle.Empty;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = ClearArea.Contains(e.Location) ? Cursors.Hand : Cursors.IBeam;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!ClearArea.Contains(e.Location))
            return;
        Box.Text = "";
        Box.Focus();
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
        var right = (int)((_searchIcon ? ClearSpace + 4 : PadX) * s);
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

        if (ClearArea is { IsEmpty: false } clear)
        {
            // ✕: empties the box.
            var s = DeviceDpi / 96f;
            using var pen = new Pen(Theme.TextMuted, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var cx = clear.X + clear.Width / 2f;
            var cy = Height / 2f;
            var r = 4.5f * s;
            g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
            g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
        }
    }
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

    private static GraphicsPath Rect(RectangleF r)
    {
        var path = new GraphicsPath();
        path.AddRectangle(r);
        return path;
    }
}
