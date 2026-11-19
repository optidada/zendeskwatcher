using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// A client's card on the Clients page, shown while pointing at the client's name: the name, its tickets in the last
/// 90 days, who usually handles them, and the products Zendesk lists as wrapped chips (the ones matching the filter highlighted), with a word on
/// how reliable that list is. Opens beside the name (right, or left when there's no room), never over it; never takes
/// the focus. Only what the list already loaded: no extra requests.
/// </summary>
internal sealed class ClientCard : Form
{
    private const int CardWidth = 300, Pad = 12, ChipPadX = 7, ChipGap = 5, MaxChips = 12; // at 96 dpi

    private string _name = "";
    private string? _tickets;
    private string? _usually;
    private List<string> _products = [];
    private HashSet<string> _matched = [];

    public ClientCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, CS_DROPSHADOW = 0x20000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    /// <summary>Shows the card for a client beside <paramref name="name"/> (the name's rectangle on screen).</summary>
    public void ShowFor(Form owner, Rectangle name, string client, int tickets, int lookBackDays, IReadOnlyList<string>? products, IEnumerable<string> matched,
        string? usually = null)
    {
        _name = client;
        _usually = usually;
        _tickets = tickets > 0 ? $"{tickets} ticket{(tickets == 1 ? "" : "s")} in the last {lookBackDays} days" : null;
        _products = [.. (products ?? []).Where(p => p.Trim().Length > 0).Select(p => p.Trim()).Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];
        _matched = matched.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var s = owner.DeviceDpi / 96f;
        var size = new Size((int)(CardWidth * s), Arrange(null, s));
        // Beside the name, top aligned with its row; on the left when the screen ends on the right.
        var screen = Screen.FromRectangle(name).WorkingArea;
        var gap = (int)(14 * s);
        var x = name.Right + gap + size.Width <= screen.Right ? name.Right + gap : name.Left - gap - size.Width;
        var y = Math.Clamp(name.Top - (int)(4 * s), screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height));
        Bounds = new Rectangle(Math.Max(screen.Left, x), y, size.Width, size.Height);
        if (Owner != owner)
            Owner = owner; // stays above the Settings window, and goes with it
        if (!Visible)
            Show(owner);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Surface);
        using (var border = new Pen(Theme.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        Arrange(g, DeviceDpi / 96f);
    }

    /// <summary>Lays the card out (and paints it when <paramref name="g"/> isn't null); returns its height.</summary>
    private int Arrange(Graphics? g, float s)
    {
        const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        const TextFormatFlags Wrapped = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak;
        int P(float v) => (int)Math.Round(v * s);
        var left = P(Pad);
        var width = P(CardWidth) - 2 * left;
        var y = P(Pad);

        var nameHeight = TextRenderer.MeasureText(_name, Theme.BodyBold, new Size(width, 0), Wrapped).Height;
        if (g is not null)
            TextRenderer.DrawText(g, _name, Theme.BodyBold, new Rectangle(left, y, width, nameHeight), Theme.Text, Wrapped);
        y += nameHeight;
        var small = TextRenderer.MeasureText("Ag", Theme.Small, Size.Empty, Line).Height;
        if (_tickets is not null)
        {
            y += P(2);
            if (g is not null)
                TextRenderer.DrawText(g, _tickets, Theme.Small, new Rectangle(left, y, width, small), Theme.TextMuted, Line);
            y += small;
        }
        if (_usually is not null)
        {
            y += P(2);
            var usuallyHeight = TextRenderer.MeasureText(_usually, Theme.Small, new Size(width, 0), Wrapped).Height;
            if (g is not null)
                TextRenderer.DrawText(g, _usually, Theme.Small, new Rectangle(left, y, width, usuallyHeight), Theme.Text, Wrapped);
            y += usuallyHeight;
        }

        y += P(10);
        if (_products.Count == 0)
        {
            if (g is not null)
                TextRenderer.DrawText(g, "No products listed in Zendesk.", Theme.Small, new Rectangle(left, y, width, small), Theme.TextMuted, Line);
            return y + small + P(Pad);
        }

        // The products as chips, wrapped; the matched ones first in the accent, then the rest; at most a dozen.
        var shown = _products.OrderBy(p => _matched.Contains(p) ? 0 : 1).Take(MaxChips).ToList();
        var more = _products.Count - shown.Count;
        var chipHeight = small + P(6);
        var x = left;
        var chips = shown.Select(p => (Text: p, Matched: _matched.Contains(p), IsMore: false)).ToList();
        if (more > 0)
            chips.Add(($"+{more} more", false, true));
        foreach (var label in chips)
        {
            var isMore = label.IsMore;
            var textWidth = Math.Min(width - 2 * P(ChipPadX), TextRenderer.MeasureText(label.Text, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 1);
            var chipWidth = textWidth + 2 * P(ChipPadX);
            if (x > left && x + chipWidth > left + width)
            {
                x = left;
                y += chipHeight + P(ChipGap);
            }
            if (g is not null)
            {
                var chip = new RectangleF(x, y, chipWidth, chipHeight);
                if (!isMore)
                {
                    using var path = Theme.RoundedRect(chip, Math.Min(Theme.ButtonRadius(chipHeight, s), chipHeight / 2f));
                    using var fill = new SolidBrush(label.Matched ? Theme.Accent : Theme.Window);
                    g.FillPath(fill, path);
                    if (!label.Matched)
                    {
                        using var outline = new Pen(Theme.Border);
                        g.DrawPath(outline, path);
                    }
                }
                TextRenderer.DrawText(g, label.Text, Theme.Small, new Rectangle(x + P(ChipPadX), y + (chipHeight - small) / 2, textWidth, small),
                    isMore ? Theme.TextMuted : label.Matched ? Theme.OnAccent : Theme.Text, Line);
            }
            x += chipWidth + P(ChipGap);
        }
        y += chipHeight + P(10);

        const string Note = "From Zendesk's client record: often out of date, and bundled products aren't listed.";
        var noteHeight = TextRenderer.MeasureText(Note, Theme.Small, new Size(width, 0), Wrapped).Height;
        if (g is not null)
            TextRenderer.DrawText(g, Note, Theme.Small, new Rectangle(left, y, width, noteHeight), Theme.TextMuted, Wrapped);
        return y + noteHeight + P(Pad);
    }
}
