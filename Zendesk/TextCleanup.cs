using System.Net;
using System.Text.RegularExpressions;

namespace ZendeskWatcher.Zendesk;

/// <summary>Turns Zendesk text (comment bodies, subjects) into clean plain text for notifications.</summary>
public static partial class TextCleanup
{
    /// <summary>
    /// Decodes HTML entities (also double-encoded ones like "&amp;nbsp;", which Zendesk sometimes sends),
    /// removes any HTML tags, turns non-breaking and other odd spaces into normal ones and collapses whitespace.
    /// </summary>
    public static string Plain(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var result = text;
        if (result.Contains('<'))
            result = Tags().Replace(result, " ");

        // Decode until nothing changes: "&amp;nbsp;" -> "&nbsp;" -> non-breaking space.
        for (var i = 0; i < 3 && result.Contains('&'); i++)
        {
            var decoded = WebUtility.HtmlDecode(result);
            if (decoded == result)
                break;
            result = decoded;
        }

        result = result
            .Replace((char)0x00A0, (char)0x20)   // non-breaking space
            .Replace((char)0x202F, (char)0x20)   // narrow non-breaking space
            .Replace(((char)0x200B).ToString(), "")   // zero-width space
            .Replace(((char)0xFEFF).ToString(), "");  // zero-width no-break space / byte order mark
        return Whitespace().Replace(result, " ").Trim();
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
