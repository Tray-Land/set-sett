using System.Text.Json;
using SetSett.Engine;
using SetSett.Model;
using SetSett.Storage;
using SetSett.Text;

namespace SetSett.Tests;

[TestClass]
public sealed class ConditionGroupTests
{
    private static readonly ChoiceValue Dark = new(SettingIds.Dark);
    private static readonly ChoiceValue Light = new(SettingIds.Light);

    private static TimeWindowCondition Nine() =>
        new(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Every) { AtTime = true };

    private static ConditionGroup Group(ConditionMatch match, params Condition[] conditions) =>
        new() { Match = match, Conditions = conditions };

    private static Setter Automatic(params Condition[] conditions) => Setter.Create("Test", 100) with
    {
        Start = StartMode.Automatic,
        Changes = [new SettingChange(SettingIds.AppsTheme, Dark)],
        When = conditions,
    };

    [TestMethod]
    public void StartGroups_CombineIndependentlyAndKeepUnknownAnswers()
    {
        var networks = Group(ConditionMatch.Any, new NetworkCondition("Office"), new NetworkCondition("Home"));
        Setter setter = Automatic(new DeviceCondition("Dock"), networks);
        SensorSnapshot now = At.Local(2026, 11, 3, 10) with { Devices = ["Dock"], Networks = ["Home"] };

        Assert.IsTrue(SetterPolicy.StartConditionsHold(setter, now));
        Assert.IsFalse(SetterPolicy.StartConditionsHold(setter, now with { Networks = ["Other"] }));
        Assert.IsNull(SetterPolicy.StartConditionsHold(setter, now with { Networks = null }));
        Assert.IsFalse(SetterPolicy.StartConditionsHold(setter, now with { Devices = [], Networks = null }));
        Assert.IsFalse(SetterPolicy.StartConditionsHold(Automatic(Group(ConditionMatch.All)), now));
    }

    [TestMethod]
    public void StopGroup_AllRequiresEveryMemberAndUsesRunContext()
    {
        var group = Group(ConditionMatch.All, new ElapsedCondition(60), new NetworkCondition("Office"));
        Setter setter = Automatic(new NetworkCondition("Home")) with { Until = [group] };
        SensorSnapshot start = At.Local(2026, 11, 3, 9);
        var run = new SetterRun(setter.Id, start.UtcNow) { TimerEndsUtc = start.UtcNow.AddMinutes(15) };

        Assert.IsFalse(SetterPolicy.StopConditionMet(setter, run, start.Plus(TimeSpan.FromMinutes(15)) with { Networks = [] }));
        Assert.IsTrue(SetterPolicy.StopConditionMet(setter, run, start.Plus(TimeSpan.FromMinutes(15)) with { Networks = ["Office"] }));
        Assert.IsFalse(SetterPolicy.StopConditionMet(setter, run, start.Plus(TimeSpan.FromMinutes(14)) with { Networks = ["Office"] }));
    }

    [TestMethod]
    public void StopGroups_OnlyShowGuaranteedClockEndsAndOfferIndependentTimers()
    {
        SensorSnapshot now = At.Local(2026, 11, 3, 9);
        Setter setter = Setter.Create("Manual", 100) with
        {
            Until = [Group(ConditionMatch.All, new ElapsedCondition(15), new ElapsedCondition(30))],
        };
        var run = new SetterRun(setter.Id, now.UtcNow);
        Assert.AreEqual(now.UtcNow.AddMinutes(30), run.EndsAtUtc(setter, now));
        Assert.IsFalse(setter.HasTimer, "a menu duration cannot independently end an All group");
        Assert.AreEqual(now.UtcNow.AddMinutes(30),
            SetterPolicy.NextWake([setter], RunState.Empty.WithRun(run), now.Plus(TimeSpan.FromMinutes(20))));
        setter = setter with { Until = [Group(ConditionMatch.All, new ElapsedCondition(15), new NetworkCondition("Office"))] };
        Assert.IsNull(run.EndsAtUtc(setter, now), "the sensor could delay the stop indefinitely");
        setter = setter with { Until = [Group(ConditionMatch.Any, new ElapsedCondition(15), new NetworkCondition("Office"))] };
        Assert.AreEqual(now.UtcNow.AddMinutes(15), run.EndsAtUtc(setter, now));
        Assert.IsTrue(setter.HasTimer);
        Assert.AreEqual(15, setter.TimerMinutes);
        var retimed = run with { TimerEndsUtc = now.UtcNow.AddMinutes(5) };
        Assert.AreEqual(now.UtcNow.AddMinutes(5), retimed.EndsAtUtc(setter, now));
    }

