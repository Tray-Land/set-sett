using System.Globalization;
using SetSett.Model;
using SetSett.Text;

namespace SetSett.Tests;

/// <summary>The edit rules behind the dial and the week view (ported from the Controls-Experiment lab).</summary>
[TestClass]
public sealed class TimeRangeMathTests
{
    private static readonly TimeRangeConstraints C = TimeRangeConstraints.Default;

    private static TimeSpan H(double hours) => TimeSpan.FromHours(hours);

    private static TimeRangeValue Range(double start, double end) => new(H(start), H(end));

    [TestMethod]
    [DataRow(9.1, 9.0)]
    [DataRow(9.13, 9.25)]
    [DataRow(23.95, 24.0)]
    public void Snap_RoundsToNearestStep(double hours, double expected) =>
        Assert.AreEqual(H(expected), TimeRangeMath.Snap(H(hours), C.Step));

    [TestMethod]
    public void Wrap_MapsNegativeAndLargeOffsetsIntoADay()
    {
        Assert.AreEqual(H(23), TimeRangeMath.WrapToDay(H(-1)));
        Assert.AreEqual(H(2), TimeRangeMath.WrapToDay(H(26)));
        Assert.AreEqual(TimeSpan.Zero, TimeRangeMath.WrapToDay(H(24)));
    }

    [TestMethod]
    public void ShortestDelta_GoesTheShortWayRound()
    {
        Assert.AreEqual(H(2), TimeRangeMath.ShortestDelta(H(23), H(1)));
        Assert.AreEqual(H(-2), TimeRangeMath.ShortestDelta(H(1), H(23)));
        Assert.AreEqual(H(5), TimeRangeMath.ShortestDelta(H(1), H(6)));
    }

    [TestMethod]
    public void Normalize_PointWrapsAStartThatSnapsToMidnight() =>
        Assert.AreEqual(new TimeRangeValue(TimeSpan.Zero), TimeRangeMath.Normalize(new(H(23.99)), C));

    [TestMethod]
    public void Normalize_KeepsAnOvernightEndPast24h() =>
        Assert.AreEqual(Range(22, 26), TimeRangeMath.Normalize(Range(22, 26), C));

    [TestMethod]
    public void Normalize_ReadsAnEndBeforeTheStartAsTomorrow() =>
        Assert.AreEqual(Range(22, 26), TimeRangeMath.Normalize(Range(22, 2), C));

    [TestMethod]
    public void Normalize_AllowsEndingExactlyAtMidnight() =>
        Assert.AreEqual(Range(9, 24), TimeRangeMath.Normalize(Range(9, 24), C));

    [TestMethod]
    public void Normalize_GivesAnEmptyRangeTheMinimumLength() =>
        Assert.AreEqual(Range(9, 9.25), TimeRangeMath.Normalize(Range(9, 9), C));

    [TestMethod]
    public void Normalize_LimitsTheLength() =>
        Assert.AreEqual(Range(9, 17), TimeRangeMath.Normalize(Range(9, 22), C with { MaxDuration = H(8) }));

    [TestMethod]
    public void Sanitize_RepairsNonsense()
    {
        TimeRangeConstraints c = new TimeRangeConstraints(TimeSpan.Zero, H(30), H(1)).Sanitize();
        Assert.IsGreaterThan(TimeSpan.Zero, c.Step);
        Assert.IsLessThanOrEqualTo(TimeRangeMath.Day, c.MinDuration);
        Assert.IsGreaterThanOrEqualTo(c.MinDuration, c.MaxDuration);
    }

    [TestMethod]
    public void MoveEnd_CanCrossMidnight() =>
        Assert.AreEqual(Range(22, 25), TimeRangeMath.MoveEnd(Range(22, 23), H(2), C));

    [TestMethod]
    public void MoveEnd_StopsAtTheMinimumLength() =>
        Assert.AreEqual(Range(9, 9.25), TimeRangeMath.MoveEnd(Range(9, 10), H(-5), C));

    [TestMethod]
    public void MoveEnd_StopsAtAFullDay() =>
        Assert.AreEqual(Range(9, 33), TimeRangeMath.MoveEnd(Range(9, 10), H(40), C));

    [TestMethod]
    public void MoveEnd_OnAPointDoesNothing() =>
        Assert.AreEqual(new TimeRangeValue(H(9)), TimeRangeMath.MoveEnd(new(H(9)), H(1), C));

    [TestMethod]
    public void MoveStart_WithoutWrapStopsAtMidnight() =>
        Assert.AreEqual(Range(0, 10), TimeRangeMath.MoveStart(Range(1, 10), H(-5), C, wrap: false));

