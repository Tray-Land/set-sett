using System.Text.Json;
using SetSett.Engine;
using SetSett.Model;
using SetSett.Storage;
using SetSett.Text;

namespace SetSett.Tests;

[TestClass]
public sealed class ToggleTests
{
    private static Setter Switch(params ISetting[] settings) => Setter.Create("Switch", 100) with
    {
        Start = StartMode.Toggle,
        Changes = [.. settings.Select(s => new SettingChange(s.Info.Id, s.Info.DefaultValue))],
    };

    private static FakeSetting Mute(bool on = false) => new(
        new SettingInfo(SettingIds.Mute, "Mute", "Sound", Glyphs.Volume, ValueKind.OnOff), new OnOffValue(on));

    [TestMethod]
    [DataRow("light", "dark")]
    [DataRow("dark", "light")]
    public async Task Theme_EachClickSwitchesFromItsCurrentValue(string initial, string other)
    {
        var theme = FakeSetting.Theme(value: initial);
        var store = new MemoryStore();
        var engine = new SetterEngine(new SettingCatalog([theme]), store);
        Setter setter = Switch(theme);
        SensorSnapshot now = At.Local(2026, 11, 3, 12);

        await engine.StartAsync(setter, now);
        Assert.AreEqual(new ChoiceValue(other), theme.Value);
        await engine.StartAsync(setter, now);
        Assert.AreEqual(new ChoiceValue(initial), theme.Value);
        Assert.AreEqual(2, theme.Writes);
        Assert.IsEmpty(engine.State.Active);
        Assert.IsEmpty(engine.State.Claims);
        Assert.AreEqual(0, store.Saves, "a toggle has no run state to persist");
    }

    [TestMethod]
    public async Task Theme_UsesAnOutsideChangeRatherThanThePreviousClick()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        Setter setter = Switch(theme);
        SensorSnapshot now = At.Local(2026, 11, 3, 12);

        await engine.StartAsync(setter, now);
        theme.Value = new ChoiceValue(SettingIds.Light);
        await engine.StartAsync(setter, now);

        Assert.AreEqual(new ChoiceValue(SettingIds.Dark), theme.Value);
        Assert.AreEqual(2, theme.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OnOff_EachClickInvertsTheCurrentValue(bool initial)
    {
        var mute = Mute(initial);
        var engine = new SetterEngine(new SettingCatalog([mute]), new MemoryStore());
        Setter setter = Switch(mute);
        SensorSnapshot now = At.Local(2026, 11, 3, 12);

        await engine.StartAsync(setter, now);
        Assert.AreEqual(new OnOffValue(!initial), mute.Value);
        await engine.StartAsync(setter, now);
        Assert.AreEqual(new OnOffValue(initial), mute.Value);
        Assert.AreEqual(2, mute.Writes);
    }

    [TestMethod]
    public async Task MultipleSettings_SwitchIndependentlyAndRespectTheSelectedScope()
    {
        var windows = FakeSetting.Theme(SettingIds.WindowsTheme, SettingIds.Dark);
        var apps = FakeSetting.Theme();
        var mute = Mute();
        var engine = new SetterEngine(new SettingCatalog([windows, apps, mute]), new MemoryStore());
        SensorSnapshot now = At.Local(2026, 11, 3, 12);

        await engine.StartAsync(Switch(windows, apps, mute), now);
        Assert.AreEqual(new ChoiceValue(SettingIds.Light), windows.Value);
        Assert.AreEqual(new ChoiceValue(SettingIds.Dark), apps.Value);
        Assert.AreEqual(new OnOffValue(true), mute.Value);

        await engine.StartAsync(Switch(apps), now);
        Assert.AreEqual(new ChoiceValue(SettingIds.Light), apps.Value);
        Assert.AreEqual(1, windows.Writes, "an Apps-only action leaves Windows alone");
        Assert.AreEqual(1, mute.Writes);
    }

    [TestMethod]
    public async Task Toggle_DoesNotStartAutomaticallyExpireOrPutBackOnQuitOrRestart()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        var catalog = new SettingCatalog([theme]);
        var engine = new SetterEngine(catalog, store);
        Setter setter = Switch(theme) with
        {
            When = [new PowerCondition(PowerCheck.PluggedIn, 30)],
            Until = [new ElapsedCondition(1)],
            PutBack = true,
        };
        SensorSnapshot now = At.Local(2026, 11, 3, 12);

        Assert.IsEmpty(await engine.StepAsync([setter], now));
        Assert.AreEqual(0, theme.Writes);
        Assert.AreEqual(Sensors.None, SetterPolicy.SensorsNeeded([setter], engine.State, now));
        Assert.IsFalse(setter.HasTimer);
        Assert.IsTrue(setter.AppliesOnce);

        await engine.StartAsync(setter, now);
        Assert.IsEmpty(await engine.StepAsync([setter], now.Plus(TimeSpan.FromMinutes(2))));
        await engine.StopAllAsync();
        await new SetterEngine(catalog, store).RecoverAsync([setter], now.Plus(TimeSpan.FromHours(1)));

        Assert.AreEqual(new ChoiceValue(SettingIds.Dark), theme.Value);
        Assert.AreEqual(1, theme.Writes);
        Assert.IsEmpty(store.Saved.Active);
        Assert.IsEmpty(store.Saved.Claims);
    }

