using SetSett.Model;
using SetSett.Solar;

namespace SetSett.Tests;

[TestClass]
public sealed class ConditionTests
{
    private static readonly SavedPlace Milwaukee = new("Milwaukee", 43.0389, -87.9065);

    [TestMethod]
    public void TimeWindow_OvernightBelongsToTheDayItStarts()
    {
        var weekdayNights = new TimeWindowCondition(new TimeOnly(22, 0), new TimeOnly(6, 0), WeekDays.Weekdays);

        // Friday 23:00 is in; Saturday 03:00 is still Friday's window; Saturday 23:00 is out.
        Assert.IsTrue(weekdayNights.Evaluate(new(At.Local(2026, 10, 30, 23))));
        Assert.IsTrue(weekdayNights.Evaluate(new(At.Local(2026, 10, 31, 3))));
        Assert.IsFalse(weekdayNights.Evaluate(new(At.Local(2026, 10, 31, 23))));
    }

    [TestMethod]
    public void TimeWindow_NextChangeIsTheNextBoundary()
    {
        var window = new TimeWindowCondition(new TimeOnly(22, 0), new TimeOnly(7, 0), WeekDays.Every);
        SensorSnapshot now = At.Local(2026, 10, 31, 12);

        Assert.AreEqual(At.Local(2026, 10, 31, 22).UtcNow, window.NextChange(new(now)));
    }

    [TestMethod]
    public void TimeWindow_UsesEachDaysOwnHours()
    {
        // Weekdays 9 to 5, but Friday 9 to 1; Saturday 10 PM to 2 AM; Sunday off.
        TimeWindowCondition week = new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays | WeekDays.Saturday)
            .WithDay(DayOfWeek.Friday, true, new TimeOnly(9, 0), new TimeOnly(13, 0))
            .WithDay(DayOfWeek.Saturday, true, new TimeOnly(22, 0), new TimeOnly(2, 0));

