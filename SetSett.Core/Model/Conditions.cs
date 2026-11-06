using System.Text.Json.Serialization;
using SetSett.Solar;

namespace SetSett.Model;

/// <summary>
/// One test about the PC's surroundings: the plugin seam for "when is it set". A setter starts while
/// its <see cref="Setter.When"/> conditions hold and stops when any <see cref="Setter.Until"/> one is
/// met. <see cref="Not"/> flips the answer ("not connected to", "isn't running").
/// </summary>
/// <remarks>
/// To add a kind: derive here and register it below for JSON, describe it in <c>SetterText</c> (with
/// <c>Missing</c> if it can be left unset), give it a sensor in the app's <c>SetterHost</c> if it reads
/// a new one, and an editor row in the app's <c>ConditionEditors</c>.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TimeWindowCondition), "time")]
[JsonDerivedType(typeof(SunCondition), "sun")]
[JsonDerivedType(typeof(NetworkCondition), "network")]
[JsonDerivedType(typeof(DeviceCondition), "device")]
[JsonDerivedType(typeof(ProcessCondition), "process")]
[JsonDerivedType(typeof(PowerCondition), "power")]
[JsonDerivedType(typeof(PlaceCondition), "place")]
[JsonDerivedType(typeof(ElapsedCondition), "elapsed")]
[JsonDerivedType(typeof(ClockCondition), "clock")]
[JsonDerivedType(typeof(CpuQuietCondition), "cpuQuiet")]
public abstract record Condition
{
    public bool Not { get; init; }

    public abstract Sensors Sensors { get; }

    /// <summary>
    /// False for stop-only kinds, which measure from the moment a setter started ("after 30
    /// minutes", "CPU quiet for 5 minutes") and so can't start one.
    /// </summary>
    public virtual bool CanStart => true;

    /// <summary>The answer after <see cref="Not"/>; null when it isn't known yet.</summary>
    public bool? Evaluate(ConditionContext context) => Test(context) is { } result ? result != Not : null;

    /// <summary>The next moment (UTC) the answer can change by the clock alone; null if only a sensor can change it.</summary>
    public virtual DateTime? NextChange(ConditionContext context) => null;

    /// <summary>The raw answer, before <see cref="Not"/>.</summary>
    protected abstract bool? Test(ConditionContext context);
}

[Flags]
public enum WeekDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    Every = Weekdays | Weekend
}

/// <summary>One day's hours in a <see cref="TimeWindowCondition"/>, when they differ from its From and To.</summary>
public sealed record DayHours(DayOfWeek Day, TimeOnly From, TimeOnly To);

/// <summary>
/// Between From and To on the chosen days, or with different hours on some days (<see cref="PerDay"/>).
/// A window that ends at or before it starts runs overnight (22:00–06:00) and belongs to the day it
/// starts on; From == To is a full 24 hours.
/// </summary>
public sealed record TimeWindowCondition(TimeOnly From, TimeOnly To, WeekDays Days) : Condition
{
    /// <summary>
    /// The days whose hours differ from From–To, at most one entry per day. Empty means every chosen
    /// day uses From–To. An entry for a day that's off is kept, so turning the day back on brings its
    /// hours back.
    /// </summary>
    public IReadOnlyList<DayHours> PerDay { get; set; } = [];

    public override Sensors Sensors => Sensors.Clock;

    /// <summary>Every day is on with the same hours, so one dial says it all (the editor shows the week otherwise).</summary>
    public bool IsSameEveryDay => Days == WeekDays.Every && PerDay.All(d => d.From == From && d.To == To);

    public static WeekDays DayFlag(DayOfWeek day) => (WeekDays)(1 << (int)day);

    public bool IsOn(DayOfWeek day) => Days.HasFlag(DayFlag(day));

    public (TimeOnly From, TimeOnly To) HoursOn(DayOfWeek day) =>
        PerDay.FirstOrDefault(d => d.Day == day) is { } hours ? (hours.From, hours.To) : (From, To);

    /// <summary>Turns a day on or off and gives it hours. Hours that match From–To don't need an entry of their own.</summary>
    public TimeWindowCondition WithDay(DayOfWeek day, bool on, TimeOnly from, TimeOnly to)
    {
        WeekDays flag = DayFlag(day);
        List<DayHours> perDay = [.. PerDay.Where(d => d.Day != day)];
        if (from != From || to != To)
        {
            perDay.Add(new DayHours(day, from, to));
            perDay.Sort((a, b) => a.Day.CompareTo(b.Day));
        }

        return this with { Days = on ? Days | flag : Days & ~flag, PerDay = perDay };
    }

