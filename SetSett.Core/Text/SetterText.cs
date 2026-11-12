using System.Globalization;
using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Text;

/// <summary>Everything a setter, condition, or value says on screen. Sentence case, no jargon.</summary>
public static class SetterText
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public static string Value(SettingInfo info, SettingValue value) => (info.Kind, value) switch
    {
        (ValueKind.OnOff, OnOffValue onOff) => onOff.On ? info.OnText : info.OffText,
        (ValueKind.Percent, NumberValue number) => $"{number.Value}%",
        (ValueKind.Duration, NumberValue number) => Duration(number.Value),
        (ValueKind.Choice, ChoiceValue choice) =>
            info.Choices.FirstOrDefault(c => string.Equals(c.Id, choice.Id, StringComparison.OrdinalIgnoreCase))?.Name ?? choice.Id,
        _ => "?",
    };

    /// <summary>"Never", "45 sec", "5 min", "1 hr", "1 hr 30 min".</summary>
    public static string Duration(int seconds)
    {
        if (seconds <= 0)
        {
            return "Never";
        }

        if (seconds < 60)
        {
            return $"{seconds} sec";
        }

        int minutes = seconds / 60;
        return Minutes(minutes);
    }

    public static string Minutes(int minutes)
    {
        int hours = minutes / 60;
        int rest = minutes % 60;
        return (hours, rest) switch
        {
            (0, _) => $"{rest} min",
            (_, 0) => $"{hours} hr",
            _ => $"{hours} hr {rest} min",
        };
    }

    public static string Time(TimeOnly time) => time.ToString("t", Culture);

    public static string Time(DateTime local) => local.ToString("t", Culture);

    public static string Days(WeekDays days) => days switch
    {
        WeekDays.Every => "every day",
        WeekDays.Weekdays => "on weekdays",
        WeekDays.Weekend => "on weekends",
        WeekDays.None => "on no days",
        _ => "on " + string.Join(", ", Week()
            .Where(d => days.HasFlag(TimeWindowCondition.DayFlag(d)))
            .Select(d => Culture.DateTimeFormat.GetAbbreviatedDayName(d))),
    };

    /// <summary>
    /// "between 10:00 PM and 7:00 AM every day", or with different hours on some days
    /// "9:00 AM–5:00 PM on weekdays, 10:00 AM–2:00 PM on Sat".
    /// </summary>
    public static string Hours(TimeWindowCondition t)
    {
        if (t.AtTime)
        {
            return Moments(t);
        }

        List<((TimeOnly From, TimeOnly To) Hours, WeekDays Days)> groups = [.. Week()
            .Where(t.IsOn)
            .GroupBy(t.HoursOn)
            .Select(g => (g.Key, g.Aggregate(WeekDays.None, (days, day) => days | TimeWindowCondition.DayFlag(day))))];

        if (groups.Count <= 1)
        {
            (TimeOnly from, TimeOnly to) = groups.Count == 1 ? groups[0].Hours : (t.From, t.To);
            return $"between {Time(from)} and {Time(to)} {Days(groups.Count == 1 ? groups[0].Days : WeekDays.None)}";
        }

        return string.Join(", ", groups.Select(g => $"{Time(g.Hours.From)}–{Time(g.Hours.To)} {Days(g.Days)}"));
    }

    /// <summary>"at 9:00 AM every day", or with times that differ "at 9:00 AM on weekdays, 10:00 AM on Sat".</summary>
    private static string Moments(TimeWindowCondition t)
    {
        List<(TimeOnly At, WeekDays Days)> groups = [.. Week()
            .Where(t.IsOn)
            .GroupBy(t.MomentOn)
            .Select(g => (g.Key, g.Aggregate(WeekDays.None, (days, day) => days | TimeWindowCondition.DayFlag(day))))];

        return groups.Count switch
        {
            0 => $"at {Time(t.From)} {Days(WeekDays.None)}",
            _ => "at " + string.Join(", ", groups.Select(g => $"{Time(g.At)} {Days(g.Days)}")),
        };
    }

    /// <summary>A start condition as a phrase: "after sunset", "connected to Home Wi-Fi".</summary>
    public static string Condition(Condition condition) => condition switch
    {
        TimeWindowCondition t => Hours(t),
        SunCondition s => Sun(s),
        NetworkCondition n => $"{(n.Not ? "not connected" : "connected")} to {Named(n.Network, "a network")}",
        DeviceCondition d => $"{Named(d.Device, "a device")} {(d.Not ? "isn't" : "is")} connected",
        ProcessCondition p => $"{Named(p.Process, "an app")} {(p.Not ? "isn't" : "is")} running",
        PowerCondition p => Power(p),
        PlaceCondition p => $"{(p.Not ? "away from" : "at")} {Named(p.Place, "a place")}",
        ElapsedCondition e => $"after {Minutes(e.Minutes)}",
        ClockCondition c => $"at {Time(c.At)}",
        CpuQuietCondition c => $"the CPU stays under {c.Percent}% for {Minutes(c.Minutes)}",
        _ => "something",
    };

    /// <summary>
    /// How a start condition that is a span of time also ends the setter: "at sunset", "at 7:00 AM". Null for
    /// conditions that don't name their own end, which need no entry under "When it stops".
    /// </summary>
    public static string? EndsAt(Condition condition)
    {
        switch (condition)
        {
            case SunCondition s:
                return s.Phase == SunPhase.Night ? Offset(s.SunriseOffsetMinutes, "sunrise") : Offset(s.SunsetOffsetMinutes, "sunset");
            case TimeWindowCondition { AtTime: true }:
                return null;
            case TimeWindowCondition t:
                var ends = Week().Where(t.IsOn).Select(d => t.HoursOn(d).To).Distinct().ToList();
                return ends.Count == 1 ? Time(ends[0]) : "the end of its hours";
            default:
                return null;
        }
    }

    /// <summary>A stop condition as an "until" phrase: "for 30 min", "until 5:00 PM", "until blender closes".</summary>
    public static string Until(Condition condition) => condition switch
    {
        ElapsedCondition e => $"for {Minutes(e.Minutes)}",
        ClockCondition c => $"until {Time(c.At)}",
        CpuQuietCondition c => $"until the CPU stays under {c.Percent}% for {Minutes(c.Minutes)}",
        ProcessCondition { Not: true } p => $"until {Named(p.Process, "the app")} closes",
        ProcessCondition p => $"until {Named(p.Process, "an app")} starts",
        NetworkCondition { Not: true } n => $"until disconnected from {Named(n.Network, "the network")}",
        DeviceCondition { Not: true } d => $"until {Named(d.Device, "the device")} disconnects",
        SunCondition { Phase: SunPhase.Day } s => $"until {Offset(s.SunriseOffsetMinutes, "sunrise")}",
        SunCondition s => $"until {Offset(s.SunsetOffsetMinutes, "sunset")}",
        _ => "until " + Condition(condition),
    };

    /// <summary>
    /// What a condition still needs before it can ever be true ("Pick a network"), or null when it has
    /// what it needs. The phrases above stay readable without it ("connected to a network"), which suits
    /// a sentence but would pass for a real choice on the condition's own row.
    /// </summary>
    public static string? Missing(Condition condition, bool hasHome) => condition switch
    {
        NetworkCondition n when string.IsNullOrWhiteSpace(n.Network) => "Pick a network",
        DeviceCondition d when string.IsNullOrWhiteSpace(d.Device) => "Pick a device",
        ProcessCondition p when string.IsNullOrWhiteSpace(p.Process) => "Pick an app",
        PlaceCondition { Center: null } => "Set where this place is",
        SunCondition when !hasHome => "Set your location",
        _ => null,
    };

    /// <summary>"after sunset and on weekdays", for an automatic setter's start.</summary>
    public static string When(Setter setter) => setter.When.Count == 0
        ? "no conditions yet"
        : string.Join(setter.Match == ConditionMatch.All ? " and " : " or ", setter.When.Select(Condition));

    /// <summary>"Dark mode, Volume 20%".</summary>
    public static string Changes(Setter setter, ISettingCatalog catalog) => setter.Changes.Count == 0
        ? "Nothing to change yet"
        : string.Join(", ", SettingFamilies.Lines(setter.Changes, catalog).Select(line =>
        {
            SettingChange c = setter.Changes[line.Indexes[0]];
            if (catalog.Find(c.SettingId) is not { } s)
            {
                return c.SettingId;
            }

            // A family set everywhere reads as the setting alone: "Screen off after Never".
            return $"{(line.Indexes.Count > 1 ? s.Info.Name : s.Info.DisplayName)} {Value(s.Info, c.Value)}";
        }));

    /// <summary>Plain-language import summary shown before a .sett file is added.</summary>
    public static string ImportSummary(Setter setter, ISettingCatalog catalog)
    {
        string starts = setter.IsAutomatic ? When(setter) : "when you click it";
        List<string> stops = setter.HasTrigger
            ? []
            : setter.IsAutomatic
                ? ["when its start conditions end"]
                : ["when you click it again"];
        stops.AddRange(setter.Until.Select(Until));

        if (setter.AppliesOnce)
        {
            return $"Changes: {Changes(setter, catalog)}.\n" +
                   $"Starts: {starts}.\n" +
                   "Afterwards: sets them once and leaves the new values in place.";
        }

        return $"Changes: {Changes(setter, catalog)}.\n" +
               $"Starts: {starts}.\n" +
               $"Stops: {string.Join(", or ", stops)}.\n" +
               (setter.PutBack
                   ? "Afterwards: puts each setting back if it has not changed."
                   : "Afterwards: leaves the new setting values in place.");
    }

    /// <summary>The flyout's second line for a setter: what it's doing now and until when.</summary>
    public static string Status(Setter setter, RunState state, SensorSnapshot now)
    {
        if (!setter.CanRun)
        {
            return setter.Changes.Count == 0 ? "Pick something to change" : "Add a condition to start it";
        }

        if (state.RunOf(setter.Id) is { } run)
        {
            return "On " + RunningUntil(setter, run, now);
        }

        if (!setter.Enabled)
        {
            return "Disabled";
        }

        if (setter.IsAutomatic)
        {
            if (state.IsPaused(setter.Id))
            {
                return "Paused · starts again " + When(setter);
            }

            return state.IsLatched(setter.Id)
                ? "Stopped until its conditions reset"
                : "Starts " + When(setter);
        }

        return setter.Until.Count > 0 ? $"Off · runs {Until(setter.Until[0])}" : "Off";
    }

    private static string RunningUntil(Setter setter, SetterRun run, SensorSnapshot now)
    {
        // The soonest clock stop, so the text agrees with the tray icon's time-left indicator.
        if (run.EndsAtUtc(setter, now) is { } end)
        {
            return $"until {Time(Local(end, now))}";
        }

        if (setter.Until.Count > 0)
        {
            return Until(setter.Until[0]);
        }

        return $"since {Time(Local(run.ActiveSinceUtc, now))}";
    }

    private static DateTime Local(DateTime utc, SensorSnapshot now) => TimeZoneInfo.ConvertTimeFromUtc(utc, now.TimeZone);

    /// <summary>The days of the week in the order the culture's week starts.</summary>
    private static IEnumerable<DayOfWeek> Week()
    {
        int first = (int)Culture.DateTimeFormat.FirstDayOfWeek;
        return Enumerable.Range(0, 7).Select(i => (DayOfWeek)((first + i) % 7));
    }

    private static string Named(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();

    private static string Sun(SunCondition s)
    {
        string set = Offset(s.SunsetOffsetMinutes, "sunset");
        string rise = Offset(s.SunriseOffsetMinutes, "sunrise");
        return s.Phase == SunPhase.Night ? $"between {set} and {rise}" : $"between {rise} and {set}";
    }

    private static string Offset(int minutes, string boundary) => minutes switch
    {
        0 => boundary,
        < 0 => $"{Minutes(-minutes)} before {boundary}",
        _ => $"{Minutes(minutes)} after {boundary}",
    };

    private static string Power(PowerCondition p)
    {
        string text = p.Check switch
        {
            PowerCheck.PluggedIn => "plugged in",
            PowerCheck.OnBattery => "on battery",
            PowerCheck.BatteryBelow => $"battery below {p.Percent}%",
            _ => $"battery at {p.Percent}% or more",
        };
        return p.Not ? "not " + text : text;
    }
}
