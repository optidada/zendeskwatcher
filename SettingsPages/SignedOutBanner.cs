using System.Drawing.Drawing2D;
using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// The "Not signed in" banner at the top of pages whose lists come from Zendesk: a box tinted in the warning color, with a
/// bar at its left edge, a bold heading, what it means, and a Sign in button.
/// </summary>
internal sealed class SignedOutBanner : Panel
{
    public Button SignIn { get; } = Theme.PrimaryButton("Sign in");

    private static Color Fill => Mix(Theme.Window, Theme.Warning, 0.14f);

    public SignedOutBanner(string message, int width)
    {
        DoubleBuffered = true;
        BackColor = Theme.Window; // corners show the page color
        Padding = new Padding(18, 12, 14, 12);
        Margin = new Padding(0, 0, 0, 14);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(width, 0);
        MaximumSize = new Size(width, 0);

        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, BackColor = Color.Transparent, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var heading = Theme.Label("Not signed in", Theme.BodyBold, Theme.Text);
        heading.BackColor = Color.Transparent; // the tint painted below shows through, in every theme
        heading.Margin = new Padding(0, 0, 0, 2);
        var text = Theme.Label(message, Theme.Small, Theme.Text, width - 180);
        text.BackColor = Color.Transparent;
        text.Margin = Padding.Empty;
        SignIn.Anchor = AnchorStyles.Right;
        SignIn.Margin = new Padding(12, 0, 0, 0);
        grid.Controls.Add(heading, 0, 0);
        grid.Controls.Add(text, 0, 1);
        grid.Controls.Add(SignIn, 1, 0);
        grid.SetRowSpan(SignIn, 2);
        Controls.Add(grid);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundedRect(bounds, 10))
        using (var fill = new SolidBrush(Fill))
        using (var pen = new Pen(Mix(Theme.Window, Theme.Warning, 0.5f)))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            // The bar at the left edge, inside the rounded corners.
            var clip = g.Clip;
            g.SetClip(path);
            using var bar = new SolidBrush(Theme.Warning);
            g.FillRectangle(bar, 0, 0, 5 * DeviceDpi / 96f, Height);
            g.Clip = clip;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    private static Color Mix(Color a, Color b, float amount) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * amount), (int)Math.Round(a.G + (b.G - a.G) * amount), (int)Math.Round(a.B + (b.B - a.B) * amount));
}