    [TestMethod]
    public void NestedGroups_WatchProcessesAndScheduleNextClockBoundary()
    {
        var inner = Group(ConditionMatch.Any, new ProcessCondition("Blender.exe"), Nine());
        Setter setter = Automatic(Group(ConditionMatch.All, new DeviceCondition("Dock"), inner));
        SensorSnapshot before = At.Local(2026, 11, 3, 8, 59);

        Assert.AreEqual(Sensors.Devices | Sensors.Processes | Sensors.Clock,
            SetterPolicy.SensorsNeeded([setter], RunState.Empty, before));
        Assert.IsTrue(SetterPolicy.ProcessesWatched([setter], RunState.Empty, before).Contains("blender"));
        Assert.AreEqual(At.Local(2026, 11, 3, 9).UtcNow, SetterPolicy.NextWake([setter], RunState.Empty, before));
    }

    [TestMethod]
    public void ClockReplay_DoesNotPretendCurrentSensorValuesWereKnownInThePast()
    {
        var hours = new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Every);
        Setter setter = Automatic(Group(ConditionMatch.Any, hours, new NetworkCondition("Office")));
        SensorSnapshot yesterday = At.Local(2026, 11, 3, 16);
        SensorSnapshot now = At.Local(2026, 11, 4, 10) with { Networks = [] };