    [TestMethod]
    public void MoveStart_WithoutWrapStopsAtTheMinimumLength() =>
        Assert.AreEqual(Range(9.75, 10), TimeRangeMath.MoveStart(Range(9, 10), H(5), C, wrap: false));

    [TestMethod]
    public void MoveStart_WithoutWrapKeepsAnOvernightEnd() =>
        Assert.AreEqual(Range(20, 26), TimeRangeMath.MoveStart(Range(22, 26), H(-2), C, wrap: false));

    [TestMethod]
    public void MoveStart_OnADialKeepsTheEndClockTimeWhenCrossingMidnight() =>
        // 23:00 -> 01:00 next day; pulling the start 1h later leaves 00:00 -> 01:00, ending at the same clock time.
        Assert.AreEqual(Range(0, 1), TimeRangeMath.MoveStart(Range(23, 25), H(1), C, wrap: true));

    [TestMethod]
    public void MoveStart_OnADialCanWrapBackwardsThroughMidnight() =>
        Assert.AreEqual(Range(23, 29), TimeRangeMath.MoveStart(Range(1, 5), H(-2), C, wrap: true));

    [TestMethod]
    public void MoveStart_OnAPointWrapsOrClamps()
    {
        Assert.AreEqual(new TimeRangeValue(H(23)), TimeRangeMath.MoveStart(new(H(1)), H(-2), C, wrap: true));
        Assert.AreEqual(new TimeRangeValue(TimeSpan.Zero), TimeRangeMath.MoveStart(new(H(1)), H(-2), C, wrap: false));
    }

    [TestMethod]
    public void MoveRange_KeepsLengthAndWrapsOnADial() =>
        Assert.AreEqual(Range(1, 2), TimeRangeMath.MoveRange(Range(23, 24), H(2), C, wrap: true));

    [TestMethod]
    public void MoveRange_WithoutWrapStopsAtTheLastStartButMayRunOvernight() =>
        Assert.AreEqual(Range(23.75, 24.75), TimeRangeMath.MoveRange(Range(22, 23), H(5), C, wrap: false));

    [TestMethod]
    public void ToRange_AddsADefaultLength() =>
        Assert.AreEqual(Range(9, 10), TimeRangeMath.ToRange(new(H(9)), C));

    [TestMethod]
    public void ResolveEnd_PicksTheNextOccurrence()
    {
        Assert.AreEqual(H(17), TimeRangeMath.ResolveEnd(H(9), H(17)));
        Assert.AreEqual(H(26), TimeRangeMath.ResolveEnd(H(22), H(2)));
        Assert.AreEqual(H(24), TimeRangeMath.ResolveEnd(H(9), TimeSpan.Zero));
        Assert.IsNull(TimeRangeMath.ResolveEnd(H(9), H(9)));
    }

    [TestMethod]
    public void Value_DescribesOvernightRanges()
    {
        TimeRangeValue overnight = Range(22, 26);
        Assert.IsTrue(overnight.IsRange);
        Assert.IsTrue(overnight.SpansMidnight);
        Assert.AreEqual(H(4), overnight.Duration);
        Assert.AreEqual(H(2), overnight.EndTimeOfDay);
        Assert.IsFalse(Range(9, 24).SpansMidnight);
        Assert.IsFalse(new TimeRangeValue(H(9)).IsRange);
    }

    [TestMethod]
    public void Between_ReadsFromAndToTheWayTheConditionDoes()
    {
        Assert.AreEqual(Range(9, 17), TimeRangeValue.Between(new TimeOnly(9, 0), new TimeOnly(17, 0)));
        Assert.AreEqual(Range(22, 31), TimeRangeValue.Between(new TimeOnly(22, 0), new TimeOnly(7, 0)));
        Assert.AreEqual(Range(9, 24), TimeRangeValue.Between(new TimeOnly(9, 0), new TimeOnly(0, 0)));
        Assert.AreEqual(Range(6, 30), TimeRangeValue.Between(new TimeOnly(6, 0), new TimeOnly(6, 0)), "From == To is a whole day");
    }

    [TestMethod]
    [DataRow(9.0, 17.0)]
    [DataRow(22.0, 7.0)]
    [DataRow(9.0, 0.0)]
    [DataRow(6.5, 6.5)]
    public void ToWindow_RoundTripsBetween(double from, double to)
    {
        var window = (TimeOnly.FromTimeSpan(H(from)), TimeOnly.FromTimeSpan(H(to)));
        Assert.AreEqual(window, TimeRangeValue.Between(window.Item1, window.Item2).ToWindow());
    }
}

