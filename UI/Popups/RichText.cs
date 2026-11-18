using System.Text.RegularExpressions;

namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// Word-wrapped text with bold and alert parts and priority tags (<see cref="Markdown"/>), for the notification body. TextRenderer can't mix fonts
/// in one call, so this lays out the words itself; the result is cached, as popups repaint often.
/// </summary>
internal sealed partial class RichText(Font regular, Font bold)
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    /// <summary>A link in the text: a ticket number ("#123456") or a Settings page ("Settings → Groups").</summary>
    public sealed record Link(string Action, long? TicketId);

    /// <summary>Priority tags (<see cref="Markdown.Tag"/>) as colored chips; false: as plain text (the Terminal style).</summary>
    public bool Tags { get; init; } = true;

    /// <summary>The scale for the tags' padding (96 dpi = 1).</summary>
    public float TagScale { get; init; } = 1f;

    /// <summary>Each line of the text stays one row: what doesn't fit ends in "…" instead of wrapping (lists of tickets).</summary>
    public bool OneRowPerLine { get; init; }

    /// <summary>Style: 0 regular, 1 bold, 2 alert (bold in the alert color), 3 a priority tag; <see cref="Link"/> when it's part of a link.</summary>
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
    /// <param name="tagColor">A priority tag's color, by priority ("urgent" …); null: the Settings window's colors.</param>
    public void Draw(Graphics g, string markdown, Rectangle rect, int maxLines, Color color, Color boldColor, Color? alertColor = null, Color? linkColor = null,
        Func<string, Color>? tagColor = null)
    {
        Layout(markdown, rect.Width, maxLines);
        foreach (var piece in _pieces)
        {
            var at = new Point(rect.Left + piece.X, rect.Top + piece.Line * LineHeight);
            if (piece.Style == 3)
            {
                var priority = Markdown.PriorityOfTag(piece.Text);
                PriorityTag.Draw(g, piece.Text, tagColor?.Invoke(priority) ?? PriorityTag.ColorOf(priority), at.X + TagMargin, at.Y + LineHeight / 2, TagScale);
                continue;
            }
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
            else if (m.Groups["more"].Success)
                link = new Link(NotificationActions.Search(Tracking.TicketRules.WorkingOnSearch), null);
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

    /// <summary>
    /// "#123456" (not inside a word), "Settings → " and sidebar pages joined by arrows, or Catch up's "+N more" (the rest
    /// of your tickets to work on, opened in Zendesk).
    /// </summary>
    private static Regex LinkPattern()
    {
        if (_linkPattern is not null)
            return _linkPattern;
        var page = "(?:" + string.Join("|", SettingsLinks.Pages.OrderByDescending(p => p.Length).Select(Regex.Escape)) + ")";
        return _linkPattern = new Regex(
            @"(?<![\w#])#(?<ticket>\d{3,})\b|\bSettings\s*→\s*(?<path>" + page + @"(?:\s*→\s*" + page + @")*)(?![\w&])|(?<more>(?<![\w+])\+\d+ more)\b",
            RegexOptions.Compiled);
    }

    /// <summary>Space on each side of a tag, so it doesn't touch the words around it.</summary>
    private int TagMargin => (int)Math.Round(2 * TagScale);

    private int Width(string text, int style) =>
        text.Length == 0 ? 0 : style == 3 ? PriorityTag.Measure(text, TagScale).Width + 2 * TagMargin : TextRenderer.MeasureText(text, style is 1 or 2 ? bold : regular, Size.Empty, Flags).Width;

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
            if (run.IsTag && Tags)
            {
                tokens.Add((part, 3, null)); // one chip, wraps like a word
                continue;
            }
            var start = 0;
            for (var i = 0; i <= part.Length; i++)
            {
                var end = i == part.Length;
                if (!end && part[i] != ' ' && part[i] != '\n' && part[i] != '\t')
                    continue;
                if (!end && part[i] == '\t')
                {
                    if (i > start)
                        tokens.Add((part[start..i], style, link));
                    tokens.Add(("\t", style, null));
                    start = i + 1;
                    continue;
                }
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
        var cut = new HashSet<int>(); // rows cut short (OneRowPerLine): "…" at their end
        void NewLine()
        {
            line++;
            x = 0;
        }

        // A word can be several tokens ("(1\u00a0" + a tag + ")"): it wraps as a whole, where it can.
        static bool GluedToNext(List<(string Text, int Style, Link? Link)> all, int i) =>
            all[i].Text is not ("\n" or "\t") && !all[i].Text.EndsWith(' ') && i + 1 < all.Count && all[i + 1].Text is not ("\n" or "\t");
        // Where the word starting at token i ends (its glued parts included).
        static int WordEnd(List<(string Text, int Style, Link? Link)> all, int i)
        {
            while (GluedToNext(all, i))
                i++;
            return i;
        }
        for (var t = 0; t < tokens.Count; t++)
        {
            var (raw, tokenStyle, link) = tokens[t];
            if (cut.Contains(line) && raw != "\n")
                continue; // the rest of a row that didn't fit
            var glued = t > 0 && GluedToNext(tokens, t - 1); // the rest of a word that already wrapped where it had to
            if (raw == "\t")
            {
                // The rest of the line at the right edge, if it fits after what's there; otherwise on its own line.
                var rest = 0;
                for (var r = t + 1; r < tokens.Count && tokens[r].Text != "\n"; r++)
                    rest += Width(r == t + 1 ? tokens[r].Text.TrimStart() : tokens[r].Text, tokens[r].Style);
                var gap = Width("  ", 0);
                if (x > 0 && x + gap + rest > width)
                {
                    NewLine();
                    if (line >= maxLines) { truncated = true; break; }
                }
                else if (x > 0)
                    x = Math.Max(x + gap, width - rest);
                continue;
            }
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
            for (var next = t; !glued && GluedToNext(tokens, next); next++)
                w += Width(tokens[next + 1].Text.TrimEnd(), tokens[next + 1].Style);
            if (OneRowPerLine && x + Width(text.TrimEnd(), tokenStyle) > width)
            {
                // Doesn't fit on this row: as much as fits, then "…" (added below).
                var fit = tokenStyle == 3 ? 0 : text.Length; // a tag chip isn't cut in half: left out
                while (fit > 0 && x + Width(text[..fit].TrimEnd() + "\u2026", tokenStyle) > width)
                    fit--;
                if (fit > 0)
                    pieces.Add(new Piece(text[..fit].TrimEnd(), tokenStyle, x, line, paragraph, link));
                cut.Add(line);
                continue;
            }
            // No lonely last word: when this word fits but the paragraph's last word after it wouldn't, both go to the next line.
            if (!OneRowPerLine && !glued && x > 0 && x + w <= width && line + 1 < maxLines)
            {
                var end = WordEnd(tokens, t);
                if (end + 1 < tokens.Count && tokens[end + 1].Text is not ("\n" or "\t"))
                {
                    var lastEnd = WordEnd(tokens, end + 1);
                    if (lastEnd + 1 >= tokens.Count || tokens[lastEnd + 1].Text == "\n")
                    {
                        var lastW = 0;
                        for (var k = end + 1; k <= lastEnd; k++)
                            lastW += Width(tokens[k].Text.TrimEnd(), tokens[k].Style);
                        var space = Width(tokens[end].Text, tokens[end].Style) - Width(tokens[end].Text.TrimEnd(), tokens[end].Style);
                        if (x + w + space + lastW > width && w + space + lastW <= width)
                        {
                            NewLine();
                            text = text.TrimStart();
                        }
                    }
                }
            }
            if (x > 0 && x + w > width && (!glued || x + Width(text.TrimEnd(), tokenStyle) > width))
            {
                NewLine();
                if (line >= maxLines) { truncated = true; break; }
                text = text.TrimStart();
            }
            // A single word wider than the line (a long link): cut it by characters.
            while (tokenStyle != 3 && Width(text.TrimEnd(), tokenStyle) > width - x && text.Length > 1)
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
            if (merged.Count > 0 && piece.Style != 3 && merged[^1].Style != 3 && merged[^1].Line == piece.Line && merged[^1].Style == piece.Style && merged[^1].Paragraph == piece.Paragraph && merged[^1].Link == piece.Link)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + piece.Text };
            else
                merged.Add(piece);
        }

        foreach (var row in cut)
        {
            var i = merged.FindLastIndex(p => p.Line == row);
            if (i < 0)
                continue;
            var last = merged[i];
            if (last.Style == 3)
                merged.Insert(i + 1, new Piece("\u2026", 0, last.X + Width(last.Text, 3), row, last.Paragraph));
            else
            {
                var text = last.Text.TrimEnd();
                while (text.Length > 0 && last.X + Width(text + "\u2026", last.Style) > width)
                    text = text[..^1].TrimEnd();
                merged[i] = last with { Text = text + "\u2026" };
            }
        }

        if (truncated && merged.Count > 0)
        {
            // "…" at the end of the last line, shortening it until it fits.
            var last = merged[^1];
            if (last.Style == 3) // after a tag: "…" as text
                merged.Add(last = new Piece("", 0, last.X + Width(last.Text, 3), last.Line, last.Paragraph));
            var text = last.Text.TrimEnd();
            while (text.Length > 0 && last.X + Width(text + "…", last.Style) > width)
                text = text[..^1].TrimEnd();
            merged[^1] = last with { Text = text + "…" };
        }

        _pieces = merged;
        _lines = merged.Count == 0 ? 0 : merged.Max(p => p.Line) + 1;
    }
}