        Assert.IsFalse(SetterPolicy.ConditionsEndedSince(setter, yesterday.UtcNow, now));
        setter = Automatic(Group(ConditionMatch.All, hours, new NetworkCondition("Office")));
        Assert.IsTrue(SetterPolicy.ConditionsEndedSince(setter, yesterday.UtcNow, now));
    }

    [TestMethod]
    public void GroupedInstants_RespectAllAnyAndOuterConditions()
    {
        var timeAndNetwork = Group(ConditionMatch.All, Nine(), new NetworkCondition("Office"));
        Setter all = Automatic(timeAndNetwork);
        SensorSnapshot before = At.Local(2026, 11, 3, 8, 59);
        SensorSnapshot moment = At.Local(2026, 11, 3, 9);
        var since = new Dictionary<string, DateTime> { [all.Id] = before.UtcNow };
        Assert.IsEmpty(SetterPolicy.Step([all], RunState.Empty, before with { Networks = ["Office"] }, since).Transitions);
        Assert.IsEmpty(SetterPolicy.Step([all], RunState.Empty, moment with { Networks = [] }, since).Transitions);
        Assert.IsTrue(SetterPolicy.Step([all], RunState.Empty, moment with { Networks = ["Office"] }, since).Transitions.Single().Once);

        Setter outer = Automatic(new DeviceCondition("Dock"), Group(ConditionMatch.Any, Nine(), new NetworkCondition("Office")));
        since = new() { [outer.Id] = before.UtcNow };
        Assert.IsEmpty(SetterPolicy.Step([outer], RunState.Empty, moment with { Networks = [], Devices = [] }, since).Transitions);
        Assert.IsTrue(SetterPolicy.Step([outer], RunState.Empty, moment with { Networks = [], Devices = ["Dock"] }, since).Transitions.Single().Once);
        Assert.IsEmpty(SetterPolicy.Step([outer], RunState.Empty, moment with { Networks = [], Devices = null }, since).Transitions);
    }

    [TestMethod]
    public async Task GroupedInstant_AndLevelStartIndependentlyAndStopOnTheirOwnTerms()
    {
        var setting = FakeSetting.Theme();
        var store = new MemoryStore();
        var engine = new SetterEngine(new SettingCatalog([setting]), store);
        Setter setter = Automatic(Group(ConditionMatch.Any, Nine(), new NetworkCondition("Office"))) with
        {
            Until = [new ElapsedCondition(30)],
        };
        SensorSnapshot before = At.Local(2026, 11, 3, 8, 55) with { Networks = ["Office"] };

        await engine.StepAsync([setter], before);
        Assert.IsTrue(engine.State.IsActive(setter.Id));
        Assert.IsFalse(engine.State.RunOf(setter.Id)!.StartedByInstant);
        await engine.StepAsync([setter], before.Plus(TimeSpan.FromMinutes(1)) with { Networks = [] });
        Assert.IsFalse(engine.State.IsActive(setter.Id));
        Assert.AreEqual(Light, setting.Value);

        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9) with { Networks = [] });
        Assert.IsTrue(engine.State.RunOf(setter.Id)!.StartedByInstant);
        RunState reloaded = SetterJson.ReadState(SetterJson.WriteState(engine.State));
        Assert.IsTrue(reloaded.RunOf(setter.Id)!.StartedByInstant);
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 10) with { Networks = [] });
        Assert.IsTrue(engine.State.IsActive(setter.Id));
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 30) with { Networks = [] });
        Assert.IsFalse(engine.State.IsActive(setter.Id));
        Assert.AreEqual(Light, setting.Value);
    }

    [TestMethod]
    public async Task GroupedInstant_RenewsAnExistingLevelRunAndLatchesIfLevelStillHolds()
    {
        var setting = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([setting]), new MemoryStore());
        Setter setter = Automatic(Group(ConditionMatch.Any, Nine(), new NetworkCondition("Office"))) with
        {
            Until = [new ElapsedCondition(30)],
        };
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 8, 59) with { Networks = ["Office"] });
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9) with { Networks = ["Office"] });
        Assert.IsTrue(engine.State.RunOf(setter.Id)!.StartedByInstant);
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 30) with { Networks = ["Office"] });
        Assert.IsTrue(engine.State.IsLatched(setter.Id));
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 31) with { Networks = ["Office"] });
        Assert.IsFalse(engine.State.IsActive(setter.Id));
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 32) with { Networks = [] });
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 33) with { Networks = ["Office"] });
        Assert.IsTrue(engine.State.IsActive(setter.Id));
    }

    [TestMethod]
    public async Task GroupedInstantRun_SurvivesRecoveryWithItsOriginalStopRule()
    {
        var setting = FakeSetting.Theme();
        var store = new MemoryStore();
        Setter setter = Automatic(Group(ConditionMatch.Any, Nine(), new NetworkCondition("Office"))) with
        {
            Until = [new ElapsedCondition(30)],
        };
        var engine = new SetterEngine(new SettingCatalog([setting]), store);
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 8, 59) with { Networks = [] });
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9) with { Networks = [] });

        var recovered = new SetterEngine(new SettingCatalog([setting]), store);
        await recovered.RecoverAsync([setter], At.Local(2026, 11, 3, 9, 10) with { Networks = [] });
        Assert.IsTrue(recovered.State.RunOf(setter.Id)!.StartedByInstant);
        await recovered.StepAsync([setter], At.Local(2026, 11, 3, 9, 30) with { Networks = [] });
        Assert.IsFalse(recovered.State.IsActive(setter.Id));
        Assert.AreEqual(Light, setting.Value);
    }

    [TestMethod]
    public async Task GroupedInstantWithoutStop_LeavesItsValueEvenIfALevelRunWasActive()
    {
        var setting = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([setting]), new MemoryStore());
        Setter setter = Automatic(Group(ConditionMatch.Any, Nine(), new NetworkCondition("Office")));
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 8, 59) with { Networks = ["Office"] });
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9) with { Networks = ["Office"] });
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 9, 1) with { Networks = [] });
        Assert.IsFalse(engine.State.IsActive(setter.Id));
        Assert.AreEqual(Dark, setting.Value);
        Assert.IsEmpty(engine.State.Claims);
    }

    [TestMethod]
    public async Task GroupedInstantWithoutStop_SupersedesAnotherSettersClaim()
    {
        var setting = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([setting]), new MemoryStore());
        Setter manual = Setter.Create("Manual", 100) with { Changes = [new SettingChange(SettingIds.AppsTheme, Dark)] };
        Setter once = Automatic(Group(ConditionMatch.Any, Nine()));
        await engine.StartAsync(manual, At.Local(2026, 11, 3, 8, 58));
        await engine.StepAsync([manual, once], At.Local(2026, 11, 3, 8, 59));
        await engine.StepAsync([manual, once], At.Local(2026, 11, 3, 9));
        await engine.StopAsync(manual.Id, StopReason.User);
        Assert.AreEqual(Dark, setting.Value);
        Assert.IsEmpty(engine.State.Claims);
    }

    [TestMethod]
    public void Groups_RoundTripWithStructuralEqualityAndReadableText()
    {
        Setter setter = Automatic(new DeviceCondition("Dock"),
            Group(ConditionMatch.Any, new NetworkCondition("Office"), new NetworkCondition("Home")));
        Setter back = SetterJson.ReadSetter(SetterJson.WriteSetter(setter));
        Assert.IsFalse(back.RunsDifferentlyThan(setter));
        Assert.AreEqual(setter.When[1].GetHashCode(), back.When[1].GetHashCode());
        StringAssert.Contains(SetterText.When(back), "(connected to Office or connected to Home)");
        setter = Automatic(Group(ConditionMatch.Any, Nine(), new NetworkCondition("Office")));
        StringAssert.Contains(SetterText.ImportSummary(setter, new SettingCatalog([FakeSetting.Theme()])),
            "At a time, it applies the values once");
    }

    [TestMethod]
    public void Import_RejectsIncompleteNestedGroupsAndStopOnlyStartMembers()
    {
        var catalog = new SettingCatalog([FakeSetting.Theme()]);
        Setter incomplete = Automatic(Group(ConditionMatch.All, null!));
        Assert.ThrowsExactly<JsonException>(() => SetterImport.Read(SetterJson.WriteSetter(incomplete), catalog));
        Setter invalid = Automatic(Group(ConditionMatch.All, new ElapsedCondition(5)));
        Assert.ThrowsExactly<JsonException>(() => SetterImport.Read(SetterJson.WriteSetter(invalid), catalog));
    }
}
