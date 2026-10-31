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
    public void PausedOrHalfBuiltSetters_DoNotRun()
    {
        SensorSnapshot now = At.Local(2026, 10, 31, 9) with { Networks = ["Office"] };

        Assert.IsEmpty(SetterPolicy.Step([WhileOn("Office") with { Enabled = false }], RunState.Empty, now).Transitions);
        Assert.IsEmpty(SetterPolicy.Step([WhileOn("Office") with { Changes = [] }], RunState.Empty, now).Transitions);
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
