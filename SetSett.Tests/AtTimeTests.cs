using SetSett.Engine;
using SetSett.Model;
using SetSett.Storage;
using SetSett.Text;

namespace SetSett.Tests;

/// <summary>"At a time": a Time of day condition that fires as the clock crosses it, instead of holding inside a span.</summary>
[TestClass]
public sealed class AtTimeTests
{
    private static readonly ChoiceValue Dark = new(SettingIds.Dark);
    private static readonly ChoiceValue Light = new(SettingIds.Light);

    private static TimeWindowCondition NineAm(WeekDays days = WeekDays.Every) =>
        new(new TimeOnly(9, 0), new TimeOnly(17, 0), days) { AtTime = true };

    private static Setter DarkAtNine(params Condition[] until) => Setter.Create("Dark at nine", 100) with
    {
        Start = StartMode.Automatic,
        Changes = [new SettingChange(SettingIds.AppsTheme, Dark)],
        When = [NineAm()],
        Until = [.. until],
    };

    // 2026-11-03 is a Tuesday.
    private static IReadOnlyDictionary<string, DateTime> Since(Setter setter, SensorSnapshot since) =>
        new Dictionary<string, DateTime> { [setter.Id] = since.UtcNow };

    [TestMethod]
    public void Fires_WhenTheClockCrossesTheTime()
    {
        Setter setter = DarkAtNine(new ElapsedCondition(60));
        SensorSnapshot before = At.Local(2026, 11, 3, 8, 59);

        var (early, _) = SetterPolicy.Step([setter], RunState.Empty, before, Since(setter, before));
        Assert.IsEmpty(early);

        var (crossed, _) = SetterPolicy.Step([setter], RunState.Empty, At.Local(2026, 11, 3, 9, 0), Since(setter, before));
        Assert.IsTrue(crossed.Single().Start);
        Assert.IsFalse(crossed.Single().Once, "it has a way to stop, so it's a run");
    }

    [TestMethod]
    public void DoesNotFire_ForADayThatIsOff_OrAfterTheGrace()
    {
        Setter weekdays = DarkAtNine() with { When = [NineAm(WeekDays.Weekdays)] };
        SensorSnapshot fridayNight = At.Local(2026, 11, 6, 20);
        SensorSnapshot mondayMorning = At.Local(2026, 11, 9, 8, 59);
        var (saturday, _) = SetterPolicy.Step([weekdays], RunState.Empty, At.Local(2026, 11, 7, 9, 1), Since(weekdays, fridayNight));
        Assert.IsEmpty(saturday, "Saturday is off");

        // Woke from sleep long after the time: missed, not fired late.
        var (late, _) = SetterPolicy.Step([weekdays], RunState.Empty, At.Local(2026, 11, 9, 13), Since(weekdays, mondayMorning));
        Assert.IsEmpty(late);

        var (soon, _) = SetterPolicy.Step([weekdays], RunState.Empty, At.Local(2026, 11, 9, 9, 5), Since(weekdays, mondayMorning));
        Assert.IsTrue(soon.Single().Start, "a short sleep still fires");
    }

    [TestMethod]
    public void OtherStartConditions_OnlyHaveToHoldAtTheMoment()
    {
        Setter setter = DarkAtNine() with { When = [NineAm(), new NetworkCondition("Office")] };
        SensorSnapshot before = At.Local(2026, 11, 3, 8, 59) with { Networks = ["Office"] };

        var (away, _) = SetterPolicy.Step([setter], RunState.Empty, At.Local(2026, 11, 3, 9) with { Networks = ["Home"] }, Since(setter, before));
        Assert.IsEmpty(away);

        var (there, _) = SetterPolicy.Step([setter], RunState.Empty, At.Local(2026, 11, 3, 9) with { Networks = ["Office"] }, Since(setter, before));
        Assert.IsTrue(there.Single().Start);
    }

    [TestMethod]
    public void NoStopCondition_AppliesOnce()
    {
        Setter setter = DarkAtNine();
        Assert.IsTrue(setter.AppliesOnce);

        SensorSnapshot before = At.Local(2026, 11, 3, 8, 59);
        var (crossed, _) = SetterPolicy.Step([setter], RunState.Empty, At.Local(2026, 11, 3, 9), Since(setter, before));
        Assert.IsTrue(crossed.Single().Once);
    }

    [TestMethod]
    public void RunningSetter_IsNotStoppedWhenTheMomentPasses_AndIsNotLatchedWhenItEnds()
    {
        Setter setter = DarkAtNine(new ElapsedCondition(60));
        SensorSnapshot start = At.Local(2026, 11, 3, 9);
        RunState running = RunState.Empty.WithRun(new SetterRun(setter.Id, start.UtcNow));

        var (during, _) = SetterPolicy.Step([setter], running, start.Plus(TimeSpan.FromMinutes(30)), Since(setter, start));
        Assert.IsEmpty(during, "no 'conditions ended' stop");

        var (stop, state) = SetterPolicy.Step([setter], running, start.Plus(TimeSpan.FromMinutes(60)), Since(setter, start));
        Assert.AreEqual(StopReason.Until, stop.Single().Reason);
        Assert.IsFalse(state.IsLatched(setter.Id), "the next time starts it again");
    }