    /// <summary>
    /// Every day on, with the hours most of the chosen days have (on a tie, the earliest such day in
    /// the week). This is what switching the editor from the week back to one dial does.
    /// </summary>
    public TimeWindowCondition SameEveryDay()
    {
        List<(TimeOnly From, TimeOnly To)> hours = [.. Enum.GetValues<DayOfWeek>().Where(IsOn).Select(HoursOn)];
        (TimeOnly from, TimeOnly to) = hours.Count == 0
            ? (From, To)
            : hours.GroupBy(h => h).OrderByDescending(g => g.Count()).First().Key;
        return this with { From = from, To = to, Days = WeekDays.Every, PerDay = [] };
    }

    public override DateTime? NextChange(ConditionContext context)
    {
        // The pattern repeats weekly, so yesterday (an overnight window still open) through a week
        // ahead holds the next boundary of every chosen day.
        DateTime local = context.Now.LocalTime;
        DateTime? next = null;
        for (int offset = -1; offset <= 7; offset++)
        {
            DateTime day = local.Date.AddDays(offset);
            if (!IsOn(day.DayOfWeek))
            {
                continue;
            }

            (DateTime start, DateTime end) = Window(day);
            foreach (DateTime boundary in (DateTime[])[start, end])
            {
                if (boundary > local && (next is null || boundary < next))
                {
                    next = boundary;
                }
            }
        }

        return next is { } at ? context.Now.ToUtc(at) : null;
    }

    protected override bool? Test(ConditionContext context)
    {
        DateTime local = context.Now.LocalTime;
        return Covers(local.Date, local) || Covers(local.Date.AddDays(-1), local);
    }

    // Lists compare by reference in a record; compare PerDay by its entries so an unchanged edit or a
    // file round trip still counts as the same condition (Setter.RunsDifferentlyThan relies on it).
    public bool Equals(TimeWindowCondition? other) =>
        other is not null && base.Equals(other) && From == other.From && To == other.To && Days == other.Days &&
        PerDay.SequenceEqual(other.PerDay);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), From, To, Days, PerDay.Count);

    private bool Covers(DateTime day, DateTime local)
    {
        if (!IsOn(day.DayOfWeek))
        {
            return false;
        }

        (DateTime start, DateTime end) = Window(day);
        return local >= start && local < end;
    }

    /// <summary>A day's window in local wall-clock time; one that ends at or before it starts ends the next day.</summary>
    private (DateTime Start, DateTime End) Window(DateTime day)
    {
        (TimeOnly from, TimeOnly to) = HoursOn(day.DayOfWeek);
        DateTime start = day + from.ToTimeSpan();
        DateTime end = day + to.ToTimeSpan();
        return (start, end > start ? end : end.AddDays(1));
    }
}

public enum SunPhase
{
    /// <summary>From sunrise to sunset.</summary>
    Day,

    /// <summary>From sunset to the next sunrise.</summary>
    Night
}

/// <summary>
/// Day or night at the user's saved place (<see cref="SensorSnapshot.Home"/>), with optional offsets
/// ("30 minutes before sunset" is SunsetOffsetMinutes = -30). Unknown until a place is set.
/// </summary>
public sealed record SunCondition(SunPhase Phase) : Condition
{
    public int SunriseOffsetMinutes { get; init; }

    public int SunsetOffsetMinutes { get; init; }

    public override Sensors Sensors => Sensors.Clock;

