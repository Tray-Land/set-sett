using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Tests;

[TestClass]
public sealed class SetterPolicyTests
{
    private static Setter WhileOn(string network) => Setter.Create("Office", 100) with
    {
        Start = StartMode.Automatic,
        Changes = [new SettingChange(SettingIds.Mute, new OnOffValue(true))],
        When = [new NetworkCondition(network)],
    };

    [TestMethod]
    public void Combine_UnknownOnlyWhenItCouldDecide()
    {
        Assert.IsFalse(SetterPolicy.Combine([true, false, null], ConditionMatch.All));
        Assert.IsNull(SetterPolicy.Combine([true, null], ConditionMatch.All));
        Assert.IsTrue(SetterPolicy.Combine([null, true], ConditionMatch.Any));
        Assert.IsNull(SetterPolicy.Combine([null, false], ConditionMatch.Any));
        Assert.IsFalse(SetterPolicy.Combine([], ConditionMatch.All));
    }

    [TestMethod]
    public void Automatic_StartsOnTheEdgeAndStopsWhenConditionsEnd()
    {
        Setter setter = WhileOn("Office");
        SensorSnapshot now = At.Local(2026, 10, 31, 9) with { Networks = ["Office"] };

        var (start, state) = SetterPolicy.Step([setter], RunState.Empty, now);
        Assert.IsTrue(start.Single().Start);

        state = state.WithRun(new SetterRun(setter.Id, now.UtcNow));
        var (stop, _) = SetterPolicy.Step([setter], state, now with { Networks = ["Home"] });
        Assert.AreEqual(StopReason.ConditionsEnded, stop.Single().Reason);
    }

    [TestMethod]
    public void UnknownSensor_NeverStartsOrStops()
    {
        Setter setter = WhileOn("Office");
        SensorSnapshot unknown = At.Local(2026, 10, 31, 9);

        Assert.IsEmpty(SetterPolicy.Step([setter], RunState.Empty, unknown).Transitions);

        RunState running = RunState.Empty.WithRun(new SetterRun(setter.Id, unknown.UtcNow));
        Assert.IsEmpty(SetterPolicy.Step([setter], running, unknown).Transitions);
    }

    [TestMethod]
    public void StopCondition_LatchesAnAutomaticSetterUntilItsConditionsReset()
    {
        Setter setter = WhileOn("Office") with { Until = [new ElapsedCondition(60)] };
        SensorSnapshot start = At.Local(2026, 10, 31, 9) with { Networks = ["Office"] };
        RunState state = RunState.Empty.WithRun(new SetterRun(setter.Id, start.UtcNow));

        var (stop, latched) = SetterPolicy.Step([setter], state, start.Plus(TimeSpan.FromMinutes(60)));
        Assert.AreEqual(StopReason.Until, stop.Single().Reason);
        Assert.IsTrue(latched.IsLatched(setter.Id));

        // Still at the office: stays stopped.
        latched = latched.WithoutRun(setter.Id);
        Assert.IsEmpty(SetterPolicy.Step([setter], latched, start.Plus(TimeSpan.FromMinutes(90))).Transitions);

        // Leaves, comes back: starts again.
        var (_, reset) = SetterPolicy.Step([setter], latched, start.Plus(TimeSpan.FromHours(2)) with { Networks = [] });
        Assert.IsFalse(reset.IsLatched(setter.Id));
        var (again, _) = SetterPolicy.Step([setter], reset, start.Plus(TimeSpan.FromHours(3)));
        Assert.IsTrue(again.Single().Start);
    }

    [TestMethod]
    public void DisabledManualSetter_IsStopped()
    {
        SensorSnapshot now = At.Local(2026, 10, 31, 9);
        Setter manual = WhileOn("Office") with { Start = StartMode.Manual, Enabled = false };
        RunState running = RunState.Empty.WithRun(new SetterRun(manual.Id, now.UtcNow));
        var (transitions, _) = SetterPolicy.Step([manual], running, now);
        Assert.IsFalse(transitions.Single().Start);
        Assert.AreEqual(StopReason.Disabled, transitions.Single().Reason);
    }

