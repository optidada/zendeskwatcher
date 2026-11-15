using System.Text.RegularExpressions;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// Word-wrapped text with bold and alert parts (<see cref="Markdown"/>), for the notification body. TextRenderer can't mix fonts
/// in one call, so this lays out the words itself; the result is cached, as popups repaint often.
/// </summary>
internal sealed partial class RichText(Font regular, Font bold)
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    /// <summary>A link in the text: a ticket number ("#123456") or a Settings page ("Settings → Groups").</summary>
    public sealed record Link(string Action, long? TicketId);

    /// <summary>Style: 0 regular, 1 bold, 2 alert (bold in the alert color); <see cref="Link"/> when it's part of a link.</summary>
    private readonly record struct Piece(string Text, int Style, int X, int Line, int Paragraph = 0, Link? Link = null);

    private string? _cachedText;
    private int _cachedWidth, _cachedMaxLines;
    private List<Piece> _pieces = [];
    private int _lines;

    public int LineHeight { get; } = Math.Max(TextRenderer.MeasureText("Ag", regular).Height, TextRenderer.MeasureText("Ag", bold).Height);

    /// <summary>The height the text needs at this width (at most <paramref name="maxLines"/> lines).</summary>
    public int Measure(string markdown, int width, int maxLines)
    {
        Layout(markdown, width, maxLines);
        return _lines * LineHeight;
    }

    /// <param name="linkColor">Links (ticket numbers, Settings pages) where they can be clicked; null: like the text around them.</param>
    public void Draw(Graphics g, string markdown, Rectangle rect, int maxLines, Color color, Color boldColor, Color? alertColor = null, Color? linkColor = null)
    {
        Layout(markdown, rect.Width, maxLines);
        foreach (var piece in _pieces)
        {
            var at = new Point(rect.Left + piece.X, rect.Top + piece.Line * LineHeight);
            var pieceColor = piece.Link is not null && linkColor is { } link ? link : piece.Style switch { 2 => alertColor ?? boldColor, 1 => boldColor, _ => color };
            TextRenderer.DrawText(g, piece.Text, piece.Style is 1 or 2 ? bold : regular, at, pieceColor, Flags);
        }
    }

    /// <summary>Where the text ends (for the terminal's cursor).</summary>
    public Point End(string markdown, Rectangle rect, int maxLines)
    {
        Layout(markdown, rect.Width, maxLines);
        if (_pieces.Count == 0)
            return rect.Location;
        var last = _pieces[^1];
        return new Point(rect.Left + last.X + Width(last.Text, last.Style), rect.Top + last.Line * LineHeight);
    }

    /// <summary>Where each link is (one wrapped over two lines has two rectangles), so a click on it can follow it.</summary>
    public List<(Link Link, Rectangle Bounds)> Links(string markdown, Rectangle rect, int maxLines)
    {
        Layout(markdown, rect.Width, maxLines);
        return _pieces.Where(p => p.Link is not null)
            .Select(p => (p.Link!, new Rectangle(rect.Left + p.X, rect.Top + p.Line * LineHeight, Width(p.Text.TrimEnd(), p.Style), LineHeight)))
            .ToList();
    }

    /// <summary>The text of a run cut into parts, each with its link or null: "#123456" and "Settings → Groups → Clients".</summary>
    internal static IEnumerable<(string Text, Link? Link)> SplitLinks(string text)
    {
        var at = 0;
        foreach (Match m in LinkPattern().Matches(text))
        {
            Link? link = null;
            if (m.Groups["ticket"].Success && long.TryParse(m.Groups["ticket"].Value, out var id))
                link = new Link(NotificationActions.OpenTicket, id);
            else if (m.Groups["path"].Success && SettingsLinks.Resolve(m.Groups["path"].Value) is var (page, section))
                link = new Link(NotificationActions.SettingsPage(page, section), null);
            if (link is null)
                continue;
            if (m.Index > at)
                yield return (text[at..m.Index], null);
            yield return (m.Value, link);
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            yield return (text[at..], null);
    }

    private static Regex? _linkPattern;

    /// <summary>"#123456" (not inside a word), or "Settings → " and sidebar pages joined by arrows.</summary>
    private static Regex LinkPattern()
    {
        if (_linkPattern is not null)
            return _linkPattern;
        var page = "(?:" + string.Join("|", SettingsLinks.Pages.OrderByDescending(p => p.Length).Select(Regex.Escape)) + ")";
        return _linkPattern = new Regex(
            @"(?<![\w#])#(?<ticket>\d{3,})\b|\bSettings\s*→\s*(?<path>" + page + @"(?:\s*→\s*" + page + @")*)(?![\w&])",
            RegexOptions.Compiled);
    }

    private int Width(string text, int style) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, style is 1 or 2 ? bold : regular, Size.Empty, Flags).Width;

    private void Layout(string markdown, int width, int maxLines)
    {
        if (markdown == _cachedText && width == _cachedWidth && maxLines == _cachedMaxLines)
            return;
        _cachedText = markdown;
        _cachedWidth = width;
        _cachedMaxLines = maxLines;

        // Words (with the spaces after them) and line breaks, each knowing its style.
        var tokens = new List<(string Text, int Style, Link? Link)>();
        foreach (var run in Markdown.Parse(markdown))
        foreach (var (part, link) in SplitLinks(run.Text))
        {
            var style = run.Alert ? 2 : run.Bold ? 1 : 0;
            var start = 0;
            for (var i = 0; i <= part.Length; i++)
            {
                var end = i == part.Length;
                if (!end && part[i] != ' ' && part[i] != '\n')
                    continue;
                if (!end && part[i] == ' ')
                {
                    tokens.Add((part[start..(i + 1)], style, link));
                    start = i + 1;
                    continue;
                }
                if (i > start)
                    tokens.Add((part[start..i], style, link));
                if (!end)
                    tokens.Add(("\n", style, null));
                start = i + 1;
            }
        }

        var pieces = new List<Piece>();
        var line = 0;
        var x = 0;
        var paragraph = 0;
        var truncated = false;
        void NewLine()
        {
            line++;
            x = 0;
        }

        foreach (var (raw, tokenStyle, link) in tokens)
        {
            if (raw == "\n")
            {
                paragraph++;
                NewLine();
                if (line >= maxLines) { truncated = true; break; }
                continue;
            }
            var text = x == 0 ? raw.TrimStart() : raw;
            if (text.Length == 0)
                continue;
            var w = Width(text.TrimEnd(), tokenStyle);
            if (x > 0 && x + w > width)
            {
                NewLine();
                if (line >= maxLines) { truncated = true; break; }
                text = text.TrimStart();
            }
            // A single word wider than the line (a long link): cut it by characters.
            while (Width(text.TrimEnd(), tokenStyle) > width - x && text.Length > 1)
            {
                var fit = text.Length - 1;
                while (fit > 1 && Width(text[..fit], tokenStyle) > width - x)
                    fit--;
                pieces.Add(new Piece(text[..fit], tokenStyle, x, line, paragraph, link));
                text = text[fit..];
                NewLine();
                if (line >= maxLines) { truncated = true; break; }
            }
            if (truncated)
                break;
            pieces.Add(new Piece(text, tokenStyle, x, line, paragraph, link));
            x += Width(text, tokenStyle);
        }

        // Neighbors on the same line in the same style become one piece (fewer draw calls, natural spacing).
        var merged = new List<Piece>();
        foreach (var piece in pieces)
        {
            if (merged.Count > 0 && merged[^1].Line == piece.Line && merged[^1].Style == piece.Style && merged[^1].Paragraph == piece.Paragraph && merged[^1].Link == piece.Link)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + piece.Text };
            else
                merged.Add(piece);
        }

        if (truncated && merged.Count > 0)
        {
            // "…" at the end of the last line, shortening it until it fits.
            var last = merged[^1];
            var text = last.Text.TrimEnd();
            while (text.Length > 0 && last.X + Width(text + "…", last.Style) > width)
                text = text[..^1].TrimEnd();
            merged[^1] = last with { Text = text + "…" };
        }

        _pieces = merged;
        _lines = merged.Count == 0 ? 0 : merged.Max(p => p.Line) + 1;
    }
}
