using SetSett.Engine;
using SetSett.Model;
using SetSett.Text;

namespace SetSett.Tests;

[TestClass]
public sealed class SettingFamilyTests
{
    private const string Screen = "screen.off";

    private static FakeSetting Timeout(string id, string scope, int seconds, string family = Screen) => new(
        new SettingInfo(id, "Screen off after", "Power", Glyphs.Monitor, ValueKind.Duration)
        {
            Durations = SettingIds.TimeoutChoices,
            Family = family,
            Scope = scope,
        },
        new NumberValue(seconds));

    private static (SettingCatalog Catalog, FakeSetting Ac, FakeSetting Dc) Catalog(int ac = 300, int dc = 120)
    {
        FakeSetting plugged = Timeout(SettingIds.ScreenOffPluggedIn, "Plugged in", ac);
        FakeSetting battery = Timeout(SettingIds.ScreenOffOnBattery, "On battery", dc);
        return (new SettingCatalog([plugged, battery, FakeSetting.Volume()]), plugged, battery);
    }

    private static SettingChange Ac(int seconds) => new(SettingIds.ScreenOffPluggedIn, new NumberValue(seconds));

    private static SettingChange Dc(int seconds) => new(SettingIds.ScreenOffOnBattery, new NumberValue(seconds));

    [TestMethod]
    public void EqualValuesForEveryMember_ShowAsOneAlwaysLine()
    {
        var (catalog, _, _) = Catalog();
        IReadOnlyList<ChangeLine> lines = SettingFamilies.Lines([Ac(0), new SettingChange(SettingIds.Volume, new NumberValue(10)), Dc(0)], catalog);

        Assert.AreEqual(2, lines.Count);
        CollectionAssert.AreEqual(new[] { 0, 2 }, lines[0].Indexes.ToArray());
        Assert.IsTrue(lines[0].IsAlways(2));
        Assert.IsNull(lines[1].Family);
    }

    [TestMethod]
    public void DifferentValues_StaySeparateLines()
    {
        var (catalog, _, _) = Catalog();
        IReadOnlyList<ChangeLine> lines = SettingFamilies.Lines([Ac(0), Dc(300)], catalog);

        Assert.AreEqual(2, lines.Count);
        Assert.IsFalse(lines[0].IsAlways(2));
        CollectionAssert.AreEqual(new[] { SettingIds.ScreenOffPluggedIn }, lines[0].Members.ToArray());
    }

    [TestMethod]
    public void OneMemberAlone_IsNotAlways()
    {
        var (catalog, _, _) = Catalog();
        IReadOnlyList<ChangeLine> lines = SettingFamilies.Lines([Dc(60)], catalog);

        Assert.AreEqual(1, lines.Count);
        Assert.IsFalse(lines[0].IsAlways(2));
    }

    [TestMethod]
    public void Rescope_FromAlwaysToOneSource_KeepsValueAndPlace()
    {
        var (catalog, _, _) = Catalog();
        SettingChange volume = new(SettingIds.Volume, new NumberValue(10));
        SettingChange[] changes = [volume, Ac(0), Dc(0)];
        ChangeLine always = SettingFamilies.Lines(changes, catalog)[1];

        IReadOnlyList<SettingChange> result = SettingFamilies.Rescope(changes, always, [SettingIds.ScreenOffOnBattery]);

        CollectionAssert.AreEqual(new[] { volume, Dc(0) }, result.ToArray());
    }

    [TestMethod]
    public void Rescope_FromOneSourceToAlways_AddsEveryMember()
    {
        var (catalog, _, _) = Catalog();
        SettingChange[] changes = [Ac(600)];
        ChangeLine line = SettingFamilies.Lines(changes, catalog)[0];

        IReadOnlyList<SettingChange> result = SettingFamilies.Rescope(changes, line, SettingFamilies.Members(catalog, Screen));

        CollectionAssert.AreEqual(new[] { Ac(600), Dc(600) }, result.ToArray());
    }

