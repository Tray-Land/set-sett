namespace SetSett.Model;

/// <summary>
/// A time of day or a span of one, as offsets from midnight, as the editor's dial and week view edit it.
/// <see cref="End"/> is measured from the same midnight as <see cref="Start"/>, so an overnight span runs past 24h
/// (22:00-02:00 is 22h..26h). A null <see cref="End"/> is a single point in time.
/// </summary>
/// <remarks>Ported from the Controls-Experiment lab along with <see cref="TimeRangeMath"/>.</remarks>
public readonly record struct TimeRangeValue(TimeSpan Start, TimeSpan? End = null)
{
    public bool IsRange => End.HasValue;

    public TimeSpan Duration => End is { } end ? end - Start : TimeSpan.Zero;

    /// <summary>True when the end falls on the following day. Ending exactly at 24:00 is the end of this day.</summary>
    public bool SpansMidnight => End is { } end && end > TimeRangeMath.Day;

    public TimeSpan? EndTimeOfDay => End is { } end ? TimeRangeMath.WrapToDay(end) : null;

    /// <summary>
    /// The span a <see cref="TimeWindowCondition"/> means by From and To: an end at or before the start runs into the
    /// next day, and From == To is a whole 24 hours.
    /// </summary>
    public static TimeRangeValue Between(TimeOnly from, TimeOnly to)
    {
        TimeSpan start = from.ToTimeSpan();
        TimeSpan end = to.ToTimeSpan();
        return new(start, end > start ? end : end + TimeRangeMath.Day);
    }

    /// <summary>The From and To of <see cref="Between"/> again. A point has no end, so it reads as a whole day from its start.</summary>
    public (TimeOnly From, TimeOnly To) ToWindow()
    {
        var from = TimeOnly.FromTimeSpan(TimeRangeMath.WrapToDay(Start));
        return (from, End is { } end ? TimeOnly.FromTimeSpan(TimeRangeMath.WrapToDay(end)) : from);
    }
}

/// <summary>Snapping and length limits applied to every edit. All three are bindable on the picker.</summary>
public readonly record struct TimeRangeConstraints(TimeSpan Step, TimeSpan MinDuration, TimeSpan MaxDuration)
{
    public static TimeRangeConstraints Default { get; } =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), TimeRangeMath.Day);

    /// <summary>Repairs nonsense (zero step, min above max, more than a day) instead of throwing; it arrives from bindings.</summary>
    public TimeRangeConstraints Sanitize()
    {
        var step = Step > TimeSpan.Zero && Step <= TimeRangeMath.Day ? Step : Default.Step;
        var min = MinDuration > step ? MinDuration : step;
        if (min > TimeRangeMath.Day)
        {
            min = TimeRangeMath.Day;
        }

        var max = MaxDuration < TimeRangeMath.Day ? MaxDuration : TimeRangeMath.Day;
        return new(step, min, max < min ? min : max);
    }
}

/// <summary>
/// Pure edit rules for <see cref="TimeRangeValue"/>. Pointer, touch and keyboard all reduce to these moves, so the three
/// input paths cannot disagree about snapping or limits.
/// </summary>
public static class TimeRangeMath
{
    public static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static readonly TimeSpan HalfDay = TimeSpan.FromHours(12);
    private static readonly TimeSpan DefaultRangeLength = TimeSpan.FromHours(1);

    public static TimeSpan Snap(TimeSpan value, TimeSpan step)
    {
        if (step <= TimeSpan.Zero)
        {
            return value;
        }

        var steps = Math.Round((double)value.Ticks / step.Ticks, MidpointRounding.AwayFromZero);
        return TimeSpan.FromTicks((long)steps * step.Ticks);
    }

    /// <summary>Maps any offset onto [0, 24h).</summary>
    public static TimeSpan WrapToDay(TimeSpan value)
    {
        var ticks = value.Ticks % Day.Ticks;
        return TimeSpan.FromTicks(ticks < 0 ? ticks + Day.Ticks : ticks);
    }

    /// <summary>The signed move from one clock time to another that goes the short way round the dial, in (-12h, 12h].</summary>
    public static TimeSpan ShortestDelta(TimeSpan from, TimeSpan to)
    {
        var delta = WrapToDay(to - from);
        return delta > HalfDay ? delta - Day : delta;
    }

