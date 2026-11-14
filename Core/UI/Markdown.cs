using System.Net;
using System.Text;

namespace ZendeskWatcher.UI;

/// <summary>
/// The little markdown notification texts use: <c>**bold**</c>, <c>!!alert!!</c> (bold in the theme's urgent color,
/// e.g. an SLA deadline), and <c>\</c> to escape a character. Text from Zendesk (comments, names) goes through
/// <see cref="Escape"/>, so a customer typing ** or !! doesn't change anything.
/// </summary>
public static class Markdown
{
    /// <summary>A piece of text; <see cref="Alert"/> is bold too, in the alert color.</summary>
    public readonly record struct Run(string Text, bool Bold, bool Alert = false);

    /// <summary>Makes text show exactly as written.</summary>
    public static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("!", "\\!");

    /// <summary>The text in pieces, bold, alert or neither. A marker without a partner shows as written.</summary>
    public static List<Run> Parse(string? markdown)
    {
        var runs = new List<Run>();
        if (string.IsNullOrEmpty(markdown))
            return runs;

        // Where the markers are, per kind; an odd one out (no partner) stays literal.
        HashSet<int> Markers(char mark)
        {
            var found = new List<int>();
            for (var i = 0; i < markdown.Length; i++)
            {
                if (markdown[i] == '\\') { i++; continue; }
                if (markdown[i] == mark && i + 1 < markdown.Length && markdown[i + 1] == mark) { found.Add(i); i++; }
            }
            if (found.Count % 2 == 1)
                found.RemoveAt(found.Count - 1);
            return found.ToHashSet();
        }
        var boldToggles = Markers('*');
        var alertToggles = Markers('!');

        var current = new StringBuilder();
        var bold = false;
        var alert = false;
        void Flush()
        {
            if (current.Length > 0)
                runs.Add(new Run(current.ToString(), bold || alert, alert));
            current.Clear();
        }
        for (var i = 0; i < markdown.Length; i++)
        {
            var c = markdown[i];
            if (c == '\\' && i + 1 < markdown.Length)
            {
                current.Append(markdown[++i]);
            }
            else if (boldToggles.Contains(i))
            {
                Flush();
                bold = !bold;
                i++;
            }
            else if (alertToggles.Contains(i))
            {
                Flush();
                alert = !alert;
                i++;
            }
            else
            {
                current.Append(c);
            }
        }
        Flush();
        return runs;
    }

    /// <summary>The plain text, without markers (Windows notifications, logs, plain-text copies).</summary>
    public static string Strip(string? markdown) => string.Concat(Parse(markdown).Select(r => r.Text));

    /// <summary>HTML with &lt;b&gt; for bold, red for alerts (copies for Teams).</summary>
    public static string ToHtml(string? markdown) => string.Concat(Parse(markdown).Select(r =>
        r.Alert ? $"<b style=\"color:#d13438\">{WebUtility.HtmlEncode(r.Text)}</b>"
        : r.Bold ? $"<b>{WebUtility.HtmlEncode(r.Text)}</b>" : WebUtility.HtmlEncode(r.Text)));

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
            result.Append(run.Alert ? $"!!{Escape(text)}!!" : run.Bold ? $"**{Escape(text)}**" : Escape(text));
        }
        return result.ToString();
    }
}
