namespace ZendeskWatcher.UI.Popups;

/// <summary>
/// Word-wrapped text with bold and alert parts (<see cref="Markdown"/>), for the notification body. TextRenderer can't mix fonts
/// in one call, so this lays out the words itself; the result is cached, as popups repaint often.
/// </summary>
internal sealed class RichText(Font regular, Font bold)
{
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    /// <summary>Style: 0 regular, 1 bold, 2 alert (bold in the alert color).</summary>
    private readonly record struct Piece(string Text, int Style, int X, int Line);

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

    public void Draw(Graphics g, string markdown, Rectangle rect, int maxLines, Color color, Color boldColor, Color? alertColor = null)
    {
        Layout(markdown, rect.Width, maxLines);
        foreach (var piece in _pieces)
        {
            var at = new Point(rect.Left + piece.X, rect.Top + piece.Line * LineHeight);
            var pieceColor = piece.Style switch { 2 => alertColor ?? boldColor, 1 => boldColor, _ => color };
            TextRenderer.DrawText(g, piece.Text, piece.Style > 0 ? bold : regular, at, pieceColor, Flags);
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

    private int Width(string text, int style) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, style > 0 ? bold : regular, Size.Empty, Flags).Width;

    private void Layout(string markdown, int width, int maxLines)
    {
        if (markdown == _cachedText && width == _cachedWidth && maxLines == _cachedMaxLines)
            return;
        _cachedText = markdown;
        _cachedWidth = width;
        _cachedMaxLines = maxLines;

        // Words (with the spaces after them) and line breaks, each knowing its style.
        var tokens = new List<(string Text, int Style)>();
        foreach (var run in Markdown.Parse(markdown))
        {
            var style = run.Alert ? 2 : run.Bold ? 1 : 0;
            var start = 0;
            for (var i = 0; i <= run.Text.Length; i++)
            {
                var end = i == run.Text.Length;
                if (!end && run.Text[i] != ' ' && run.Text[i] != '\n')
                    continue;
                if (!end && run.Text[i] == ' ')
                {
                    tokens.Add((run.Text[start..(i + 1)], style));
                    start = i + 1;
                    continue;
                }
                if (i > start)
                    tokens.Add((run.Text[start..i], style));
                if (!end)
                    tokens.Add(("\n", style));
                start = i + 1;
            }
        }

        var pieces = new List<Piece>();
        var line = 0;
        var x = 0;
        var truncated = false;
        void NewLine()
        {
            line++;
            x = 0;
        }

        foreach (var (raw, tokenStyle) in tokens)
        {
            if (raw == "\n")
            {
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
                pieces.Add(new Piece(text[..fit], tokenStyle, x, line));
                text = text[fit..];
                NewLine();
                if (line >= maxLines) { truncated = true; break; }
            }
            if (truncated)
                break;
            pieces.Add(new Piece(text, tokenStyle, x, line));
            x += Width(text, tokenStyle);
        }

        // Neighbors on the same line in the same style become one piece (fewer draw calls, natural spacing).
        var merged = new List<Piece>();
        foreach (var piece in pieces)
        {
            if (merged.Count > 0 && merged[^1].Line == piece.Line && merged[^1].Style == piece.Style)
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