    [TestMethod]
    public void DisabledOrHalfBuiltSetters_DoNotRun()
    {
        SensorSnapshot now = At.Local(2026, 10, 31, 9) with { Networks = ["Office"] };

        Assert.IsEmpty(SetterPolicy.Step([WhileOn("Office") with { Enabled = false }], RunState.Empty, now).Transitions);
        Assert.IsEmpty(SetterPolicy.Step([WhileOn("Office") with { Changes = [] }], RunState.Empty, now).Transitions);
    }

    private static Setter NineToFive(params Condition[] others) => Setter.Create("Work hours", 100) with
    {
        Start = StartMode.Automatic,
        Changes = [new SettingChange(SettingIds.Mute, new OnOffValue(true))],
        When = [new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Every), .. others],
    };

    private static readonly SensorSnapshot Tuesday4Pm = At.Local(2026, 11, 3, 16);
    private static readonly SensorSnapshot Wednesday10Am = At.Local(2026, 11, 4, 10);

    private static RunState RunningSince(Setter setter, SensorSnapshot start) =>
        RunState.Empty.WithRun(new SetterRun(setter.Id, start.UtcNow));

    [TestMethod]
    public void Running_SleptThroughTheEnd_StartsAfresh()
    {
        Setter setter = NineToFive();

        SetterTransition transition = SetterPolicy.Step([setter], RunningSince(setter, Tuesday4Pm), Wednesday10Am).Transitions.Single();

        Assert.IsTrue(transition.Start);
        Assert.IsTrue(transition.Renew);
    }

    [TestMethod]
    public void Running_SameWindow_IsLeftAlone()
    {
        Setter setter = NineToFive();
        SensorSnapshot later = Tuesday4Pm.Plus(TimeSpan.FromMinutes(30));

        Assert.IsEmpty(SetterPolicy.Step([setter], RunningSince(setter, Tuesday4Pm), later).Transitions);
    }

    [TestMethod]
    public void Running_ALeftoverTimerDoesNotEndTheNewWindow()
    {
        Setter setter = NineToFive() with { Until = [new ElapsedCondition(60)] };

        var (transitions, state) = SetterPolicy.Step([setter], RunningSince(setter, Tuesday4Pm), Wednesday10Am);

        Assert.IsTrue(transitions.Single().Renew);
        Assert.IsFalse(state.IsLatched(setter.Id));
    }

    [TestMethod]
    public void Running_ATwentyFourHourWindowNeverEnds()
    {
        Setter setter = NineToFive() with { When = [new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(9, 0), WeekDays.Every)] };
        SensorSnapshot days = Tuesday4Pm.Plus(TimeSpan.FromDays(3));

        Assert.IsEmpty(SetterPolicy.Step([setter], RunningSince(setter, Tuesday4Pm), days).Transitions);
    }

    [TestMethod]
    public void Running_SunCondition_SleptThroughTheDay_StartsAfresh()
    {
        Setter setter = NineToFive() with { When = [new SunCondition(SunPhase.Night)] };
        var home = new SavedPlace("Chicago", 41.88, -87.63);
        SensorSnapshot tuesdayNight = At.Local(2026, 11, 3, 22) with { Home = home };

        Assert.IsTrue(SetterPolicy.Step([setter], RunningSince(setter, tuesdayNight), tuesdayNight.Plus(TimeSpan.FromDays(1))).Transitions.Single().Renew);
        Assert.IsEmpty(SetterPolicy.Step([setter], RunningSince(setter, tuesdayNight), tuesdayNight.Plus(TimeSpan.FromHours(1))).Transitions);
    }

    [TestMethod]
    public void Running_SensorConditionsNeverProveAnEnd()
    {
        Setter sensorOnly = WhileOn("Office");
        SensorSnapshot office = Wednesday10Am with { Networks = ["Office"] };
        Assert.IsEmpty(SetterPolicy.Step([sensorOnly], RunningSince(sensorOnly, Tuesday4Pm), office).Transitions);

        // Either one holding keeps it on, and nobody knows what the network was overnight.
        Setter either = NineToFive(new NetworkCondition("Office")) with { Match = ConditionMatch.Any };
        Assert.IsEmpty(SetterPolicy.Step([either], RunningSince(either, Tuesday4Pm), office with { Networks = [] }).Transitions);
    }

    [TestMethod]
    public void Running_AllMustHold_ClockAloneProvesTheEnd_ButAnUnknownNowWaits()
    {
        Setter setter = NineToFive(new NetworkCondition("Office"));
        RunState running = RunningSince(setter, Tuesday4Pm);

        Assert.IsTrue(SetterPolicy.Step([setter], running, Wednesday10Am with { Networks = ["Office"] }).Transitions.Single().Renew);
        Assert.IsEmpty(SetterPolicy.Step([setter], running, Wednesday10Am).Transitions);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "paused")]
    [DataRow(false, DisplayName = "ended on a stop condition")]
    public void Latched_SleptThroughTheEnd_ClearsAndStarts(bool paused)
    {
        Setter setter = NineToFive();
        RunState latched = RunState.Empty.WithLatch(setter.Id, Tuesday4Pm.UtcNow, paused);

        var (transitions, state) = SetterPolicy.Step([setter], latched, Wednesday10Am);

        Assert.IsTrue(transitions.Single().Start);
        Assert.IsFalse(transitions.Single().Renew);
        Assert.IsFalse(state.IsLatched(setter.Id));
    }

    [TestMethod]
    public void Latched_SameWindow_StaysLatched()
    {
        Setter setter = NineToFive();
        RunState latched = RunState.Empty.WithLatch(setter.Id, Tuesday4Pm.UtcNow, paused: true);

        var (transitions, state) = SetterPolicy.Step([setter], latched, Tuesday4Pm.Plus(TimeSpan.FromMinutes(30)));

        Assert.IsEmpty(transitions);
        Assert.IsTrue(state.IsPaused(setter.Id));
    }

    [TestMethod]
    public void Latched_FromAnOlderFile_WaitsToSeeItsConditionsEnd()
    {
        Setter setter = NineToFive();
        RunState older = RunState.Empty with { Latched = [setter.Id] };

        var (transitions, state) = SetterPolicy.Step([setter], older, Wednesday10Am);

        Assert.IsEmpty(transitions);
        Assert.IsTrue(state.IsLatched(setter.Id));
    }

    [TestMethod]
    public void SensorsNeeded_StopConditionsOnlyWhileRunning()
    {
        Setter setter = Setter.Create("Render", 100) with
        {
            Changes = [new SettingChange(SettingIds.KeepAwake, new ChoiceValue(SettingIds.AwakeSystem))],
            Until = [new CpuQuietCondition(10, 5)],
        };
        SensorSnapshot now = At.Local(2026, 10, 31, 9);

        Assert.AreEqual(Sensors.None, SetterPolicy.SensorsNeeded([setter], RunState.Empty, now));

        RunState running = RunState.Empty.WithRun(new SetterRun(setter.Id, now.UtcNow));
        Assert.AreEqual(Sensors.Cpu, SetterPolicy.SensorsNeeded([setter], running, now));
    }

    [TestMethod]
    public void NextWake_IsTheEarliestClockBoundary()
    {
        Setter timer = Setter.Create("Timer", 100) with
        {
            Changes = [new SettingChange(SettingIds.Mute, new OnOffValue(true))],
            Until = [new ElapsedCondition(20)],
        };
        SensorSnapshot now = At.Local(2026, 10, 31, 9);
        RunState running = RunState.Empty.WithRun(new SetterRun(timer.Id, now.UtcNow));

        Assert.AreEqual(now.UtcNow.AddMinutes(20), SetterPolicy.NextWake([timer], running, now));
        Assert.IsNull(SetterPolicy.NextWake([timer], RunState.Empty, now));
    }
}
