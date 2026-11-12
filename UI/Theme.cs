using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>Colors, fonts and small owner-drawn controls for a clean, modern look.</summary>
public static class Theme
{
    public static readonly Color Window = Color.FromArgb(243, 244, 246);      // page background
    public static readonly Color Surface = Color.White;                        // cards
    public static readonly Color Border = Color.FromArgb(229, 231, 235);
    public static readonly Color Text = Color.FromArgb(17, 24, 39);
    public static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentSoft = Color.FromArgb(239, 246, 255);
    public static readonly Color Success = Color.FromArgb(22, 163, 74);
    public static readonly Color Warning = Color.FromArgb(217, 119, 6);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color Neutral = Color.FromArgb(156, 163, 175);
    public static readonly Color Sidebar = Color.FromArgb(17, 24, 39);
    public static readonly Color SidebarHover = Color.FromArgb(31, 41, 55);
    public static readonly Color SidebarText = Color.FromArgb(209, 213, 219);

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
        Margin = new Padding(0, 0, 0, 4)
    };

    public static Button PrimaryButton(string text) => StyledButton(text, Accent, Color.White, Accent, AccentHover);

    public static Button SecondaryButton(string text) => StyledButton(text, Surface, Text, Border, Window);

    private static Button StyledButton(string text, Color back, Color fore, Color border, Color hover)
    {
        var button = new Button
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

        TextRenderer.DrawText(g, Text, _selected ? Theme.BodyBold : Font, new Rectangle(24, 0, Width - 24, Height),
            _selected ? Color.White : Theme.SidebarText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
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

    private void LayoutBox()
    {
        if (Box is null)
            return;
        var left = PadX + (_searchIcon ? IconSpace : 0);
        Box.SetBounds(left, (Height - Box.PreferredHeight) / 2, Math.Max(10, Width - left - PadX), Box.PreferredHeight);
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
            Columns[0].Width = Math.Max(50, ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
    }

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
        Size = new Size(124, 104);
        Margin = new Padding(0, 0, 10, 0);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

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
        var mini = new RectangleF(10 * s, 10 * s, Width - 20 * s, 54 * s);
        if (Value == NotificationTheme.WindowsNative)
            PaintNative(g, mini, s);
        else
            PaintCustom(g, mini, s, ZendeskWatcher.UI.Popups.PopupPalette.For(Value));

        TextRenderer.DrawText(g, Text, _selected ? Theme.BodyBold : Theme.Body, new Rectangle(0, (int)(70 * s), Width, (int)(26 * s)),
            Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
