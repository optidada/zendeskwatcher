using System.Globalization;

namespace ZendeskWatcher;

/// <summary>
/// Times as this PC's clock shows them: 24-hour ("17:00") or 12-hour ("5:00 PM"), from the Windows time format
/// (Settings → Time &amp; language). The words stay English, like the rest of the app.
/// </summary>
public static class Clock
{
    /// <summary>12-hour clock with AM/PM. From Windows; tests set it.</summary>
    public static bool TwelveHour { get; set; } = UsesTwelveHour(CultureInfo.CurrentCulture);

    /// <summary>A short time pattern with "h" (not "H") is a 12-hour clock.</summary>
    internal static bool UsesTwelveHour(CultureInfo culture) => culture.DateTimeFormat.ShortTimePattern.Contains('h');

    /// <summary>"17:00" or "5:00 PM".</summary>
    public static string Time(DateTime time) =>
        time.ToString(TwelveHour ? "h:mm tt" : "HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"Fri 17:00" or "Fri 5:00 PM".</summary>
    public static string DayTime(DateTime time) => $"{time.ToString("ddd", CultureInfo.InvariantCulture)} {Time(time)}";

    /// <summary>A whole hour of the day, 0–24: "08:00" or "8:00 AM" (24 is midnight at the end of the day).</summary>
    public static string Hour(int hour) => Time(DateTime.MinValue.AddHours(hour % 24));

    /// <summary>A short mark on a 0–24 scale: "18" or "6p".</summary>
    public static string HourMark(int hour)
    {
        if (!TwelveHour)
            return hour.ToString(CultureInfo.InvariantCulture);
        var h = hour % 24;
        return $"{(h % 12 == 0 ? 12 : h % 12)}{(h < 12 ? "a" : "p")}";
    }
}