    /// <summary>
    /// As a stop condition ("until sunrise") it fires at the first sunrise (or sunset) after the
    /// setter started, not merely while it's day, so a setter started at noon runs until tomorrow.
    /// </summary>
    public DateTime? PhaseStartsAfter(SavedPlace home, DateTime afterUtc, TimeZoneInfo zone)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone));
        for (int offset = -1; offset <= 2; offset++)
        {
            var (rise, set) = Boundaries(home, day.AddDays(offset));
            DateTime? start = Phase == SunPhase.Day ? rise : set;
            if (start is { } at && at > afterUtc)
            {
                return at;
            }
        }

        return null;
    }

    public override DateTime? NextChange(ConditionContext context)
    {
        if (context.Now.Home is not { } home)
        {
            return null;
        }

        if (context.ActiveSinceUtc is { } since)
        {
            return PhaseStartsAfter(home, since, context.Now.TimeZone);
        }

        DateTime now = context.Now.UtcNow;
        var today = DateOnly.FromDateTime(context.Now.LocalTime);
        DateTime? next = null;
        for (int day = -1; day <= 2; day++)
        {
            var (rise, set) = Boundaries(home, today.AddDays(day));
            foreach (DateTime? boundary in (DateTime?[])[rise, set])
            {
                if (boundary is { } at && at > now && (next is null || at < next))
                {
                    next = at;
                }
            }
        }

        return next;
    }

    /// <summary>Sunrise and sunset (UTC, offsets applied) for a local date; null on polar days and nights.</summary>
    public (DateTime? Rise, DateTime? Set) Boundaries(SavedPlace home, DateOnly date)
    {
        SunTimes sun = SolarCalculator.Calculate(home.Latitude, home.Longitude, date);
        return (sun.SunriseUtc?.AddMinutes(SunriseOffsetMinutes), sun.SunsetUtc?.AddMinutes(SunsetOffsetMinutes));
    }

    protected override bool? Test(ConditionContext context)
    {
        if (context.Now.Home is not { } home)
        {
            return null;
        }

        DateTime now = context.Now.UtcNow;
        if (context.ActiveSinceUtc is { } since)
        {
            return PhaseStartsAfter(home, since, context.Now.TimeZone) is { } start ? now >= start : null;
        }

        var date = DateOnly.FromDateTime(context.Now.LocalTime);
        SunTimes sun = SolarCalculator.Calculate(home.Latitude, home.Longitude, date);
        bool isDay;
        if (sun.SunriseUtc is null || sun.SunsetUtc is null)
        {
            isDay = sun.AlwaysUp;
        }
        else
        {
            var (rise, set) = Boundaries(home, date);
            isDay = now >= rise && now < set;
        }

        return Phase == SunPhase.Day ? isDay : !isDay;
    }
}

/// <summary>Connected to a network with this name: the Wi-Fi SSID or the name Windows gives a wired network.</summary>
public sealed record NetworkCondition(string Network) : Condition
{
    public override Sensors Sensors => Sensors.Network;

