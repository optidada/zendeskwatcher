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
    /// <summary>A piece of text; <see cref="Alert"/> is bold too, in the alert color; <see cref="IsTag"/>: a priority
    /// tag ("P1"), drawn as a colored chip where the style draws tags, otherwise as plain text.</summary>
    public readonly record struct Run(string Text, bool Bold, bool Alert = false, bool IsTag = false);

    /// <summary>Around a priority tag: private-use characters, which no text of ours uses; <see cref="Escape"/>
    /// escapes them in text from Zendesk, so they show as written there.</summary>
    private const char TagOpen = '\uE000', TagClose = '\uE001';

    /// <summary>A priority as a tag in the text: "urgent" or "P1" → [P1] (a colored chip; plain "P1" in
    /// <see cref="Strip"/> and in styles without tags). Unknown priorities come back as plain escaped text.</summary>
    public static string Tag(string? priority)
    {
        var tag = priority?.ToLowerInvariant() switch
        {
            "urgent" or "p1" => "P1",
            "high" or "p2" => "P2",
            "normal" or "p3" => "P3",
            "low" or "p4" => "P4",
            _ => null
        };
        return tag is null ? Escape(priority) : $"{TagOpen}{tag}{TagClose}";
    }

    /// <summary>The priority a tag stands for: "P1" → "urgent" … "P4" → "low".</summary>
    public static string PriorityOfTag(string tag) => tag switch
    {
        "P1" => "urgent",
        "P2" => "high",
        "P3" => "normal",
        _ => "low"
    };

    /// <summary>Makes text show exactly as written.</summary>
    public static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("!", "\\!")
            .Replace(TagOpen.ToString(), "\\" + TagOpen).Replace(TagClose.ToString(), "\\" + TagClose);

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
            else if (c == TagOpen && markdown.IndexOf(TagClose, i + 1) is var close and > 0 && close > i + 1)
            {
                Flush();
                runs.Add(new Run(markdown[(i + 1)..close], bold || alert, alert, IsTag: true));
                i = close;
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

    /// <summary>The plain text, without markers (logs, plain-text copies).</summary>
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
            if (run.IsTag && text.Length == run.Text.Length)
                result.Append(Tag(text)); // a tag shows once it's whole
            else
                result.Append(run.Alert ? $"!!{Escape(text)}!!" : run.Bold ? $"**{Escape(text)}**" : Escape(text));
        }
        return result.ToString();
    }
}