        Assert.IsTrue(week.Evaluate(new(At.Local(2026, 10, 29, 16))), "Thursday 4 PM");
        Assert.IsFalse(week.Evaluate(new(At.Local(2026, 10, 30, 14))), "Friday 2 PM, after Friday's 1 PM");
        Assert.IsFalse(week.Evaluate(new(At.Local(2026, 10, 31, 12))), "Saturday noon");
        Assert.IsTrue(week.Evaluate(new(At.Local(2026, 10, 31, 23))), "Saturday 11 PM");
        Assert.IsTrue(week.Evaluate(new(At.Local(2026, 11, 1, 0, 30))), "Sunday 12:30 AM is still Saturday night");
        Assert.IsFalse(week.Evaluate(new(At.Local(2026, 11, 1, 10))), "Sunday is off");
    }

    [TestMethod]
    public void TimeWindow_NextChangeSkipsDaysThatAreOff()
    {
        var weekdays = new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays);
        SensorSnapshot fridayEvening = At.Local(2026, 10, 30, 18);

        Assert.AreEqual(At.Local(2026, 11, 2, 9).UtcNow, weekdays.NextChange(new(fridayEvening)), "Monday 9 AM");
        Assert.IsNull((weekdays with { Days = WeekDays.None }).NextChange(new(fridayEvening)));
    }

    [TestMethod]
    public void TimeWindow_WithDayKeepsOnlyHoursThatDiffer()
    {
        var nights = new TimeWindowCondition(new TimeOnly(22, 0), new TimeOnly(7, 0), WeekDays.Every);
        TimeWindowCondition friday = nights.WithDay(DayOfWeek.Friday, true, new TimeOnly(23, 0), new TimeOnly(9, 0));

        Assert.IsFalse(friday.IsSameEveryDay);
        Assert.AreEqual((new TimeOnly(23, 0), new TimeOnly(9, 0)), friday.HoursOn(DayOfWeek.Friday));
        Assert.AreEqual(nights, friday.WithDay(DayOfWeek.Friday, true, new TimeOnly(22, 0), new TimeOnly(7, 0)), "back to the usual hours");

        // A day turned off keeps its hours for when it comes back on.
        TimeWindowCondition off = friday.WithDay(DayOfWeek.Friday, false, new TimeOnly(23, 0), new TimeOnly(9, 0));
        Assert.IsFalse(off.IsOn(DayOfWeek.Friday));
        Assert.AreEqual((new TimeOnly(23, 0), new TimeOnly(9, 0)), off.HoursOn(DayOfWeek.Friday));
        Assert.AreNotEqual(friday, off);
    }

    [TestMethod]
    public void TimeWindow_SameEveryDayTakesTheMostCommonHours()
    {
        TimeWindowCondition week = new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays | WeekDays.Saturday)
            .WithDay(DayOfWeek.Saturday, true, new TimeOnly(10, 0), new TimeOnly(14, 0));

        TimeWindowCondition every = week.SameEveryDay();

        Assert.IsTrue(every.IsSameEveryDay);
        Assert.AreEqual(new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Every), every);
    }

    [TestMethod]
    public void SolarCalculator_MilwaukeeSolstice()
    {
        SunTimes sun = SolarCalculator.Calculate(Milwaukee.Latitude, Milwaukee.Longitude, new DateOnly(2026, 6, 21));

        // Published times: sunrise 5:15 AM CDT (10:15 UTC), sunset 8:37 PM CDT (01:37 UTC next day).
        AssertNear(new DateTime(2026, 6, 21, 10, 15, 0, DateTimeKind.Utc), sun.SunriseUtc!.Value);
        AssertNear(new DateTime(2026, 6, 22, 1, 37, 0, DateTimeKind.Utc), sun.SunsetUtc!.Value);
    }

    [TestMethod]
    public void SolarCalculator_PolarNightAndMidnightSun()
    {
        SunTimes winter = SolarCalculator.Calculate(78.2, 15.6, new DateOnly(2026, 12, 21)); // Longyearbyen
        SunTimes summer = SolarCalculator.Calculate(78.2, 15.6, new DateOnly(2026, 6, 21));

        Assert.IsNull(winter.SunriseUtc);
        Assert.IsFalse(winter.AlwaysUp);
        Assert.IsTrue(summer.AlwaysUp);
    }

    [TestMethod]
    public void Sun_NightAfterSunsetWithOffset()
    {
        var night = new SunCondition(SunPhase.Night) { SunsetOffsetMinutes = -30 };
        SensorSnapshot evening = At.Local(2026, 10, 31, 17, 30) with { Home = Milwaukee }; // sunset ~5:41 PM CDT

        Assert.IsTrue(night.Evaluate(new(evening)));
        Assert.IsFalse(night.Evaluate(new(At.Local(2026, 10, 31, 12) with { Home = Milwaukee })));
        Assert.IsNull(night.Evaluate(new(At.Local(2026, 10, 31, 12))), "unknown without a place");
    }

    [TestMethod]
    public void Sun_NextChangeIsTodaysSunset()
    {
        var night = new SunCondition(SunPhase.Night);
        SensorSnapshot noon = At.Local(2026, 10, 31, 12) with { Home = Milwaukee };

        DateTime next = night.NextChange(new(noon))!.Value;

        AssertNear(At.Local(2026, 10, 31, 17, 41).UtcNow, next);
    }

    [TestMethod]
    public void SunUntil_MeansTheNextSunriseAfterStarting()
    {
        var untilSunrise = new SunCondition(SunPhase.Day);
        SensorSnapshot noon = At.Local(2026, 10, 31, 12) with { Home = Milwaukee };
        var context = new ConditionContext(noon, noon.UtcNow);

        // Started at noon (daytime): it must not stop at once, but at tomorrow's sunrise.
        Assert.IsFalse(untilSunrise.Evaluate(context));
        DateTime next = untilSunrise.NextChange(context)!.Value;
        AssertNear(At.Local(2026, 11, 1, 6, 25).UtcNow, next); // 6:25 AM CST after DST ends
        Assert.IsTrue(untilSunrise.Evaluate(context with { Now = noon with { UtcNow = next.AddMinutes(1) } }));
    }

    [TestMethod]
    public void ClockUntil_EndsAtTheFirstOccurrenceAfterStart()
    {
        var until = new ClockCondition(new TimeOnly(9, 0));
        SensorSnapshot start = At.Local(2026, 10, 31, 22);
        var context = new ConditionContext(start, start.UtcNow);

        // Daylight saving ends overnight, so 9:00 the next morning is 12 real hours after 22:00.
        Assert.AreEqual(At.Local(2026, 11, 1, 9).UtcNow, until.NextChange(context));
        Assert.IsFalse(until.Evaluate(context with { Now = start.Plus(TimeSpan.FromHours(11)) }));
        Assert.IsTrue(until.Evaluate(context with { Now = start.Plus(TimeSpan.FromHours(12)) }));
    }

    [TestMethod]
    public void CpuQuiet_NeedsAFullQuietWindowAfterStarting()
    {
        var quiet = new CpuQuietCondition(10, 5);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);
        List<CpuSample> samples = [];
        for (int s = 10; s <= 600; s += 10)
        {
            // Busy for the first two minutes, then idle.
            samples.Add(new CpuSample(start.UtcNow.AddSeconds(s), s <= 120 ? 95 : 3));
        }

        bool? QuietAfter(int minutes) => quiet.Evaluate(new ConditionContext(
            start.Plus(TimeSpan.FromMinutes(minutes)) with { Cpu = samples.Where(x => x.AtUtc <= start.UtcNow.AddMinutes(minutes)).ToList() },
            start.UtcNow));

        Assert.IsFalse(QuietAfter(4), "not a full window yet");
        Assert.IsFalse(QuietAfter(6), "busy samples are still inside the window");
        Assert.IsTrue(QuietAfter(8), "quiet for the last five minutes");
    }

    [TestMethod]
    public void Process_MatchesNormalizedNames()
    {
        var blender = new ProcessCondition("Blender.EXE");
        SensorSnapshot now = At.Local(2026, 10, 31, 12) with { Processes = ["blender"] };

        Assert.IsTrue(blender.Evaluate(new(now)));
        Assert.IsFalse((blender with { Not = true }).Evaluate(new(now)));
        Assert.IsNull(blender.Evaluate(new(At.Local(2026, 10, 31, 12))));
    }

    private static void AssertNear(DateTime expected, DateTime actual, int minutes = 5) =>
        Assert.IsLessThanOrEqualTo(minutes, Math.Abs((actual - expected).TotalMinutes), $"expected ~{expected:u}, got {actual:u}");
}