    protected override bool? Test(ConditionContext context) =>
        string.IsNullOrWhiteSpace(Network) || context.Now.Networks is not { } networks
            ? null
            : networks.Contains(Network.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A device (headphones, a dock, a mouse) is connected, matched by the name Windows shows in
/// Settings > Bluetooth &amp; devices. Names survive moving a USB device to another port; ids don't.
/// </summary>
public sealed record DeviceCondition(string Device) : Condition
{
    public override Sensors Sensors => Sensors.Devices;

    protected override bool? Test(ConditionContext context) =>
        string.IsNullOrWhiteSpace(Device) || context.Now.Devices is not { } devices
            ? null
            : devices.Contains(Device.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>An app is running, by executable name ("blender" or "blender.exe").</summary>
public sealed record ProcessCondition(string Process) : Condition
{
    public override Sensors Sensors => Sensors.Processes;

    public string Normalized => ProcessNames.Normalize(Process);

    protected override bool? Test(ConditionContext context) =>
        string.IsNullOrWhiteSpace(Process) || context.Now.Processes is not { } running
            ? null
            : running.Contains(Normalized, StringComparer.OrdinalIgnoreCase);
}

public enum PowerCheck
{
    PluggedIn,
    OnBattery,
    BatteryBelow,     // under Percent
    BatteryAtLeast    // Percent or more
}

/// <summary>Power source or charge level. The percentage checks are unknown on a PC without a battery.</summary>
public sealed record PowerCondition(PowerCheck Check, int Percent) : Condition
{
    public const int DefaultPercent = 20;

    public override Sensors Sensors => Sensors.Power;

    protected override bool? Test(ConditionContext context) => (context.Now.Power, Check) switch
    {
        (null, _) => null,
        ({ } power, PowerCheck.PluggedIn) => power.PluggedIn,
        ({ } power, PowerCheck.OnBattery) => !power.PluggedIn,
        ({ BatteryPercent: int percent }, PowerCheck.BatteryBelow) => percent < Percent,
        ({ BatteryPercent: int percent }, PowerCheck.BatteryAtLeast) => percent >= Percent,
        _ => null,
    };
}

/// <summary>
/// Within RadiusMeters of a point; Place is the user's name for it ("Home"). Needs location turned
/// on for Set Sett, so the editor only offers it when Windows already allows location.
/// </summary>
public sealed record PlaceCondition(GeoPoint? Center, int RadiusMeters, string Place) : Condition
{
    public static readonly int[] RadiusChoices = [100, 250, 500, 1000, 5000];
    public const int DefaultRadius = 250;

    private const double EarthRadiusMeters = 6_371_000;

    // Nothing to compare against until a place is set, so no reason to run location (or ask for it).
    public override Sensors Sensors => Center is null ? Sensors.None : Sensors.Location;

    /// <summary>Great-circle distance (haversine); plenty accurate at the radii a place uses.</summary>
    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        double dLat = ToRadians(b.Latitude - a.Latitude);
        double dLon = ToRadians(b.Longitude - a.Longitude);
        double h = Math.Pow(Math.Sin(dLat / 2), 2) +
                   (Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2));
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    protected override bool? Test(ConditionContext context) =>
        Center is { } center && context.Now.Location is { } here
            ? DistanceMeters(center, here) <= RadiusMeters
            : null;

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}

/// <summary>
/// Stop condition: this many minutes after the setter started (a timer). A run started for another
/// length from the setter's tray icon menu (<see cref="ConditionContext.TimerEndsUtc"/>) ends then instead.
/// </summary>
public sealed record ElapsedCondition(int Minutes) : Condition
{
    public override Sensors Sensors => Sensors.Clock;

    public override bool CanStart => false;

    public DateTime? EndsAtUtc(ConditionContext context) =>
        context.ActiveSinceUtc is { } since ? context.TimerEndsUtc ?? since.AddMinutes(Minutes) : null;

    public override DateTime? NextChange(ConditionContext context) => EndsAtUtc(context);

    protected override bool? Test(ConditionContext context) =>
        EndsAtUtc(context) is { } end ? context.Now.UtcNow >= end : null;
}

/// <summary>Stop condition: at the first <see cref="At"/> after the setter started ("until 5:00 PM").</summary>
public sealed record ClockCondition(TimeOnly At) : Condition
{
    public override Sensors Sensors => Sensors.Clock;

    public override bool CanStart => false;

    public DateTime? EndsAtUtc(ConditionContext context)
    {
        if (context.ActiveSinceUtc is not { } since)
        {
            return null;
        }

        DateTime startLocal = TimeZoneInfo.ConvertTimeFromUtc(since, context.Now.TimeZone);
        DateTime candidate = startLocal.Date + At.ToTimeSpan();
        if (candidate <= startLocal)
        {
            candidate = candidate.AddDays(1);
        }

        return context.Now.ToUtc(candidate);
    }

    public override DateTime? NextChange(ConditionContext context) => EndsAtUtc(context);

    protected override bool? Test(ConditionContext context) =>
        EndsAtUtc(context) is { } end ? context.Now.UtcNow >= end : null;
}

/// <summary>
/// Stop condition: the whole PC's CPU has stayed under <see cref="Percent"/> for <see cref="Minutes"/>
/// since the setter started, e.g. "keep awake until the render finishes". The CPU is sampled only
/// while a setter with this condition is running.
/// </summary>
public sealed record CpuQuietCondition(int Percent, int Minutes) : Condition
{
    /// <summary>How late the first sample may be and still count as covering the start of the window.</summary>
    public static readonly TimeSpan SampleSlack = TimeSpan.FromSeconds(30);

    public override Sensors Sensors => Sensors.Cpu;

    public override bool CanStart => false;

    protected override bool? Test(ConditionContext context)
    {
        if (context.ActiveSinceUtc is not { } since)
        {
            return null;
        }

        DateTime now = context.Now.UtcNow;
        var window = TimeSpan.FromMinutes(Math.Max(1, Minutes));
        if (now - since < window)
        {
            return false;
        }

        if (context.Now.Cpu is not { Count: > 0 } samples)
        {
            return null;
        }

        DateTime from = now - window;

        // The sampler starts with the setter; until it has covered a full window, keep holding.
        if (samples[0].AtUtc > from + SampleSlack)
        {
            return false;
        }

        bool any = false;
        foreach (CpuSample sample in samples)
        {
            if (sample.AtUtc < from)
            {
                continue;
            }

            any = true;
            if (sample.Percent >= Percent)
            {
                return false;
            }
        }

        // No sample inside the window means the sampler stalled; don't guess.
        return any ? true : null;
    }
}
