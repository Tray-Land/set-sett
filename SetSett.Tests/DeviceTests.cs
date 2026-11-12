using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Tests;

[TestClass]
public sealed class DeviceTests
{
    [TestMethod]
    public void Merge_CombinesSourcesWithoutLosingAudioOrPeripheralFlags()
    {
        IReadOnlyList<ConnectedDevice> devices = DeviceNames.Merge(
        [
            new(" USB headset ", false),
            new("usb HEADSET", true, true),
            new("Dock", true),
            new("Internal controller", false),
            new(" ", true),
        ]);

        Assert.HasCount(3, devices);
        ConnectedDevice headset = devices.Single(d => d.Name == "USB headset");
        Assert.IsTrue(headset.Peripheral);
        Assert.IsTrue(headset.Audio);
        CollectionAssert.AreEqual(new[] { "Dock", "Internal controller", "USB headset" }, devices.Select(d => d.Name).ToArray());
    }

    [TestMethod]
    public void Choices_HidesBasicHardwareButAlwaysOffersAudio()
    {
        ConnectedDevice[] devices =
        [
            new("Bluetooth mouse", true),
            new("Dock", true),
            new("Internal controller", false),
            new("Speakers", false, true),
            new("USB headset", true, true),
        ];

        CollectionAssert.AreEqual(new[] { "Bluetooth mouse", "Dock", "Speakers", "USB headset" },
            DeviceNames.Choices(devices, DeviceFilter.Peripherals).ToArray());
        CollectionAssert.AreEqual(new[] { "Speakers", "USB headset" },
            DeviceNames.Choices(devices, DeviceFilter.Audio).ToArray());
        CollectionAssert.AreEqual(devices.Select(d => d.Name).ToArray(),
            DeviceNames.Choices(devices, DeviceFilter.All).ToArray());
        Assert.IsEmpty(DeviceNames.Choices([], DeviceFilter.Peripherals));
    }

    [TestMethod]
    [DataRow("4d36e97d-e325-11ce-bfc1-08002be10318")]
    [DataRow("4d36e966-e325-11ce-bfc1-08002be10318")]
    [DataRow("50127dc3-0f36-415e-a6cc-4cb3be910b65")]
    [DataRow("36fc9e60-c465-11cf-8056-444553540000")]
    [DataRow("5c4c3332-344d-483c-8739-259e934c9cc8")]
    public void Peripherals_ExcludesInfrastructureEvenOnAnExternalBus(string deviceClass)
    {
        Assert.IsFalse(DeviceNames.IsPeripheral(true, Guid.Parse(deviceClass)));
    }

    [TestMethod]
    public void Peripherals_UsesWindowsContainerMembershipNotJustUsbOrBluetooth()
    {
        Guid monitor = new("4d36e96e-e325-11ce-bfc1-08002be10318");
        Assert.IsTrue(DeviceNames.IsPeripheral(true, monitor));
        Assert.IsFalse(DeviceNames.IsPeripheral(false, monitor));
        Assert.IsTrue(DeviceNames.IsPeripheral(true, Guid.Empty), "unknown external classes remain available");
        Assert.IsFalse(DeviceNames.IsPeripheral(true, monitor, hasContainer: true), "offer its container instead of duplicate functions");
    }

    [TestMethod]
    public void Audio_OnlyTheEndpointWatcherMayReportAudioDevnodes()
    {
        Assert.IsFalse(DeviceNames.IsPlugAndPlayDevice(new Guid("c166523c-fe0c-4a94-a586-f1a80cfbbf3e"), "SWD\\MMDEVAPI\\endpoint", true),
            "an unplugged audio endpoint can still be a present PnP device");
        Assert.IsTrue(DeviceNames.IsPlugAndPlayDevice(new Guid("4d36e96c-e325-11ce-bfc1-08002be10318"), "USB\\audio", null),
            "physical audio adapters are still selectable devices");
    }

    [TestMethod]
    [DataRow("BTHENUM\\headset")]
    [DataRow("BTHLEDEVICE\\keyboard")]
    [DataRow("BTHHFENUM\\handsfree")]
    [DataRow("bthenum\\headset")]
    public void PairedBluetoothNode_OnlyCountsWhenItsContainerIsConnected(string instanceId)
    {
        Guid bluetoothClass = new("e0cbf06c-cd8b-4647-bb8a-263b43f0f974");
        Assert.IsFalse(DeviceNames.IsPlugAndPlayDevice(bluetoothClass, instanceId, false));
        Assert.IsFalse(DeviceNames.IsPlugAndPlayDevice(bluetoothClass, instanceId, null));
        Assert.IsTrue(DeviceNames.IsPlugAndPlayDevice(bluetoothClass, instanceId, true));
    }

    [TestMethod]
    [DataRow("USB\\headset")]
    [DataRow("DISPLAY\\monitor")]
    [DataRow("PCI\\adapter")]
    public void PresentWiredDevice_DoesNotRequireAConnectedContainer(string instanceId)
    {
        Assert.IsTrue(DeviceNames.IsPlugAndPlayDevice(Guid.Empty, instanceId, false));
        Assert.IsTrue(DeviceNames.IsPlugAndPlayDevice(Guid.Empty, instanceId, null));
    }

    [TestMethod]
    [DataRow("USB headset")]
    [DataRow("Dock")]
    [DataRow("Speakers")]
    [DataRow("Bluetooth mouse")]
    [DataRow("Internal controller")]
    public void Condition_MatchesEverySourceRegardlessOfPickerFilter(string name)
    {
        var condition = new DeviceCondition($" {name.ToUpperInvariant()} ");
        var connected = new SensorSnapshot(DateTime.UtcNow) { Devices = [name], DeviceChoices = [] };

        Assert.IsTrue(condition.Evaluate(new(connected)));
        Assert.IsFalse((condition with { Not = true }).Evaluate(new(connected)));
        Assert.IsFalse(condition.Evaluate(new(connected with { Devices = [] })));
        Assert.IsTrue((condition with { Not = true }).Evaluate(new(connected with { Devices = [] })));
        Assert.IsNull(condition.Evaluate(new(connected with { Devices = null })));
        Assert.IsNull((condition with { Not = true }).Evaluate(new(connected with { Devices = null })));
    }

    [TestMethod]
    public async Task Device_ConnectStartsAndDisconnectPutsBack_UnknownDoesNeither()
    {
        var volume = FakeSetting.Volume(40);
        var engine = new SetterEngine(new SettingCatalog([volume]), new MemoryStore());
        Setter setter = Setter.Create("Headset volume", 100) with
        {
            Start = StartMode.Automatic,
            When = [new DeviceCondition("USB headset")],
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(20))],
        };
        var now = new SensorSnapshot(DateTime.UtcNow);
        await engine.StepAsync([setter], now);
        Assert.AreEqual(0, volume.Writes);

        await engine.StepAsync([setter], now with { Devices = ["USB headset"] });
        Assert.IsTrue(engine.State.IsActive(setter.Id));
        Assert.AreEqual(new NumberValue(20), volume.Value);

        await engine.StepAsync([setter], now);
        Assert.IsTrue(engine.State.IsActive(setter.Id), "a sensor failure is not an unplug");
        Assert.AreEqual(1, volume.Writes);

        await engine.StepAsync([setter], now with { Devices = [] });
        Assert.IsFalse(engine.State.IsActive(setter.Id));
        Assert.AreEqual(new NumberValue(40), volume.Value);
    }
}