[TestClass]
public sealed class TimeRangeFormatTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static TimeSpan H(double hours) => TimeSpan.FromHours(hours);

    [TestMethod]
    public void FormatTime_Respects12And24Hour()
    {
        Assert.AreEqual("9:30 AM", TimeRangeFormat.FormatTime(H(9.5), Culture, false));
        Assert.AreEqual("9:30 PM", TimeRangeFormat.FormatTime(H(21.5), Culture, false));
        Assert.AreEqual("21:30", TimeRangeFormat.FormatTime(H(21.5), Culture, true));
        Assert.AreEqual("12:00 AM", TimeRangeFormat.FormatTime(H(24), Culture, false));
    }

    [TestMethod]
    public void FormatTime_FollowsTheChosenClockInAnyCulture()
    {
        var german = CultureInfo.GetCultureInfo("de-DE");
        var french = CultureInfo.GetCultureInfo("fr-FR");
        var american = CultureInfo.GetCultureInfo("en-US");

        // A 24-hour culture whose user picked the 12-hour clock still gets readable AM/PM, and the reverse.
        Assert.AreEqual("9:30 PM", TimeRangeFormat.FormatTime(H(21.5), german, false));
        Assert.AreEqual("9:30 PM", TimeRangeFormat.FormatTime(H(21.5), french, false));
        Assert.AreEqual("6 PM", TimeRangeFormat.FormatHour(18, french, false));
        Assert.AreEqual("21:30", TimeRangeFormat.FormatTime(H(21.5), american, true));
        Assert.AreEqual("18:00", TimeRangeFormat.FormatHour(18, american, true));
        Assert.IsTrue(TimeRangeFormat.Is24Hour(german));
        Assert.IsFalse(TimeRangeFormat.Is24Hour(american));
    }

    [TestMethod]
    public void FormatHour_LabelsAxisHours()
    {
        Assert.AreEqual("12 AM", TimeRangeFormat.FormatHour(0, Culture, false));
        Assert.AreEqual("6 PM", TimeRangeFormat.FormatHour(18, Culture, false));
        Assert.AreEqual("06:00", TimeRangeFormat.FormatHour(6, Culture, true));
        Assert.AreEqual("12 AM", TimeRangeFormat.FormatHour(24, Culture, false));
    }

    [TestMethod]
    [DataRow(0.75, "45m")]
    [DataRow(8.0, "8h")]
    [DataRow(1.5, "1h 30m")]
    [DataRow(24.0, "24h")]
    public void FormatDuration_IsCompact(double hours, string expected) =>
        Assert.AreEqual(expected, TimeRangeFormat.FormatDuration(H(hours)));

    [TestMethod]
    public void FormatRange_CoversPointRangeOvernightAndAllDay()
    {
        Assert.AreEqual("9:00 AM", TimeRangeFormat.FormatRange(new(H(9)), Culture, false));
        Assert.AreEqual("9:00 AM – 5:00 PM", TimeRangeFormat.FormatRange(new(H(9), H(17)), Culture, false));
        Assert.AreEqual("10:00 PM – 2:00 AM +1", TimeRangeFormat.FormatRange(new(H(22), H(26)), Culture, false));
        Assert.AreEqual("9:00 AM – 12:00 AM", TimeRangeFormat.FormatRange(new(H(9), H(24)), Culture, false));
        Assert.AreEqual("All day", TimeRangeFormat.FormatRange(new(TimeSpan.Zero, H(24)), Culture, false));
    }

    [TestMethod]
    [DataRow("9", 9.0)]
    [DataRow("09", 9.0)]
    [DataRow("930", 9.5)]
    [DataRow("0930", 9.5)]
    [DataRow("1230", 12.5)]
    [DataRow("9:30", 9.5)]
    [DataRow("9.30", 9.5)]
    [DataRow("21:15", 21.25)]
    [DataRow("9pm", 21.0)]
    [DataRow("9 PM", 21.0)]
    [DataRow("9:30 p.m.", 21.5)]
    [DataRow("12am", 0.0)]
    [DataRow("12pm", 12.0)]
    [DataRow("12:15 AM", 0.25)]
    [DataRow("24:00", 0.0)]
    [DataRow("  7a ", 7.0)]
    public void TryParseTime_AcceptsCommonSpellings(string text, double expectedHours)
    {
        Assert.IsTrue(TimeRangeFormat.TryParseTime(text, out TimeSpan parsed));
        Assert.AreEqual(H(expectedHours), parsed);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("abc")]
    [DataRow("13pm")]
    [DataRow("0pm")]
    [DataRow("9:75")]
    [DataRow("25:00")]
    [DataRow("24:30")]
    [DataRow("9:3")]
    [DataRow("9:30:15")]
    [DataRow(null)]
    public void TryParseTime_RejectsNonsense(string? text) =>
        Assert.IsFalse(TimeRangeFormat.TryParseTime(text, out _));
}
