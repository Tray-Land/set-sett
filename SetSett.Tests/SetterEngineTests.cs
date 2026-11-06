using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Tests;

[TestClass]
public sealed class SetterEngineTests
{
    private static readonly ChoiceValue Dark = new(SettingIds.Dark);
    private static readonly ChoiceValue Light = new(SettingIds.Light);

    private static Setter DarkFor(int minutes) => Setter.Create("Dark", 100) with
    {
        Changes = [new SettingChange(SettingIds.AppsTheme, Dark)],
        Until = [new ElapsedCondition(minutes)],
    };

    private static Setter DarkNineToFive() => Setter.Create("Dark at work", 100) with
    {
        Start = StartMode.Automatic,
        Changes = [new SettingChange(SettingIds.AppsTheme, Dark)],
        When = [new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Every)],
    };

    [TestMethod]
    public async Task SleptThroughTheEnd_ValueStillSet_KeepsSnapshotAndDoesNotWrite()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkNineToFive();
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 16));

        SensorSnapshot nextDay = At.Local(2026, 11, 4, 10);
        await engine.StepAsync([setter], nextDay);

        Assert.AreEqual(1, theme.Writes, "nothing flickers");
        Assert.AreEqual(nextDay.UtcNow, engine.State.RunOf(setter.Id)!.ActiveSinceUtc, "a new run");

        await engine.StepAsync([setter], At.Local(2026, 11, 4, 17));
        Assert.AreEqual(Light, theme.Value, "still puts back the original value");
    }

    [TestMethod]
    public async Task SleptThroughTheEnd_ValueChanged_SetsItAgainLikeEnteringAWindow()
    {
        var volume = FakeSetting.Volume(40);
        var engine = new SetterEngine(new SettingCatalog([volume]), new MemoryStore());
        Setter setter = DarkNineToFive() with { Changes = [new SettingChange(SettingIds.Volume, new NumberValue(20))] };
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 16));
        volume.Value = new NumberValue(70); // the user turned it up before closing the lid

        await engine.StepAsync([setter], At.Local(2026, 11, 4, 10));
        Assert.AreEqual(new NumberValue(20), volume.Value);

        await engine.StepAsync([setter], At.Local(2026, 11, 4, 17));
        Assert.AreEqual(new NumberValue(70), volume.Value, "the value it found when this run began");
    }

    [TestMethod]
    public async Task SleptThroughTheEnd_PutBackOff_WritesOnlyAChangedValue()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkNineToFive() with { PutBack = false };
        await engine.StepAsync([setter], At.Local(2026, 11, 3, 16));

        await engine.StepAsync([setter], At.Local(2026, 11, 4, 10));
        Assert.AreEqual(1, theme.Writes);

        theme.Value = Light;
        await engine.StepAsync([setter], At.Local(2026, 11, 5, 10));
        Assert.AreEqual(Dark, theme.Value);
        Assert.IsEmpty(engine.State.Claims);
    }

    [TestMethod]
    public async Task Recover_RunFromAnEarlierWindow_SetsAChangedValueAgain()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        Setter setter = DarkNineToFive();
        await new SetterEngine(new SettingCatalog([theme]), store).StepAsync([setter], At.Local(2026, 11, 3, 16));
        theme.Value = Light;

        var engine = new SetterEngine(new SettingCatalog([theme]), store);
        await engine.RecoverAsync([setter], At.Local(2026, 11, 4, 10));

        Assert.AreEqual(Dark, theme.Value);
        Assert.IsTrue(engine.State.IsActive(setter.Id));
    }

    [TestMethod]
    public async Task Pause_PutsBackAndWaits_ThenResumeStartsAgain()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkNineToFive();
        SensorSnapshot start = At.Local(2026, 11, 3, 10);
        await engine.StepAsync([setter], start);

        await engine.PauseAsync(setter.Id, start);
        Assert.AreEqual(Light, theme.Value);
        Assert.IsTrue(engine.State.IsPaused(setter.Id));

        await engine.StepAsync([setter], start.Plus(TimeSpan.FromHours(1)));
        Assert.AreEqual(Light, theme.Value, "stays paused for the rest of the window");

        await engine.ResumeAsync([setter], setter.Id, start.Plus(TimeSpan.FromHours(2)));
        Assert.AreEqual(Dark, theme.Value);
        Assert.IsFalse(engine.State.IsPaused(setter.Id));
        Assert.IsTrue(engine.State.IsActive(setter.Id));
    }

    [TestMethod]
    public async Task Resume_OutsideItsConditions_WaitsForThem()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkNineToFive();
        SensorSnapshot start = At.Local(2026, 11, 3, 10);
        await engine.StepAsync([setter], start);
        await engine.PauseAsync(setter.Id, start);

        await engine.ResumeAsync([setter], setter.Id, At.Local(2026, 11, 3, 18));

        Assert.AreEqual(Light, theme.Value);
        Assert.IsFalse(engine.State.IsActive(setter.Id));
        Assert.IsFalse(engine.State.IsPaused(setter.Id));
    }

    [TestMethod]
    public async Task Pause_EndsOnItsOwnAtTheNextWindow_EvenIfThePCSleptThroughTheGap()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkNineToFive();
        SensorSnapshot start = At.Local(2026, 11, 3, 16);
        await engine.StepAsync([setter], start);
        await engine.PauseAsync(setter.Id, start);

        await engine.StepAsync([setter], At.Local(2026, 11, 4, 10));

        Assert.AreEqual(Dark, theme.Value);
        Assert.IsTrue(engine.State.IsActive(setter.Id));
        Assert.IsFalse(engine.State.IsLatched(setter.Id));
    }

    [TestMethod]
    public async Task Pause_SurvivesARestartInTheSameWindow()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        Setter setter = DarkNineToFive();
        SensorSnapshot start = At.Local(2026, 11, 3, 10);
        var first = new SetterEngine(new SettingCatalog([theme]), store);
        await first.StepAsync([setter], start);
        await first.PauseAsync(setter.Id, start);

        var engine = new SetterEngine(new SettingCatalog([theme]), store);
        await engine.RecoverAsync([setter], start.Plus(TimeSpan.FromHours(1)));

        Assert.AreEqual(Light, theme.Value);
        Assert.IsTrue(engine.State.IsPaused(setter.Id));
    }

    [TestMethod]
    public async Task StopAll_ForgetsPauses()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkNineToFive();
        SensorSnapshot start = At.Local(2026, 11, 3, 10);
        await engine.StepAsync([setter], start);
        await engine.PauseAsync(setter.Id, start);

        await engine.StopAllAsync();

        Assert.IsFalse(engine.State.IsLatched(setter.Id));
        Assert.IsEmpty(engine.State.LatchDetails);
    }

    [TestMethod]
    public async Task ManualTimer_SetsThenPutsBackWhenItExpires()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkFor(30);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);

        await engine.StartAsync(setter, start);
        Assert.AreEqual(Dark, theme.Value);

        await engine.StepAsync([setter], start.Plus(TimeSpan.FromMinutes(29)));
        Assert.AreEqual(Dark, theme.Value);

        IReadOnlyList<SetterTransition> transitions = await engine.StepAsync([setter], start.Plus(TimeSpan.FromMinutes(30)));
        Assert.AreEqual(Light, theme.Value);
        Assert.AreEqual(StopReason.Until, transitions.Single().Reason);
        Assert.IsFalse(engine.State.IsActive(setter.Id));
    }

    [TestMethod]
    public async Task StartFor_UsesTheChosenLengthInsteadOfItsOwnTimer()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkFor(30);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);

        await engine.StartForAsync(setter, start, TimeSpan.FromHours(2));
        Assert.AreEqual(Dark, theme.Value);

        await engine.StepAsync([setter], start.Plus(TimeSpan.FromMinutes(31)));
        Assert.AreEqual(Dark, theme.Value, "its own 30 minutes don't apply to this run");

        await engine.StepAsync([setter], start.Plus(TimeSpan.FromHours(2)));
        Assert.AreEqual(Light, theme.Value);
        Assert.IsFalse(engine.State.IsActive(setter.Id));
    }

    [TestMethod]
    public async Task StartFor_WhileOn_OnlyMovesWhenItStops()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkFor(30);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);

        await engine.StartAsync(setter, start);
        await engine.StartForAsync(setter, start.Plus(TimeSpan.FromMinutes(20)), TimeSpan.FromMinutes(15));

        Assert.AreEqual(1, theme.Writes, "nothing is set again");
        Assert.AreEqual(start.UtcNow, engine.State.RunOf(setter.Id)!.ActiveSinceUtc, "still the same run");

        await engine.StepAsync([setter], start.Plus(TimeSpan.FromMinutes(31)));
        Assert.AreEqual(Dark, theme.Value, "15 minutes from minute 20 is minute 35");

        await engine.StepAsync([setter], start.Plus(TimeSpan.FromMinutes(35)));
        Assert.AreEqual(Light, theme.Value);
    }

    [TestMethod]
    public async Task Recover_PutsBackAChosenLengthThatEndedWhileSetSettWasClosed()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        Setter setter = DarkFor(8 * 60);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);
        await new SetterEngine(new SettingCatalog([theme]), store).StartForAsync(setter, start, TimeSpan.FromMinutes(15));

        // Crash; the next launch is an hour later, well inside its own 8 hours.
        var engine = new SetterEngine(new SettingCatalog([theme]), store);
        await engine.RecoverAsync([setter], start.Plus(TimeSpan.FromHours(1)));

        Assert.AreEqual(Light, theme.Value);
        Assert.IsEmpty(engine.State.Active);
    }

    [TestMethod]
    public async Task UserChangeWhileRunning_VoidsThePutBack()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkFor(30);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);

        await engine.StartAsync(setter, start);
        theme.Value = Light; // the user switched back by hand

        await engine.StopAsync(setter.Id, StopReason.User);

        Assert.AreEqual(Light, theme.Value);
        Assert.AreEqual(1, theme.Writes, "one write to start, none to put back");
    }

    [TestMethod]
    public async Task PutBackOff_LeavesTheValue()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkFor(30) with { PutBack = false };

        await engine.StartAsync(setter, At.Local(2026, 10, 31, 12));
        await engine.StopAsync(setter.Id, StopReason.User);

        Assert.AreEqual(Dark, theme.Value);
        Assert.IsEmpty(engine.State.Claims);
    }

    [TestMethod]
    public async Task SnapshotIsSavedBeforeTheWrite()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        var engine = new SetterEngine(new SettingCatalog([theme]), store);

        await engine.StartAsync(DarkFor(30), At.Local(2026, 10, 31, 12));

        Assert.AreEqual(Light, store.Saved.ClaimsOn(SettingIds.AppsTheme).Single().Snapshot);
    }

    [TestMethod]
    public async Task Recover_PutsBackATimerThatExpiredWhileSetSettWasClosed()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        Setter setter = DarkFor(30);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);
        await new SetterEngine(new SettingCatalog([theme]), store).StartAsync(setter, start);

        // Crash; the next launch is two hours later.
        var engine = new SetterEngine(new SettingCatalog([theme]), store);
        await engine.RecoverAsync([setter], start.Plus(TimeSpan.FromHours(2)));

        Assert.AreEqual(Light, theme.Value);
        Assert.IsEmpty(engine.State.Active);
    }

    [TestMethod]
    public async Task Recover_VoidsClaimsTheUserOverrodeWhileClosed()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        Setter setter = DarkFor(30);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);
        await new SetterEngine(new SettingCatalog([theme]), store).StartAsync(setter, start);

        theme.Value = Light; // changed in Settings while Set Sett was down
        var engine = new SetterEngine(new SettingCatalog([theme]), store);
        await engine.RecoverAsync([setter], start.Plus(TimeSpan.FromMinutes(5)));

        Assert.IsEmpty(engine.State.Claims);
        Assert.IsTrue(engine.State.IsActive(setter.Id), "still running until its timer ends");

        theme.Value = Dark; // even if it ends up dark again later, the void stands
        await engine.StepAsync([setter], start.Plus(TimeSpan.FromMinutes(31)));
        Assert.AreEqual(Dark, theme.Value);
    }

    [TestMethod]
    public async Task Recover_ReappliesValuesOnlySetSettHeld()
    {
        var awake = FakeSetting.KeepAwake();
        var store = new MemoryStore();
        Setter setter = Setter.Create("Awake", 100) with
        {
            Changes = [new SettingChange(SettingIds.KeepAwake, new ChoiceValue(SettingIds.AwakeSystem))],
        };
        SensorSnapshot start = At.Local(2026, 10, 31, 12);
        await new SetterEngine(new SettingCatalog([awake]), store).StartAsync(setter, start);

        awake.Value = new ChoiceValue(SettingIds.AwakeOff); // the process died, and its power request with it
        var engine = new SetterEngine(new SettingCatalog([awake]), store);
        await engine.RecoverAsync([setter], start.Plus(TimeSpan.FromMinutes(1)));

        Assert.AreEqual(new ChoiceValue(SettingIds.AwakeSystem), awake.Value);

        await engine.StopAsync(setter.Id, StopReason.User);
        Assert.AreEqual(new ChoiceValue(SettingIds.AwakeOff), awake.Value);
    }

    [TestMethod]
    public async Task FailedWrite_IsReportedAndLeavesNoClaim()
    {
        var theme = FakeSetting.Theme();
        theme.Refuse = true;
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());

        await engine.StartAsync(DarkFor(30), At.Local(2026, 10, 31, 12));

        Assert.HasCount(1, engine.Problems);
        Assert.IsEmpty(engine.State.Claims);
    }

    [TestMethod]
    public async Task UnavailableSetting_IsReportedAndLeavesNoClaim()
    {
        var theme = FakeSetting.Theme();
        theme.Available = false;
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());

        await engine.StartAsync(DarkFor(30), At.Local(2026, 10, 31, 12));

        Assert.HasCount(1, engine.Problems);
        Assert.AreEqual(SettingIds.AppsTheme, engine.Problems[0].SettingId);
        Assert.IsEmpty(engine.State.Claims);
        Assert.AreEqual(0, theme.Writes);
    }

    [TestMethod]
    public async Task MissingSetting_IsReportedAndLeavesNoClaim()
    {
        var engine = new SetterEngine(new SettingCatalog([FakeSetting.Volume()]), new MemoryStore());

        await engine.StartAsync(DarkFor(30), At.Local(2026, 10, 31, 12));

        Assert.HasCount(1, engine.Problems);
        Assert.IsEmpty(engine.State.Claims);
    }

    [TestMethod]
    public async Task RejectedValue_IsReportedAndLeavesNoClaim()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = Setter.Create("Bad", 100) with
        {
            Changes = [new SettingChange(SettingIds.AppsTheme, new NumberValue(5))],
        };

        await engine.StartAsync(setter, At.Local(2026, 10, 31, 12));

        Assert.HasCount(1, engine.Problems);
        Assert.IsEmpty(engine.State.Claims);
        Assert.AreEqual(0, theme.Writes);
    }

    [TestMethod]
    public async Task DeletedSetter_StillPutsBackWhatItChanged()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = DarkFor(30);
        SensorSnapshot start = At.Local(2026, 10, 31, 12);
        await engine.StartAsync(setter, start);

        IReadOnlyList<SetterTransition> transitions = await engine.StepAsync([], start.Plus(TimeSpan.FromMinutes(1)));

        Assert.AreEqual(StopReason.Removed, transitions.Single().Reason);
        Assert.AreEqual(Light, theme.Value);
    }

    [TestMethod]
    public async Task StopAll_PutsEverythingBack()
    {
        var theme = FakeSetting.Theme();
        var volume = FakeSetting.Volume(40);
        var engine = new SetterEngine(new SettingCatalog([theme, volume]), new MemoryStore());
        Setter setter = Setter.Create("Both", 100) with
        {
            Changes = [new SettingChange(SettingIds.AppsTheme, Dark), new SettingChange(SettingIds.Volume, new NumberValue(10))],
        };

        await engine.StartAsync(setter, At.Local(2026, 10, 31, 12));
        await engine.StopAllAsync();

        Assert.AreEqual(Light, theme.Value);
        Assert.AreEqual(new NumberValue(40), volume.Value);
        Assert.IsEmpty(engine.State.Active);
    }
}
