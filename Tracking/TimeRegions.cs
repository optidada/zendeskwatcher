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

    public static string Name(TimeRegion region) => region switch
    {
        TimeRegion.Americas => "Americas",
        TimeRegion.EuropeAfrica => "Europe & Africa",
        TimeRegion.AsiaPacific => "Asia & Pacific",
        TimeRegion.Other => "Other time zones",
        _ => "No time zone (always skipped)"
    };
}