    [TestMethod]
    public async Task DisabledOrEmptyToggle_DoesNotWrite()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        SensorSnapshot now = At.Local(2026, 11, 3, 12);

        await engine.StartAsync(Switch(theme) with { Enabled = false }, now);
        await engine.StartAsync(Switch(), now);

        Assert.AreEqual(0, theme.Writes);
        Assert.IsEmpty(engine.State.Active);
    }

    [TestMethod]
    public async Task Recovery_ASetterEditedIntoToggleModeEndsItsOldRunWithoutToggling()
    {
        var theme = FakeSetting.Theme();
        var catalog = new SettingCatalog([theme]);
        var store = new MemoryStore();
        SensorSnapshot now = At.Local(2026, 11, 3, 12);
        Setter setter = Setter.Create("Dark", 101) with
        {
            Changes = [new SettingChange(theme.Info.Id, new ChoiceValue(SettingIds.Dark))],
        };
        await new SetterEngine(catalog, store).StartAsync(setter, now);

        var recovered = new SetterEngine(catalog, store);
        await recovered.RecoverAsync([setter with { Start = StartMode.Toggle }], now);

        Assert.AreEqual(new ChoiceValue(SettingIds.Light), theme.Value);
        Assert.AreEqual(2, theme.Writes, "the old run is put back, not toggled as a new action");
        Assert.IsEmpty(recovered.State.Active);
        Assert.IsEmpty(recovered.State.Claims);
    }

    [TestMethod]
    public async Task TimedStart_CannotTurnAToggleIntoARunningSetter()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());

        await engine.StartForAsync(Switch(theme), At.Local(2026, 11, 3, 12), TimeSpan.FromMinutes(30));

        Assert.AreEqual(0, theme.Writes);
        Assert.IsEmpty(engine.State.Active);
    }

    [TestMethod]
    public async Task UnreadableOrInvalidValue_ReportsAProblemWithoutGuessing()
    {
        foreach (bool unreadable in new[] { true, false })
        {
            var theme = FakeSetting.Theme();
            theme.Readable = !unreadable;
            if (!unreadable)
            {
                theme.Value = new ChoiceValue("unknown");
            }

            var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
            Setter setter = Switch(theme);
            await engine.StartAsync(setter, At.Local(2026, 11, 3, 12));

            Assert.AreEqual(0, theme.Writes);
            Assert.AreEqual(theme.Info.Id, engine.Problems.Single().SettingId);
            Assert.IsEmpty(engine.State.Active);
            Assert.IsEmpty(engine.State.Claims);
        }
    }

    [TestMethod]
    public async Task UnsupportedUnknownAndUnavailableSettings_ReportProblems()
    {
        var volume = FakeSetting.Volume();
        var awake = FakeSetting.KeepAwake();
        var theme = FakeSetting.Theme();
        theme.Available = false;
        var engine = new SetterEngine(new SettingCatalog([volume, awake, theme]), new MemoryStore());
        Setter setter = Switch(volume, awake, theme) with
        {
            Changes = [.. Switch(volume, awake, theme).Changes, new SettingChange("unknown", new OnOffValue(true))],
        };

        await engine.StartAsync(setter, At.Local(2026, 11, 3, 12));

        CollectionAssert.AreEquivalent(
            new[] { volume.Info.Id, awake.Info.Id, theme.Info.Id, "unknown" },
            engine.Problems.Select(p => p.SettingId).ToArray());
        Assert.AreEqual(0, volume.Writes + awake.Writes + theme.Writes);
        Assert.IsEmpty(engine.State.Active);
    }

    [TestMethod]
    public async Task SuccessfulToggle_VoidsOlderClaimsEvenAfterSwitchingBack()
    {
        var theme = FakeSetting.Theme();
        var store = new MemoryStore();
        var engine = new SetterEngine(new SettingCatalog([theme]), store);
        SensorSnapshot now = At.Local(2026, 11, 3, 12);
        Setter running = Setter.Create("Dark", 101) with
        {
            Changes = [new SettingChange(theme.Info.Id, new ChoiceValue(SettingIds.Dark))],
        };
        await engine.StartAsync(running, now);

        Setter action = Switch(theme);
        await engine.StartAsync(action, now);
        Assert.IsEmpty(store.Saved.Claims, "the lasting user change is persisted");
        await engine.StartAsync(action, now);
        await engine.StopAllAsync();

        Assert.AreEqual(new ChoiceValue(SettingIds.Dark), theme.Value, "Quit must not undo the two clicks");
        Assert.AreEqual(3, theme.Writes);
    }

    [TestMethod]
    public async Task RefusedToggle_PreservesTheExistingPutBack()
    {
        var theme = FakeSetting.Theme();
        var engine = new SetterEngine(new SettingCatalog([theme]), new MemoryStore());
        SensorSnapshot now = At.Local(2026, 11, 3, 12);
        Setter running = Setter.Create("Dark", 101) with
        {
            Changes = [new SettingChange(theme.Info.Id, new ChoiceValue(SettingIds.Dark))],
        };
        await engine.StartAsync(running, now);
        theme.Refuse = true;

        await engine.StartAsync(Switch(theme), now);
        Assert.AreEqual(theme.Info.Id, engine.Problems.Single().SettingId);
        Assert.HasCount(1, engine.State.ClaimsOn(theme.Info.Id));
        theme.Refuse = false;
        await engine.StopAllAsync();

        Assert.AreEqual(new ChoiceValue(SettingIds.Light), theme.Value);
        Assert.AreEqual(2, theme.Writes);
    }

    [TestMethod]
    public void ToggleEligibility_OnlyWindowsHeldBinaryValues()
    {
        SettingInfo theme = FakeSetting.Theme().Info;
        Assert.IsTrue(theme.CanToggle);
        Assert.AreEqual(new ChoiceValue(SettingIds.Light), theme.OtherValue(new ChoiceValue("DARK")));
        Assert.AreEqual(new OnOffValue(false), Mute().Info.OtherValue(new OnOffValue(true)));
        Assert.IsNull(theme.OtherValue(new OnOffValue(true)));
        Assert.IsNull(theme.OtherValue(null));

        SettingInfo[] unsupported =
        [
            FakeSetting.Volume().Info,
            FakeSetting.KeepAwake().Info,
            theme with { Kind = ValueKind.Duration },
            theme with { Choices = [new("one", "One")] },
            theme with { Choices = [new("one", "One"), new("two", "Two"), new("three", "Three")] },
            theme with { Choices = [new("same", "One"), new("SAME", "Two")] },
        ];
        foreach (SettingInfo info in unsupported)
        {
            Assert.IsFalse(info.CanToggle, info.Id);
            Assert.IsNull(info.OtherValue(info.DefaultValue));
        }
    }

    [TestMethod]
    public void Toggle_RoundTripsAndImportsWithActionWording()
    {
        var theme = FakeSetting.Theme();
        var catalog = new SettingCatalog([theme]);
        Setter setter = Switch(theme);
        Setter back = SetterImport.Read(SetterJson.WriteSetters([setter]), catalog);

        Assert.AreEqual(StartMode.Toggle, back.Start);
        Assert.IsFalse(back.RunsDifferentlyThan(setter));
        Assert.IsTrue(back.NewCopy(101).IsToggle);
        Assert.AreEqual("Theme: toggle Light / Dark", SetterText.Changes(back, catalog));
        Assert.AreEqual("Click to switch", SetterText.Status(back, RunState.Empty, At.Local(2026, 11, 3, 12)));
        Assert.AreEqual(
            "Changes: Theme: toggle Light / Dark.\nStarts: on each click.\n" +
            "Afterwards: switches each setting from its current value and leaves it there.",
            SetterText.ImportSummary(back, catalog));
        Assert.AreEqual("Disabled", SetterText.Status(back with { Enabled = false }, RunState.Empty, At.Local(2026, 11, 3, 12)));
    }

    [TestMethod]
    public void Import_RejectsTogglesOfUnsupportedSettings()
    {
        var volume = FakeSetting.Volume();
        var catalog = new SettingCatalog([volume]);
        var error = Assert.ThrowsExactly<JsonException>(() => SetterImport.Read(SetterJson.WriteSetter(Switch(volume)), catalog));

        StringAssert.Contains(error.Message, "cannot be toggled");
    }

    [TestMethod]
    public void OldSetters_KeepManualStartStopBehavior()
    {
        Setter old = SetterJson.ReadSetter("""{"id":"old","changes":[{"settingId":"mute","value":{"type":"onOff","on":true}}]}""");

        Assert.AreEqual(StartMode.Manual, old.Start);
        Assert.IsFalse(old.IsToggle);
        Assert.IsFalse(old.AppliesOnce);
        Assert.IsTrue(old.PutBack);
        Assert.IsTrue((old with { Start = StartMode.Toggle }).RunsDifferentlyThan(old));
    }
}