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
        Format(TimeRangeMath.WrapToDay(timeOfDay), culture, use24Hour, wholeHour: false);

    /// <summary>A whole hour for an axis label: "6 AM" or "06:00".</summary>
    public static string FormatHour(int hour, CultureInfo culture, bool use24Hour) =>
        Format(TimeSpan.FromHours(hour % 24), culture, use24Hour, wholeHour: true);

    /// <summary>
    /// Lays the time out the way the culture's own short time pattern does (separator, and whether AM/PM comes first),
    /// switched to the 12- or 24-hour clock the user chose. A culture with no AM/PM text of its own borrows English's.
    /// </summary>
    private static string Format(TimeSpan time, CultureInfo culture, bool use24Hour, bool wholeHour)
    {
        var pattern = culture.DateTimeFormat.ShortTimePattern;
        var designatorFirst = pattern.IndexOf('t') is var t && t >= 0 && t < pattern.IndexOfAny(['h', 'H']);
        var core = DesignatorPattern().Replace(pattern, string.Empty).Trim();
        core = HourPattern().Replace(core, use24Hour ? "HH" : "h");
        if (!core.Contains('m'))
        {
            core += ":mm";
        }

        if (wholeHour && !use24Hour)
        {
            core = MinutesPattern().Replace(core, string.Empty).Trim();
        }

        var format = use24Hour ? core : designatorFirst ? $"tt {core}" : $"{core} tt";
        var designators = string.IsNullOrEmpty(culture.DateTimeFormat.AMDesignator) ? CultureInfo.GetCultureInfo("en-US") : culture;
        return (DateTime.MinValue + time).ToString(format, designators);
    }

    [GeneratedRegex(@"\s*t+\s*")]
    private static partial Regex DesignatorPattern();

    [GeneratedRegex("[hH]+")]
    private static partial Regex HourPattern();

    [GeneratedRegex(@"[^\shHm]*m+[^\shHm]*")]
    private static partial Regex MinutesPattern();

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