    [TestMethod]
    public void FreeMembers_ExcludeWhatOtherLinesUse()
    {
        var (catalog, _, _) = Catalog();
        SettingChange[] changes = [Ac(0), Dc(300)];
        ChangeLine plugged = SettingFamilies.Lines(changes, catalog)[0];

        CollectionAssert.AreEqual(new[] { SettingIds.ScreenOffPluggedIn }, SettingFamilies.FreeMembers(changes, catalog, plugged).ToArray());
    }

    [TestMethod]
    public void Add_FamilyWithNothingUsed_AddsAlways_ThenFillsWhatIsLeft()
    {
        var (catalog, ac, _) = Catalog();

        IReadOnlyList<SettingChange> first = SettingFamilies.Add([], catalog, ac);
        Assert.AreEqual(2, first.Count);
        Assert.IsTrue(SettingFamilies.Lines(first, catalog)[0].IsAlways(2));
        Assert.IsFalse(SettingFamilies.HasFreeMember(first, catalog, Screen));

        IReadOnlyList<SettingChange> rest = SettingFamilies.Add([Ac(0)], catalog, ac);
        CollectionAssert.AreEqual(new[] { SettingIds.ScreenOffPluggedIn, SettingIds.ScreenOffOnBattery }, rest.Select(c => c.SettingId).ToArray());
    }

    [TestMethod]
    public void Revalue_ChangesEveryChangeOnTheLine()
    {
        var (catalog, _, _) = Catalog();
        SettingChange[] changes = [Ac(0), Dc(0)];
        ChangeLine always = SettingFamilies.Lines(changes, catalog)[0];

        IReadOnlyList<SettingChange> result = SettingFamilies.Revalue(changes, always, new NumberValue(900));

        CollectionAssert.AreEqual(new[] { Ac(900), Dc(900) }, result.ToArray());
    }

    [TestMethod]
    public void Text_AlwaysReadsAsTheSettingAlone_OneSourceNamesIt()
    {
        var (catalog, _, _) = Catalog();
        Setter always = Setter.Create("x", 1) with { Changes = [Ac(0), Dc(0)] };
        Setter battery = Setter.Create("y", 2) with { Changes = [Dc(60)] };

        Assert.AreEqual("Screen off after Never", SetterText.Changes(always, catalog));
        Assert.AreEqual("Screen off after (on battery) 1 min", SetterText.Changes(battery, catalog));
    }

    [TestMethod]
    public async Task Always_PutsBackEachSourcesOwnOriginalValue()
    {
        var (catalog, ac, dc) = Catalog(ac: 300, dc: 120);
        var engine = new SetterEngine(catalog, new MemoryStore());
        Setter setter = Setter.Create("stay on", 1) with { Changes = [Ac(0), Dc(0)] };
        SensorSnapshot now = At.Local(2026, 1, 5, 9);

        await engine.StartAsync(setter, now);
        Assert.AreEqual(new NumberValue(0), ac.Value);
        Assert.AreEqual(new NumberValue(0), dc.Value);

        await engine.StopAsync(setter.Id, StopReason.User);
        Assert.AreEqual(new NumberValue(300), ac.Value);
        Assert.AreEqual(new NumberValue(120), dc.Value);
    }

    [TestMethod]
    public async Task OnBatteryOnly_LeavesPluggedInAlone()
    {
        var (catalog, ac, dc) = Catalog(ac: 300, dc: 120);
        var engine = new SetterEngine(catalog, new MemoryStore());
        Setter setter = Setter.Create("battery", 1) with { Changes = [Dc(30)] };

        await engine.StartAsync(setter, At.Local(2026, 1, 5, 9));

        Assert.AreEqual(new NumberValue(300), ac.Value);
        Assert.AreEqual(new NumberValue(30), dc.Value);
    }
}
