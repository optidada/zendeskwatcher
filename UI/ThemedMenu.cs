using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>The tray menu in the app theme's colors (Windows draws it light whatever the theme).</summary>
internal static class ThemedMenu
{
    /// <summary>Colors the menu and its items; called each time it opens, so a theme change shows right away.</summary>
    public static void Apply(ToolStrip menu, UiPalette palette)
    {
        menu.Renderer = new Renderer(palette);
        menu.BackColor = palette.Surface;
        menu.ForeColor = palette.Text;
        // More room than Windows' compact default: easier to read and to hit.
        var s = menu.DeviceDpi / 96f;
        foreach (ToolStripItem item in menu.Items)
        {
            item.Margin = Padding.Empty; // text centered in each row: the same space above the first line as below the last
            item.BackColor = palette.Surface;
            item.ForeColor = palette.Text;
            if (item is ToolStripMenuItem)
                item.Padding = new Padding((int)(4 * s), (int)(5 * s), (int)(12 * s), (int)(5 * s));
            else if (item is ToolStripSeparator)
            {
                item.AutoSize = false; // a little taller than Windows' 6 px, to match the roomier lines
                item.Height = (int)(11 * s);
            }
        }
    }

    /// <summary>The tray menu as PNGs, one per app theme (developer helper: --render-menu).</summary>
    public static void RenderSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var theme in Enum.GetValues<NotificationTheme>())
        {
            using var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("Watching · 3 unassigned") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Zendesk");
            menu.Items.Add("Check now");
            menu.Items.Add(new ToolStripMenuItem("Pause notifications") { Checked = true });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Settings…");
            menu.Items.Add("Exit");
            Apply(menu, UiPalette.For(theme));
            menu.Show(-3000, -3000); // off-screen: lays out the items
            menu.Items[3].Select(); // "Check now" as if hovered: shows the selection
            using var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
            menu.Close();
            bitmap.Save(Path.Combine(dir, $"{theme}.png"));
        }
    }

    private sealed class Renderer(UiPalette p) : ToolStripProfessionalRenderer(new Colors(p))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? p.Text : p.TextMuted; // the status line is disabled: muted, readable
            // The drop-down layout puts the text near the top when items have extra padding: center it in the row,
            // so it sits in the middle of the hover highlight.
            var r = e.TextRectangle;
            e.TextRectangle = new Rectangle(r.X, 0, r.Width, e.Item.Height);
            e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom)) | TextFormatFlags.VerticalCenter;
            base.OnRenderItemText(e);
        }

        /// <summary>The separator: one line in the middle of its row, with the same space on both sides.</summary>
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var item = e.Item;
            var y = item.Height / 2;
            var inset = (int)(10 * (item.Owner?.DeviceDpi ?? 96) / 96f); // the same on both sides: centered
            using var pen = new Pen(p.Border);
            e.Graphics.DrawLine(pen, inset, y, item.Width - inset, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = p.TextMuted;
            base.OnRenderArrow(e);
        }

        /// <summary>A tick in the accent color ("Pause notifications"), instead of Windows' blue box.</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            r.Y = (e.Item.Height - r.Height) / 2; // centered in the row, like the text
            var s = r.Height / 16f;
            using var pen = new Pen(p.Accent, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [
                new PointF(r.X + 3 * s, r.Y + 8.5f * s), new PointF(r.X + 6.5f * s, r.Y + 12 * s), new PointF(r.X + 13 * s, r.Y + 4.5f * s)
            ]);
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        private readonly UiPalette _p;

        public Colors(UiPalette p)
        {
            _p = p;
            UseSystemColors = false;
        }

        public override Color ToolStripDropDownBackground => _p.Surface;
        public override Color ImageMarginGradientBegin => _p.Surface;
        public override Color ImageMarginGradientMiddle => _p.Surface;
        public override Color ImageMarginGradientEnd => _p.Surface;
        public override Color MenuBorder => _p.Border;
        public override Color MenuItemBorder => _p.AccentSoft;
        public override Color MenuItemSelected => _p.AccentSoft;
        public override Color MenuItemSelectedGradientBegin => _p.AccentSoft;
        public override Color MenuItemSelectedGradientEnd => _p.AccentSoft;
        public override Color MenuItemPressedGradientBegin => _p.AccentSoft;
        public override Color MenuItemPressedGradientMiddle => _p.AccentSoft;
        public override Color MenuItemPressedGradientEnd => _p.AccentSoft;
        public override Color SeparatorDark => _p.Border;
        public override Color SeparatorLight => _p.Surface;
        public override Color CheckBackground => _p.Surface;
        public override Color CheckSelectedBackground => _p.AccentSoft;
        public override Color CheckPressedBackground => _p.AccentSoft;
        public override Color ButtonSelectedBorder => _p.AccentSoft;
    }
}
