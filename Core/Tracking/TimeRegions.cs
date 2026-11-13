using ZendeskWatcher.Zendesk;

namespace ZendeskWatcher.Tracking;

/// <summary>Regions the "Help colleagues" assignees are grouped by, in display order.</summary>
public enum TimeRegion { Americas, EuropeAfrica, AsiaPacific, Other, NoTimeZone }

public static class TimeRegions
{
    /// <summary>The region of an IANA time zone id ("Europe/Stockholm" → Europe & Africa); null → no time zone.</summary>
    public static TimeRegion Of(string? zone) => zone is null ? TimeRegion.NoTimeZone : zone.Split('/')[0] switch
    {
        "America" or "US" or "Canada" or "Brazil" or "Chile" or "Mexico" => TimeRegion.Americas,
        "Europe" or "Africa" or "Atlantic" or "Arctic" => TimeRegion.EuropeAfrica,
        "Asia" or "Australia" or "Pacific" or "Indian" or "Antarctica" => TimeRegion.AsiaPacific,
        _ => TimeRegion.Other
    };

    /// <summary>The IANA id of a profile's time zone, if it can be used (the same rule "Help colleagues" applies).</summary>
    public static string? ZoneOf(User? user) =>
        IanaId(TicketRules.ResolveTimeZone(user?.IanaTimeZone) ?? TicketRules.ResolveTimeZone(user?.TimeZone));

    /// <summary>This PC's time zone as an IANA id.</summary>
    public static string? LocalZone() => IanaId(TimeZoneInfo.Local);

    /// <summary>Your region: from your Zendesk profile, or this PC's time zone.</summary>
    public static TimeRegion Mine(User? me) => Of(ZoneOf(me) ?? LocalZone());

    private static string? IanaId(TimeZoneInfo? zone) =>
        zone is null ? null
        : zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null;

    public static string Name(TimeRegion region) => region switch
    {
        TimeRegion.Americas => "Americas",
        TimeRegion.EuropeAfrica => "Europe & Africa",
        TimeRegion.AsiaPacific => "Asia & Pacific",
        TimeRegion.Other => "Other time zones",
        _ => "No time zone (always skipped)"
    };
}
