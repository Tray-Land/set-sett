using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Tests;

[TestClass]
public sealed class ClaimLedgerTests
{
    private const string Theme = SettingIds.AppsTheme;
    private static readonly ChoiceValue Light = new(SettingIds.Light);
    private static readonly ChoiceValue Dark = new(SettingIds.Dark);

    [TestMethod]
    public void Release_PutsBackTheSnapshot_WhenUnchanged()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, Theme, "a", Light, Dark);

        var (after, putBack) = ClaimLedger.Release(state, Theme, "a", Dark);

        Assert.AreEqual(Light, putBack);
        Assert.IsEmpty(after.ClaimsOn(Theme));
    }

    [TestMethod]
    public void Release_VoidsThePutBack_WhenTheUserChangedIt()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, Theme, "a", Light, Dark);

        var (after, putBack) = ClaimLedger.Release(state, Theme, "a", Light);

        Assert.IsNull(putBack);
        Assert.IsEmpty(after.ClaimsOn(Theme));
    }

    [TestMethod]
    public void StackedClaims_LastToStopPutsBackTheOriginal()
    {
        // A sets 60, B sets 20 on top. A stops first: nothing changes, B inherits A's snapshot.
        RunState state = ClaimLedger.Apply(RunState.Empty, SettingIds.Volume, "a", new NumberValue(40), new NumberValue(60));
        state = ClaimLedger.Apply(state, SettingIds.Volume, "b", new NumberValue(60), new NumberValue(20));

        (state, SettingValue? first) = ClaimLedger.Release(state, SettingIds.Volume, "a", new NumberValue(20));
        (state, SettingValue? second) = ClaimLedger.Release(state, SettingIds.Volume, "b", new NumberValue(20));

        Assert.IsNull(first);
        Assert.AreEqual(new NumberValue(40), second);
        Assert.IsEmpty(state.ClaimsOn(SettingIds.Volume));
    }

    [TestMethod]
    public void StackedClaims_TopStoppingFirstRestoresTheOneBelow()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, Theme, "a", Light, Dark);
        state = ClaimLedger.Apply(state, Theme, "b", Dark, Light);

        (state, SettingValue? b) = ClaimLedger.Release(state, Theme, "b", Light);
        (state, SettingValue? a) = ClaimLedger.Release(state, Theme, "a", Dark);

        Assert.AreEqual(Dark, b);
        Assert.AreEqual(Light, a);
    }

    [TestMethod]
    public void Apply_VoidsOlderClaims_WhenTheSettingWasChangedBetween()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, Theme, "a", Light, Dark);

        // The user went back to light by hand, then B sets dark: A's claim is stale.
        state = ClaimLedger.Apply(state, Theme, "b", Light, Dark);

        Assert.HasCount(1, state.ClaimsOn(Theme));
        Assert.AreEqual("b", state.ClaimsOn(Theme)[0].SetterId);
    }

    [TestMethod]
    public void Apply_WithUnreadableCurrent_RecordsNothing()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, Theme, "a", current: null, Dark);

        Assert.IsEmpty(state.ClaimsOn(Theme));
    }

    [TestMethod]
    public void Apply_AgainOnTop_KeepsTheOriginalSnapshot()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, Theme, "a", Light, Dark);
        state = ClaimLedger.Apply(state, Theme, "a", Dark, Dark);

        Assert.AreEqual(Light, state.ClaimsOn(Theme)[0].Snapshot);
    }

    [TestMethod]
    public void NumberValues_AllowOneStepOfSlack()
    {
        RunState state = ClaimLedger.Apply(RunState.Empty, SettingIds.Volume, "a", new NumberValue(40), new NumberValue(60));

        var (_, putBack) = ClaimLedger.Release(state, SettingIds.Volume, "a", new NumberValue(59));

        Assert.AreEqual(new NumberValue(40), putBack);
    }
}
