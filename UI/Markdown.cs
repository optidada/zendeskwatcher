using System.Net;
using System.Text;

namespace ZendeskWatcher.UI;

/// <summary>
/// The little markdown notification texts use: <c>**bold**</c>, and <c>\</c> to escape a character. Text from Zendesk
/// (comments, names) goes through <see cref="Escape"/>, so a customer typing ** doesn't turn anything bold.
/// </summary>
public static class Markdown
{
    public readonly record struct Run(string Text, bool Bold);

    /// <summary>Makes text show exactly as written.</summary>
    public static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\\", "\\\\").Replace("*", "\\*");

    /// <summary>The text in pieces, bold or not. A ** without a partner shows as written.</summary>
    public static List<Run> Parse(string? markdown)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(markdown))
            return runs;

        // Where the bold markers are; an odd one out (no partner) stays literal.
        var markers = new List<int>();
        for (var i = 0; i < markdown.Length; i++)
        {
            if (markdown[i] == '\\') { i++; continue; }
            if (markdown[i] == '*' && i + 1 < markdown.Length && markdown[i + 1] == '*') { markers.Add(i); i++; }
        }
        if (markers.Count % 2 == 1)
            markers.RemoveAt(markers.Count - 1);
        var toggles = markers.ToHashSet();

        var current = new StringBuilder();
        var bold = false;
        for (var i = 0; i < markdown.Length; i++)
        {
            var c = markdown[i];
            if (c == '\\' && i + 1 < markdown.Length)
            {
                current.Append(markdown[++i]);
            }
            else if (toggles.Contains(i))
            {
                if (current.Length > 0)
                    runs.Add(new Run(current.ToString(), bold));
                current.Clear();
                bold = !bold;
                i++;
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            runs.Add(new Run(current.ToString(), bold));
        return runs;
    }

    /// <summary>The plain text, without markers (Windows notifications, logs, plain-text copies).</summary>
    public static string Strip(string? markdown) => string.Concat(Parse(markdown).Select(r => r.Text));

    /// <summary>HTML with &lt;b&gt; for bold (copies for Teams).</summary>
    public static string ToHtml(string? markdown) => string.Concat(Parse(markdown).Select(r =>
        r.Bold ? $"<b>{WebUtility.HtmlEncode(r.Text)}</b>" : WebUtility.HtmlEncode(r.Text)));

    /// <summary>The first <paramref name="visibleChars"/> characters that show, still as valid markdown (the terminal's typing effect).</summary>
    public static string Truncate(string? markdown, int visibleChars)
    {
        var result = new StringBuilder();
        foreach (var run in Parse(markdown))
        {
            if (visibleChars <= 0)
                break;
            var text = run.Text.Length <= visibleChars ? run.Text : run.Text[..visibleChars];
            visibleChars -= text.Length;
            result.Append(run.Bold ? $"**{Escape(text)}**" : Escape(text));
        }
        return result.ToString();
    }
}