    /// <summary>
    /// Snaps and repairs a raw value: start into [0, 24h), end after the start (an end at or before it means "the next
    /// time that clock time comes round"), and the length within the constraints.
    /// </summary>
    public static TimeRangeValue Normalize(TimeRangeValue value, TimeRangeConstraints constraints)
    {
        var c = constraints.Sanitize();
        var snappedStart = Snap(value.Start, c.Step);
        var start = WrapToDay(snappedStart);
        if (value.End is not { } end)
        {
            return new(start);
        }

        var length = end > value.Start ? end - value.Start : WrapToDay(end - value.Start);
        var duration = Clamp(Snap(value.Start + length, c.Step) - snappedStart, c.MinDuration, c.MaxDuration);
        return new(start, start + duration);
    }

    /// <summary>
    /// Moves the start edge by <paramref name="delta"/>. The end keeps its place, so the length changes. Without
    /// <paramref name="wrap"/> the start stops at midnight; with it (a dial) the start swings round the clock and only the
    /// length is limited.
    /// </summary>
    public static TimeRangeValue MoveStart(TimeRangeValue value, TimeSpan delta, TimeRangeConstraints constraints, bool wrap)
    {
        var c = constraints.Sanitize();
        var v = Normalize(value, c);
        var moved = Snap(v.Start + delta, c.Step);
        var latest = Day - c.Step;

        if (v.End is not { } end)
        {
            return new(wrap ? WrapToDay(moved) : Clamp(moved, TimeSpan.Zero, latest));
        }

        if (wrap)
        {
            var duration = Clamp(end - moved, c.MinDuration, c.MaxDuration);
            var start = WrapToDay(end - duration);
            return new(start, start + duration);
        }

        var earliest = end - c.MaxDuration > TimeSpan.Zero ? end - c.MaxDuration : TimeSpan.Zero;
        var limit = end - c.MinDuration < latest ? end - c.MinDuration : latest;
        return new(Clamp(moved, earliest, limit), end);
    }

    /// <summary>Moves the end edge by <paramref name="delta"/>, possibly past midnight. A point has no end to move.</summary>
    public static TimeRangeValue MoveEnd(TimeRangeValue value, TimeSpan delta, TimeRangeConstraints constraints)
    {
        var c = constraints.Sanitize();
        var v = Normalize(value, c);
        if (v.End is not { } end)
        {
            return v;
        }

        var duration = Clamp(Snap(end + delta, c.Step) - v.Start, c.MinDuration, c.MaxDuration);
        return new(v.Start, v.Start + duration);
    }

    /// <summary>Slides the whole range, keeping its length. Without <paramref name="wrap"/> the start stops at midnight.</summary>
    public static TimeRangeValue MoveRange(TimeRangeValue value, TimeSpan delta, TimeRangeConstraints constraints, bool wrap)
    {
        var c = constraints.Sanitize();
        var v = Normalize(value, c);
        var moved = Snap(v.Start + delta, c.Step);
        var start = wrap ? WrapToDay(moved) : Clamp(moved, TimeSpan.Zero, Day - c.Step);
        return v.IsRange ? new(start, start + v.Duration) : new(start);
    }

    /// <summary>Turns a point into a range of a sensible default length starting at the point.</summary>
    public static TimeRangeValue ToRange(TimeRangeValue value, TimeRangeConstraints constraints)
    {
        var c = constraints.Sanitize();
        var v = Normalize(value, c);
        var length = Clamp(Snap(DefaultRangeLength, c.Step), c.MinDuration, c.MaxDuration);
        return new(v.Start, v.Start + length);
    }

    /// <summary>
    /// The first moment after <paramref name="start"/> showing <paramref name="timeOfDay"/> on the clock, which is how a
    /// typed end time is read ("5pm" after 9am is the same day, "2am" after 10pm is tomorrow). Null when it is the start
    /// itself, since that cannot say whether the range is empty or a whole day.
    /// </summary>
    public static TimeSpan? ResolveEnd(TimeSpan start, TimeSpan timeOfDay)
    {
        var length = WrapToDay(timeOfDay - start);
        return length == TimeSpan.Zero ? null : start + length;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
