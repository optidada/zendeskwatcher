using System.Text.RegularExpressions;

namespace ZendeskWatcher.UI;

/// <summary>
/// References to other Settings pages in texts become links: "(Notifications → Working hours)", "the Groups page", or
/// written out as [[Set my hours|Notifications/Working hours]]. A click shows that page and scrolls to the card.
/// </summary>
public static partial class SettingsLinks
{
    /// <summary>The sidebar's pages, as texts name them.</summary>
    public static readonly string[] Pages =
    [
        "Account", "Groups", "Clients", "Products", "Notifications", "Colleagues", "Behavior", "Look & sound", "SLA", "General",
        "Updates & feedback", "Insights", "How it works", "Experimental"
    ];

    /// <summary>Shows a page (and scrolls to the card with this title); set by the open Settings window, else null.</summary>
    public static Action<string, string?>? Go { get; set; }

    /// <summary>A text with at least one reference (then it's shown as a <see cref="LinkedLabel"/>).</summary>
    public static bool HasLinks(string? text) => !string.IsNullOrEmpty(text) && Parse(text).Links.Count > 0;

    /// <summary>The text as shown (markup removed) and its links: where they are and where they go.</summary>
    public static (string Text, List<(int Start, int Length, string Page, string? Section)> Links) Parse(string text)
    {
        var links = new List<(int, int, string, string?)>();
        // Written out: [[text|Page/Section]].
        var shown = Explicit().Replace(text, m => $"\u0001{m.Groups[1].Value}\u0002{m.Groups[2].Value}\u0003");
        var plain = new System.Text.StringBuilder();
        for (var i = 0; i < shown.Length; i++)
        {
            if (shown[i] != '\u0001')
            {
                plain.Append(shown[i]);
                continue;
            }
            var mid = shown.IndexOf('\u0002', i);
            var end = shown.IndexOf('\u0003', mid);
            var label = shown[(i + 1)..mid];
            var target = shown[(mid + 1)..end].Split('/', 2);
            links.Add((plain.Length, label.Length, target[0], target.Length > 1 ? target[1] : null));
            plain.Append(label);
            i = end;
        }
        var result = plain.ToString();

        // In brackets: "(Notifications → Working hours)", "(Groups page)".
        foreach (Match m in Bracketed().Matches(result))
            if (Resolve(m.Groups[1].Value) is var (page, section) && !Overlaps(links, m.Groups[1].Index))
                links.Add((m.Groups[1].Index, m.Groups[1].Length, page, section));
        // In a sentence: "on the Groups page".
        foreach (Match m in PageWord().Matches(result))
            if (Pages.Contains(m.Groups[1].Value) && !Overlaps(links, m.Index))
                links.Add((m.Index, m.Length, m.Groups[1].Value, null));
        links.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return (result, links);
    }

    private static bool Overlaps(List<(int Start, int Length, string, string?)> links, int at) =>
        links.Any(l => at >= l.Start && at < l.Start + l.Length);

    /// <summary>"Notifications → Behavior → Snooze" → (Behavior, Snooze); "Groups page" → (Groups, null); null if no page.</summary>
    internal static (string Page, string? Section)? Resolve(string reference)
    {
        var parts = reference.Split('→').Select(p => p.Trim()).Select(p => p.EndsWith(" page") ? p[..^5] : p).ToList();
        var last = parts.FindLastIndex(p => Pages.Contains(p));
        if (last < 0 || parts.Take(last).Any(p => !Pages.Contains(p)))
            return null; // not a path through the sidebar (e.g. "New → Open")
        var section = last + 1 < parts.Count ? parts[last + 1] : null;
        return section is { Length: > 0 and <= 40 } || section is null ? (parts[last], section) : null;
    }

    [GeneratedRegex(@"\[\[([^\]|]+)\|([^\]]+)\]\]")]
    private static partial Regex Explicit();

    [GeneratedRegex(@"\(([^()]+)\)")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"(?<=\bthe )(Account|Groups|Clients|Products|Notifications|Colleagues|Behavior|SLA|General|Insights|Experimental) page\b")]
    private static partial Regex PageWord();
}

/// <summary>A label that's a link as a whole (see <c>Kit.LinkLabel</c>); <see cref="Theme.Recolor"/> keeps it in the link color.</summary>
public sealed class LinkText : Label;

/// <summary>A label whose page references are links (see <see cref="SettingsLinks"/>); set <see cref="Text"/> as usual.</summary>
public sealed class LinkedLabel : LinkLabel
{
    public LinkedLabel()
    {
        AutoSize = true;
        UseMnemonic = false;
        LinkBehavior = LinkBehavior.HoverUnderline;
        LinkColor = Theme.Current.Link;
        ActiveLinkColor = Theme.Current.LinkHover;
        VisitedLinkColor = Theme.Current.Link;
        BackColor = Color.Transparent;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            var (text, links) = SettingsLinks.Parse(value ?? "");
            base.Text = text;
            Links.Clear();
            foreach (var (start, length, page, section) in links)
                Links.Add(start, length, (page, section));
        }
    }

    protected override void OnLinkClicked(LinkLabelLinkClickedEventArgs e)
    {
        base.OnLinkClicked(e);
        if (e.Link?.LinkData is ValueTuple<string, string?> target)
            SettingsLinks.Go?.Invoke(target.Item1, target.Item2);
    }
}
