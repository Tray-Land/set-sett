using System.Globalization;
using System.Text.RegularExpressions;
using SetSett.Model;

namespace SetSett.Text;

/// <summary>Display and forgiving parsing of times of day for the editor's dial and week view.</summary>
public static partial class TimeRangeFormat
{
    // 9, 930, 9:30, 9.30, 0930, 9pm, 9:30 p.m., 21:15
    [GeneratedRegex(@"^\s*(\d{1,2})(?:[:.h]?(\d{2}))?\s*(?:([ap])\.?m?\.?)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    public static bool Is24Hour(CultureInfo culture) => culture.DateTimeFormat.ShortTimePattern.Contains('H');

    public static string FormatTime(TimeSpan timeOfDay, CultureInfo culture, bool use24Hour) =>
        (DateTime.MinValue + TimeRangeMath.WrapToDay(timeOfDay)).ToString(use24Hour ? "HH:mm" : "h:mm tt", culture);

    /// <summary>A whole hour for an axis label: "6 AM" or "06:00".</summary>
    public static string FormatHour(int hour, CultureInfo culture, bool use24Hour) =>
        (DateTime.MinValue + TimeSpan.FromHours(hour % 24)).ToString(use24Hour ? "HH:mm" : "h tt", culture);

    public static string FormatDuration(TimeSpan duration)
    {
        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return hours == 0 ? $"{minutes}m" : minutes == 0 ? $"{hours}h" : $"{hours}h {minutes}m";
    }

    public static string FormatRange(TimeRangeValue value, CultureInfo culture, bool use24Hour)
    {
        var start = FormatTime(value.Start, culture, use24Hour);
        if (value.End is not { } end)
        {
            return start;
        }

        if (value.Start == TimeSpan.Zero && end == TimeRangeMath.Day)
        {
            return "All day";
        }

        var text = $"{start} – {FormatTime(end, culture, use24Hour)}";
        return end > TimeRangeMath.Day ? $"{text} +1" : text;
    }

    /// <summary>
    /// Reads a time of day. Without am/pm the hour is 24-hour, so "5" is 5 in the morning; "24:00" is accepted and means
    /// midnight.
    /// </summary>
    public static bool TryParseTime(string? text, out TimeSpan timeOfDay)
    {
        timeOfDay = default;
        if (text is null || TimePattern().Match(text) is not { Success: true } match)
        {
            return false;
        }

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (minute > 59)
        {
            return false;
        }

        if (match.Groups[3].Success)
        {
            if (hour is < 1 or > 12)
            {
                return false;
            }

            hour = hour % 12 + (char.ToLowerInvariant(match.Groups[3].Value[0]) == 'p' ? 12 : 0);
        }
        else if (hour > 24 || (hour == 24 && minute != 0))
        {
            return false;
        }

        timeOfDay = TimeRangeMath.WrapToDay(new TimeSpan(hour, minute, 0));
        return true;
    }
}
