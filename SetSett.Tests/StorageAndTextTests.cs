using System.Globalization;
using System.Text.Json;
using SetSett.Engine;
using SetSett.Icons;
using SetSett.Model;
using SetSett.Places;
using SetSett.Storage;
using SetSett.Text;

namespace SetSett.Tests;

[TestClass]
public sealed class StorageAndTextTests
{
    [TestMethod]
    public void Setters_RoundTripEveryConditionKind()
    {
        Setter setter = Setter.Create("Everything", 101) with
        {
            Start = StartMode.Automatic,
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(30)), new SettingChange(SettingIds.Mute, new OnOffValue(true))],
            When =
            [
                new TimeWindowCondition(new TimeOnly(22, 0), new TimeOnly(6, 30), WeekDays.Weekdays),
                new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Every)
                    .WithDay(DayOfWeek.Friday, true, new TimeOnly(9, 0), new TimeOnly(13, 0))
                    .WithDay(DayOfWeek.Sunday, false, new TimeOnly(10, 0), new TimeOnly(14, 0)),
                new SunCondition(SunPhase.Night) { SunsetOffsetMinutes = -15 },
                new NetworkCondition("Office") { Not = true },
                new DeviceCondition("Surface Headphones"),
                new ProcessCondition("blender"),
                new PowerCondition(PowerCheck.BatteryBelow, 30),
                new PlaceCondition(new GeoPoint(43, -88), 250, "Home"),
            ],
            Until = [new ElapsedCondition(45), new ClockCondition(new TimeOnly(17, 0)), new CpuQuietCondition(10, 5)],
            TimerChoices = [10, 90],
            Indicator = DurationIndicator.VerticalBar,
        };

        Setter back = SetterJson.ReadSetters(SetterJson.WriteSetters([setter])).Single();

        Assert.IsTrue(back.Changes.SequenceEqual(setter.Changes));
        Assert.IsTrue(back.When.SequenceEqual(setter.When));
        Assert.IsTrue(back.Until.SequenceEqual(setter.Until));
        Assert.IsTrue(back.TimerChoices.SequenceEqual(setter.TimerChoices));
        Assert.AreEqual(setter.TrayIconId, back.TrayIconId);
        Assert.AreEqual(DurationIndicator.VerticalBar, back.Indicator);
        Assert.IsFalse(back.RunsDifferentlyThan(setter));

        Setter standalone = SetterJson.ReadSetter(SetterJson.WriteSetter(setter));
        Assert.IsTrue(standalone.Changes.SequenceEqual(setter.Changes));
        Assert.IsTrue(standalone.When.SequenceEqual(setter.When));
        Assert.IsTrue(standalone.Until.SequenceEqual(setter.Until));
        Assert.IsTrue(standalone.TimerChoices.SequenceEqual(setter.TimerChoices));
        Assert.AreEqual(setter.ActiveLook, standalone.ActiveLook);
        Assert.AreEqual(setter.InactiveLook, standalone.InactiveLook);
        Assert.AreEqual(setter.TrayIconId, standalone.TrayIconId);
        Assert.AreEqual(setter.Indicator, standalone.Indicator);
        Assert.IsFalse(standalone.RunsDifferentlyThan(setter));
    }

    [TestMethod]
    public void RunState_RoundTrips()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, SettingIds.AppsTheme, "a", new ChoiceValue("light"), new ChoiceValue("dark"))
            .WithRun(new SetterRun("a", new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc)))
            .WithRun(new SetterRun("c", new DateTime(2026, 10, 31, 17, 0, 0, DateTimeKind.Utc))
            {
                TimerEndsUtc = new DateTime(2026, 10, 31, 19, 0, 0, DateTimeKind.Utc),
            })
            .WithLatch("b", new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc), paused: true);

        RunState back = SetterJson.ReadState(SetterJson.WriteState(state));

        Assert.IsTrue(back.Active.SequenceEqual(state.Active));
        Assert.IsNull(back.RunOf("a")!.TimerEndsUtc);
        Assert.AreEqual("b", back.Latched.Single());
        Assert.IsTrue(back.IsPaused("b"));
        Assert.AreEqual(state.LatchedSinceUtc("b"), back.LatchedSinceUtc("b"));
        Assert.AreEqual(state.ClaimsOn(SettingIds.AppsTheme).Single(), back.ClaimsOn(SettingIds.AppsTheme).Single());
    }

    [TestMethod]
    public void OlderStateFile_LatchHasNoDetailsAndIsNotAPause()
    {
        RunState state = SetterJson.ReadState("""{"version": 1, "state": {"latched": ["x"]}}""");

        Assert.IsTrue(state.IsLatched("x"));
        Assert.IsFalse(state.IsPaused("x"));
        Assert.IsNull(state.LatchedSinceUtc("x"));
        Assert.IsEmpty(state.WithoutLatch("x").Latched);
    }

    [TestMethod]
    public void Status_SaysPausedOnlyForAPause()
    {
        Setter setter = Setter.Create("Dark", 100) with
        {
            Start = StartMode.Automatic,
            Changes = [new SettingChange(SettingIds.AppsTheme, new ChoiceValue(SettingIds.Dark))],
            When = [new SunCondition(SunPhase.Night)],
        };
        SensorSnapshot now = At.Local(2026, 11, 3, 22);

        Assert.AreEqual(
            "Paused · starts again between sunset and sunrise",
            SetterText.Status(setter, RunState.Empty.WithLatch(setter.Id, now.UtcNow, paused: true), now));
        Assert.AreEqual(
            "Stopped until its conditions reset",
            SetterText.Status(setter, RunState.Empty.WithLatch(setter.Id, now.UtcNow), now));
    }

    [TestMethod]
    public void MissingProperties_KeepTheirDefaults()
    {
        Setter setter = SetterJson.ReadSetters("""
            {"version": 1, "setters": [{"id": "x", "changes": [{"settingId": "mute", "value": {"type": "onOff", "on": true}}]}]}
            """).Single();

        Assert.IsTrue(setter.Enabled);
        Assert.IsTrue(setter.PutBack);
        Assert.IsTrue(setter.ShowInFlyout);
        Assert.AreEqual(Setter.DefaultGlyph, setter.Glyph);
        Assert.IsEmpty(setter.When);
        Assert.IsEmpty(setter.Until);
        Assert.IsTrue(setter.CanRun);
        Assert.IsTrue(setter.TimerChoices.SequenceEqual(Setter.DefaultTimerChoices));

        RunState state = SetterJson.ReadState("""{"version": 1, "state": {}}""");
        Assert.IsEmpty(state.Active);
        Assert.IsEmpty(state.Claims);
    }

    [TestMethod]
    public void TimeWindow_WithoutPerDay_IsTheSameHoursEveryChosenDay()
    {
        Setter setter = SetterJson.ReadSetters("""
            {"version": 1, "setters": [{"id": "x", "start": "Automatic",
              "when": [{"kind": "time", "from": "22:00:00", "to": "07:00:00", "days": "Every"}]}]}
            """).Single();

        var window = (TimeWindowCondition)setter.When.Single();
        Assert.IsNotNull(window.PerDay);
        Assert.IsEmpty(window.PerDay);
        Assert.IsTrue(window.IsSameEveryDay);
    }

    [TestMethod]
    public void NewerFileVersion_IsRefused()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => SetterJson.ReadSetters("""{"version": 99, "setters": []}"""));
    }

    [TestMethod]
    public void Text_DescribesConditionsAndValues()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

        Assert.AreEqual("for 1 hr 30 min", SetterText.Until(new ElapsedCondition(90)));
        Assert.AreEqual("until blender closes", SetterText.Until(new ProcessCondition("blender") { Not = true }));
        Assert.AreEqual("between 30 min before sunset and sunrise", SetterText.Condition(new SunCondition(SunPhase.Night) { SunsetOffsetMinutes = -30 }));
        Assert.AreEqual("between 10:00 PM and 6:00 AM on weekdays", SetterText.Condition(new TimeWindowCondition(new TimeOnly(22, 0), new TimeOnly(6, 0), WeekDays.Weekdays)));
        TimeWindowCondition week = new TimeWindowCondition(new TimeOnly(9, 0), new TimeOnly(17, 0), WeekDays.Weekdays | WeekDays.Saturday)
            .WithDay(DayOfWeek.Saturday, true, new TimeOnly(10, 0), new TimeOnly(14, 0));
        Assert.AreEqual("9:00 AM–5:00 PM on weekdays, 10:00 AM–2:00 PM on Sat", SetterText.Condition(week));
        Assert.AreEqual("between 10:00 AM and 2:00 PM on Sat", SetterText.Condition(week with { Days = WeekDays.Saturday }), "one day on: its own hours");
        Assert.AreEqual("Never", SetterText.Duration(0));
        Assert.AreEqual("5 min", SetterText.Duration(300));
    }

    [TestMethod]
    public void Status_ShowsWhenARunningTimerEnds()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Setter setter = Setter.Create("Screen on", 100) with
        {
            Changes = [new SettingChange(SettingIds.ScreenOffPluggedIn, new NumberValue(0))],
            Until = [new ElapsedCondition(60)],
        };
        SensorSnapshot now = At.Local(2026, 10, 31, 14, 40);
        RunState running = RunState.Empty.WithRun(new SetterRun(setter.Id, now.UtcNow));

        Assert.AreEqual("On until 3:40 PM", SetterText.Status(setter, running, now));
        Assert.AreEqual("Off · runs for 1 hr", SetterText.Status(setter, RunState.Empty, now));

        RunState forTwoHours = RunState.Empty.WithRun(new SetterRun(setter.Id, now.UtcNow) { TimerEndsUtc = now.UtcNow.AddHours(2) });
        Assert.AreEqual("On until 4:40 PM", SetterText.Status(setter, forTwoHours, now), "a length picked from the tray menu");
    }

    [TestMethod]
    public void TimerMenu_OnlyForClickToStartTimers_ShortestFirst()
    {
        Setter timer = Setter.Create("Awake", 100) with
        {
            Changes = [new SettingChange(SettingIds.Mute, new OnOffValue(true))],
            Until = [new ElapsedCondition(45)],
            TimerChoices = [120, 15, 0, 15, 2000, 60],
        };

        Assert.IsTrue(timer.HasTimer);
        Assert.AreEqual(45, timer.TimerMinutes);
        CollectionAssert.AreEqual(new[] { 15, 60, 120 }, timer.TimerMenu.ToArray());
        Assert.IsFalse((timer with { Start = StartMode.Automatic }).HasTimer, "an automatic setter starts on its conditions");
        Assert.IsFalse((timer with { Until = [new ClockCondition(new TimeOnly(17, 0))] }).HasTimer);
    }

    [TestMethod]
    public void Templates_AreRunnable()
    {
        foreach (SetterTemplates.Template template in SetterTemplates.All)
        {
            Setter first = template.Create(100);
            Setter second = template.Create(100);

            Assert.IsTrue(first.CanRun, template.Name);
            Assert.AreNotEqual(first.Id, second.Id, template.Name);
            Assert.AreNotEqual(template.Definition.Id, first.Id, template.Name);
        }
    }

    [TestMethod]
    public void Templates_FullyDefineSettersAsJson()
    {
        SetterTemplates.Template template = SetterTemplates.Read("""
            {
              "version": 1,
              "templates": [{
                "name": "JSON setter",
                "description": "Everything comes from JSON.",
                "definition": {
                  "id": "template.json",
                  "name": "Defined in JSON",
                  "glyph": "J",
                  "changes": [
                    {"settingId": "mute", "value": {"type": "onOff", "on": true}},
                    {"settingId": "volume", "value": {"type": "number", "value": 35}},
                    {"settingId": "theme.apps", "value": {"type": "choice", "id": "dark"}}
                  ],
                  "start": "Automatic",
                  "enabled": false,
                  "match": "Any",
                  "when": [{"kind": "network", "network": "Office", "not": true}],
                  "until": [
                    {"kind": "elapsed", "minutes": 45, "not": false},
                    {"kind": "clock", "at": "17:30:00", "not": false}
                  ],
                  "putBack": false,
                  "showInFlyout": false,
                  "showTrayIcon": true,
                  "activeLook": {"glyph": "A", "color": "Blue", "dim": false, "bar": true},
                  "inactiveLook": {"glyph": "I", "color": "Purple", "dim": true, "bar": false},
                  "indicator": "VerticalBar",
                  "trayIconId": 1,
                  "timerChoices": [90, 15]
                }
              }]
            }
            """).Single();

        Setter setter = template.Create(77);

        Assert.AreEqual("JSON setter", template.Name);
        Assert.AreEqual("Everything comes from JSON.", template.Description);
        Assert.AreEqual("Defined in JSON", setter.Name);
        Assert.AreEqual("J", setter.Glyph);
        Assert.HasCount(3, setter.Changes);
        Assert.IsTrue(setter.Changes[0].Value is OnOffValue { On: true });
        Assert.IsTrue(setter.Changes[1].Value is NumberValue { Value: 35 });
        Assert.IsTrue(setter.Changes[2].Value is ChoiceValue { Id: "dark" });
        Assert.AreEqual(StartMode.Automatic, setter.Start);
        Assert.IsFalse(setter.Enabled);
        Assert.AreEqual(ConditionMatch.Any, setter.Match);
        Assert.IsTrue(setter.When[0] is NetworkCondition { Network: "Office", Not: true });
        Assert.IsTrue(setter.Until[0] is ElapsedCondition { Minutes: 45 });
        Assert.IsTrue(setter.Until[1] is ClockCondition { At.Hour: 17, At.Minute: 30 });
        Assert.IsFalse(setter.PutBack);
        Assert.IsFalse(setter.ShowInFlyout);
        Assert.IsTrue(setter.ShowTrayIcon);
        Assert.AreEqual(new IconLook { Glyph = "A", Color = IconColor.Blue, Bar = true }, setter.ActiveLook);
        Assert.AreEqual(new IconLook { Glyph = "I", Color = IconColor.Purple, Dim = true }, setter.InactiveLook);
        Assert.AreEqual(DurationIndicator.VerticalBar, setter.Indicator);
        Assert.AreEqual(77u, setter.TrayIconId);
        CollectionAssert.AreEqual(new[] { 90, 15 }, setter.TimerChoices.ToArray());
        Assert.AreEqual(32, setter.Id.Length);
    }

    [TestMethod]
    public void TemplateFile_NewerVersionIsRefused()
    {
        Assert.ThrowsExactly<NotSupportedException>(
            () => SetterTemplates.Read("""{"version": 99, "templates": []}"""));
    }

    [TestMethod]
    public void Import_ValidatesAndAssignsFreshIdentity()
    {
        var volume = FakeSetting.Volume();
        var catalog = new SettingCatalog([volume]);
        Setter source = Setter.Create("Mute for a while", 88) with
        {
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(25))],
            Until = [new ElapsedCondition(45)],
        };

        Setter imported = SetterImport.Read(SetterJson.WriteSetter(source), catalog);
        Assert.IsNull(SetterImport.Problem(imported, catalog));
        Setter copy = imported.NewCopy(101);
        Assert.AreNotEqual(imported.Id, copy.Id);
        Assert.AreEqual(101u, copy.TrayIconId);
        Assert.AreEqual(
            "Changes: Volume 25%.\nStarts: when you click it.\n" +
            "Stops: when you click it again, or for 45 min.\n" +
            "Afterwards: puts each setting back if it has not changed.",
            SetterText.ImportSummary(copy, catalog));
    }

    [TestMethod]
    public void Import_ReadsOneOrManyAndKeepsLegacySingleFiles()
    {
        var catalog = new SettingCatalog([FakeSetting.Volume()]);
        Setter first = Setter.Create("First", 1) with
        {
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(25))],
        };
        Setter second = Setter.Create("Second", 2) with
        {
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(50))],
        };

        IReadOnlyList<Setter> legacy = SetterImport.ReadAll(SetterJson.WriteSetter(first), catalog);
        IReadOnlyList<Setter> single = SetterImport.ReadAll(SetterJson.WriteSetters([first]), catalog);
        IReadOnlyList<Setter> many = SetterImport.ReadAll(SetterJson.WriteSetters([first, second]), catalog);

        Assert.HasCount(1, legacy);
        Assert.HasCount(1, single);
        Assert.HasCount(2, many);
        Assert.AreEqual("First", many[0].Name);
        Assert.AreEqual("Second", many[1].Name);
        Assert.ThrowsExactly<JsonException>(() => SetterImport.Read(SetterJson.WriteSetters([first, second]), catalog));
    }

    [TestMethod]
    public void Import_RejectsEmptyBundlesAndAnyInvalidSetter()
    {
        var catalog = new SettingCatalog([FakeSetting.Volume()]);
        Setter valid = Setter.Create("Valid", 1) with
        {
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(25))],
        };
        Setter invalid = Setter.Create("Invalid", 2) with
        {
            Changes = [new SettingChange("future.setting", new OnOffValue(true))],
        };

        Assert.ThrowsExactly<JsonException>(() => SetterImport.ReadAll(SetterJson.WriteSetters([]), catalog));
        StringAssert.Contains(
            Assert.ThrowsExactly<JsonException>(() => SetterImport.ReadAll(
                SetterJson.WriteSetters([valid, invalid]), catalog)).Message,
            "does not know");
    }

    [TestMethod]
    public void Import_RejectsUnknownSettingsAndStopConditionsUsedToStart()
    {
        var catalog = new SettingCatalog([FakeSetting.Volume()]);
        Setter unknown = Setter.Create("Unknown", 88) with
        {
            Changes = [new SettingChange("future.setting", new OnOffValue(true))],
        };
        Setter badStart = Setter.Create("Nope", 89) with
        {
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(25))],
            Start = StartMode.Automatic,
            When = [new ElapsedCondition(30)],
        };

        StringAssert.Contains(SetterImport.Problem(unknown, catalog)!, "does not know");
        StringAssert.Contains(SetterImport.Problem(badStart, catalog)!, "cannot be used to start");
    }

    [TestMethod]
    public void Cities_ParseSearchAndNearest()
    {
        using var reader = new StreamReader(Path.Combine(AppContext.BaseDirectory, "Data", "world_cities.csv"));
        IReadOnlyList<City> cities = CityCatalog.Parse(reader);

        Assert.IsGreaterThan(7000, cities.Count);
        City milwaukee = CityCatalog.Search(cities, "milwau").First();
        StringAssert.StartsWith(milwaukee.Display, "Milwaukee");
        Assert.AreEqual("Milwaukee", CityCatalog.Nearest(cities, new GeoPoint(43.05, -87.95))!.Name);
        Assert.IsTrue(CityCatalog.Search(cities, "zurich").Any(c => c.Country == "Switzerland"));
    }

    [TestMethod]
    public void TrayIcon_OnHasABarOffIsFaded()
    {
        int size = 24;
        int glyph = SetterIcon.GlyphSize(size);
        byte[] coverage = Enumerable.Repeat((byte)255, glyph * glyph).ToArray();

        uint[] on = SetterIcon.Compose(coverage, size, 0xFFFFFFFF, on: true);
        uint[] off = SetterIcon.Compose(coverage, size, 0xFFFFFFFF, on: false);

        Assert.AreEqual(0xFFFFFFFFu, on[((size - 1) * size) + (size / 2)], "bar under the glyph");
        Assert.AreEqual(0u, off[((size - 1) * size) + (size / 2)]);
        Assert.AreEqual((uint)Math.Round(255 * SetterIcon.OffOpacity), off[size / 2] >> 24, "faded glyph");
        Assert.HasCount(IcoEncoder.ImageOffset + 40 + (size * size * 4) + (4 * size), IcoEncoder.Encode(on, size));
    }

    [TestMethod]
    public void TrayIcon_DimAndBarAreIndependent()
    {
        int size = 24;
        int glyph = SetterIcon.GlyphSize(size);
        byte[] coverage = Enumerable.Repeat((byte)255, glyph * glyph).ToArray();

        uint[] brightNoBar = SetterIcon.Compose(coverage, size, 0xFFE5484D, dim: false, bar: false, DurationIndicator.None, 0);
        uint[] dimBar = SetterIcon.Compose(coverage, size, 0xFFE5484D, dim: true, bar: true, DurationIndicator.None, 0);

        Assert.AreEqual(0xFFE5484Du, brightNoBar[size / 2], "full-strength color");
        Assert.AreEqual(0u, brightNoBar[((size - 1) * size) + (size / 2)], "no bar");
        Assert.AreEqual(0xFFE5484Du, dimBar[((size - 1) * size) + (size / 2)], "bar while dimmed");
        Assert.AreEqual(0x00E5484Du, dimBar[size / 2] & 0x00FFFFFF);
        Assert.IsTrue(dimBar[size / 2] >> 24 < 255);
    }

    [TestMethod]
    public void IconLooks_SurviveSavingAndOldFilesGetDefaults()
    {
        Setter setter = Setter.Create("Looks", 100) with
        {
            InactiveLook = new IconLook { Color = IconColor.Blue, Glyph = Glyphs.Moon, Dim = false },
        };
        Setter back = SetterJson.ReadSetters(SetterJson.WriteSetters([setter]))[0];
        Assert.AreEqual(setter.InactiveLook, back.InactiveLook);

        Setter old = SetterJson.ReadSetters("""{"version":1,"setters":[{"id":"a"}]}""")[0];
        Assert.IsTrue(old.ActiveLook.Bar && !old.ActiveLook.Dim);
        Assert.IsTrue(old.InactiveLook.Dim && !old.InactiveLook.Bar);
    }

    [TestMethod]
    public void RunEnd_IsTheSoonestClockStop()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        SensorSnapshot now = At.Local(2026, 10, 31, 14, 40);
        Setter setter = Setter.Create("Screen on", 100) with
        {
            Changes = [new SettingChange(SettingIds.ScreenOffPluggedIn, new NumberValue(0))],
            Until = [new ElapsedCondition(60), new ClockCondition(new TimeOnly(15, 0)), new CpuQuietCondition(10, 5)],
        };
        var run = new SetterRun(setter.Id, now.UtcNow);

        Assert.AreEqual(now.UtcNow.AddMinutes(20), run.EndsAtUtc(setter, now), "3:00 PM comes before an hour is up");
        Assert.AreEqual("On until 3:00 PM", SetterText.Status(setter, RunState.Empty.WithRun(run), now));
        Assert.AreEqual(now.UtcNow.AddMinutes(60), run.EndsAtUtc(setter with { Until = [setter.Until[0]] }, now));
        Assert.IsNull(run.EndsAtUtc(setter with { Until = [new CpuQuietCondition(10, 5)] }, now), "only a sensor can stop it");

        var picked = new SetterRun(setter.Id, now.UtcNow) { TimerEndsUtc = now.UtcNow.AddMinutes(10) };
        Assert.AreEqual(now.UtcNow.AddMinutes(10), picked.EndsAtUtc(setter, now), "a length picked from the tray menu");
    }

    [TestMethod]
    public void TimeLeft_FullAtTheStart_StepsDown_EmptyAtTheEnd()
    {
        var start = new DateTime(2026, 10, 31, 14, 0, 0, DateTimeKind.Utc);
        DateTime end = start.AddMinutes(10);

        TimeLeft first = SetterIcon.Measure(start, end, start, 20);
        Assert.AreEqual(1.0, first.Share);
        Assert.AreEqual(start.AddSeconds(30), first.NextChangeUtc, "a 10 min run in 20 steps drops one every 30 sec");

        TimeLeft half = SetterIcon.Measure(start, end, start.AddMinutes(5).AddSeconds(10), 20);
        Assert.AreEqual(0.5, half.Share, "rounded up, so the last step still shows");
        Assert.AreEqual(start.AddMinutes(5).AddSeconds(30), half.NextChangeUtc);

        TimeLeft last = SetterIcon.Measure(start, end, end.AddSeconds(-1), 20);
        Assert.AreEqual(0.05, last.Share, 1e-9);
        Assert.AreEqual(end, last.NextChangeUtc);

        Assert.AreEqual(new TimeLeft(0, null), SetterIcon.Measure(start, end, end, 20));
        Assert.AreEqual(new TimeLeft(0, null), SetterIcon.Measure(end, end, start, 20), "no length");
    }

    [TestMethod]
    public void TrayIcon_IndicatorsShowTheShareLeft()
    {
        const uint White = 0xFFFFFFFF;
        uint track = (uint)Math.Round(255 * SetterIcon.TrackOpacity);
        int size = 24;
        int width = SetterIcon.IndicatorWidth(size);
        int Index(int x, int y) => (y * size) + x;
        uint[] Draw(DurationIndicator indicator, double share)
        {
            int glyph = SetterIcon.GlyphSize(size, indicator);
            return SetterIcon.Compose(new byte[glyph * glyph], size, White, on: true, indicator, share);
        }

        uint[] across = Draw(DurationIndicator.HorizontalBar, 0.5);
        Assert.AreEqual(White, across[Index(0, size - 1)], "full from the left");
        Assert.AreEqual(track, across[Index(size - 1, size - 1)] >> 24, "the used part is a faint track");
        Assert.AreEqual(0u, across[Index(size / 2, size - width - 1)], "a gap above the bar");

        uint[] up = Draw(DurationIndicator.VerticalBar, 0.5);
        Assert.AreEqual(White, up[Index(size - 1, size - 1)], "full from the bottom");
        Assert.AreEqual(track, up[Index(size - 1, 0)] >> 24);

        uint[] donut = Draw(DurationIndicator.Donut, 0.25);
        int mid = size / 2;
        Assert.AreEqual(White, donut[Index(mid, 0)], "12 o'clock");
        Assert.AreEqual(White, donut[Index(mid + 7, mid - 8)], "half past 1 o'clock, in the first quarter");
        Assert.AreEqual(track, donut[Index(0, mid)] >> 24, "9 o'clock is used up");
        Assert.AreEqual(0u, donut[Index(mid, mid)], "no ring in the middle");
        Assert.AreEqual(0u, donut[Index(0, 0)], "nor in the corners");

        Assert.IsTrue(SetterIcon.GlyphSize(size, DurationIndicator.Donut) < SetterIcon.GlyphSize(size), "the glyph shrinks inside the ring");
        Assert.AreEqual(0, (size - SetterIcon.GlyphSize(size, DurationIndicator.Donut)) % 2, "and sits on whole pixels");
    }
}