    [TestMethod]
    public void TimeComingRoundAgain_WhileRunning_RenewsTheRun()
    {
        Setter setter = DarkAtNine(new ElapsedCondition(60 * 30));
        SensorSnapshot start = At.Local(2026, 11, 3, 9);
        RunState running = RunState.Empty.WithRun(new SetterRun(setter.Id, start.UtcNow));

        SensorSnapshot before = At.Local(2026, 11, 4, 8, 59);
        var (next, _) = SetterPolicy.Step([setter], running, At.Local(2026, 11, 4, 9), Since(setter, before));
        Assert.IsTrue(next.Single().Renew);
    }

    [TestMethod]
    public void NextChange_IsTheNextTime()
    {
        TimeWindowCondition nine = NineAm();
        DateTime? next = nine.NextChange(new ConditionContext(At.Local(2026, 11, 3, 10)));
        Assert.AreEqual(At.Local(2026, 11, 4, 9).UtcNow, next);

        next = nine.NextChange(new ConditionContext(At.Local(2026, 11, 3, 8)));
        Assert.AreEqual(At.Local(2026, 11, 3, 9).UtcNow, next);
    }

    [TestMethod]
    public void ADayCanHaveItsOwnTime_AndSwitchingBackKeepsTheWindow()
    {
        TimeWindowCondition week = NineAm().WithMoment(DayOfWeek.Saturday, true, new TimeOnly(10, 30));
        Assert.AreEqual(new TimeOnly(10, 30), week.MomentOn(DayOfWeek.Saturday));
        Assert.AreEqual(new TimeOnly(9, 0), week.MomentOn(DayOfWeek.Monday));
        Assert.IsFalse(week.IsSameEveryDay);
        Assert.IsTrue(NineAm().IsSameEveryDay);

        // The window's end is untouched, so turning Range back on restores it.
        Assert.AreEqual(new TimeOnly(17, 0), (week with { AtTime = false }).To);
        Assert.AreEqual(new TimeOnly(17, 0), (week with { AtTime = false }).HoursOn(DayOfWeek.Saturday).To);
    }

    [TestMethod]
    public void Text_NamesTheTime()
    {
        Assert.AreEqual("at 9:00 AM every day", SetterText.Condition(NineAm()));
        Assert.IsNull(SetterText.EndsAt(NineAm()), "an instant doesn't also stop the setter");
    }

    [TestMethod]
    public void RoundTripsThroughJson_AndAnOlderFileLoadsAsASpan()
    {
        Setter at = DarkAtNine();
        string json = SetterJson.WriteSetter(at);
        Assert.IsTrue(((TimeWindowCondition)SetterJson.ReadSetter(json).When.Single()).AtTime);

        // A file from before the option has no "atTime": it's a span.
        Setter span = at with { When = [NineAm() with { AtTime = false }] };
        string older = SetterJson.WriteSetter(span).Replace("\"atTime\": false", "\"unused\": 0");
        Assert.IsFalse(older.Contains("atTime"));
        Assert.IsFalse(((TimeWindowCondition)SetterJson.ReadSetter(older).When.Single()).AtTime);
    }

    [TestMethod]
    public async Task Engine_AppliesOnce_WithNoRunAndNothingToPutBack()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkAtNine();

        await engine.StepAsync([setter], At.Local(2026, 11, 3, 8, 59));
        Assert.AreEqual(Light, theme.Value);

        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9));
        Assert.AreEqual(Dark, theme.Value);
        Assert.IsEmpty(engine.State.Active);
        Assert.IsEmpty(engine.State.Claims);
    }

    [TestMethod]
    public async Task Engine_WithAWayToStop_RunsThenPutsBack()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkAtNine(new ElapsedCondition(30));

        await engine.StepAsync([setter], At.Local(2026, 11, 3, 8, 59));
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9));
        Assert.AreEqual(Dark, theme.Value);
        Assert.IsTrue(engine.State.IsActive(setter.Id));

        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 30));
        Assert.AreEqual(Light, theme.Value);
        Assert.IsFalse(engine.State.IsActive(setter.Id));
    }

    [TestMethod]
    public async Task Engine_AddingATimeJustPassed_DoesNotFireIt()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter before = DarkAtNine() with { When = [NineAm(WeekDays.None)] };

        await engine.StepAsync([before], At.Local(2026, 11, 3, 8, 58));
        await engine.StepAsync([DarkAtNine() with { Id = before.Id }], At.Local(2026, 11, 3, 9, 3));
        Assert.AreEqual(Light, theme.Value, "an edit counts from when it was made");
    }
}
