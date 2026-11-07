using Microsoft.Windows.System.Power;
using SetSett.Diagnostics;
using SetSett.Model;
using Windows.Devices.Enumeration;
using Windows.Networking.Connectivity;

namespace SetSett.Sensing;

// Event-driven sensors, from traydog's Situations. Each starts in its constructor, stops in Dispose,
// and raises Changed on a background thread; SetterHost folds bursts into one evaluation.

/// <summary>
/// The names of the networks the PC is connected to (Wi-Fi SSIDs, and the names Windows gives wired
/// networks), re-read on NetworkStatusChanged.
/// </summary>
internal sealed class NetworkSensor : IDisposable
{
    public NetworkSensor()
    {
        Networks = ReadNames();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    public event Action? Changed;

    public IReadOnlyList<string> Networks { get; private set; }

    /// <summary>Every connected network's name, the one with internet access first.</summary>
    public static IReadOnlyList<string> ReadNames()
    {
        try
        {
            ConnectionProfile? internet = NetworkInformation.GetInternetConnectionProfile();
            var profiles = NetworkInformation.GetConnectionProfiles()
                .OrderByDescending(p => p.ProfileName == internet?.ProfileName);

            var names = new List<string>();
            foreach (ConnectionProfile profile in profiles)
            {
                if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                {
                    continue;
                }

                Add(profile.ProfileName);
                if (profile.IsWlanConnectionProfile)
                {
                    Add(profile.WlanConnectionProfileDetails?.GetConnectedSsid());
                }
            }

            return names;

            void Add(string? name)
            {
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Sensors", "Could not read network names", ex);
            return [];
        }
    }

    public void Dispose() => NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;

    private void OnNetworkStatusChanged(object sender)
    {
        Networks = ReadNames();
        Changed?.Invoke();
    }
}

/// <summary>
/// External devices (Bluetooth, USB, monitors, docks) and whether each is connected: a DeviceWatcher
/// over device containers, the same list as Settings > Bluetooth &amp; devices.
/// </summary>
internal sealed class DeviceSensor : IDisposable
{
    private const string ExternalDevices = "System.Devices.LocalMachine:=System.StructuredQueryType.Boolean#False";
    private const string ConnectedProperty = "System.Devices.Connected";

    private readonly DeviceWatcher _watcher;
    private readonly Dictionary<string, (string Name, bool Connected)> _containers = [];
    private readonly Lock _lock = new();
    private bool _enumerated;

    public DeviceSensor()
    {
        // A List, not an array: CsWinRT marshals it as IIterable<string> in trimmed builds too.
        _watcher = DeviceInformation.CreateWatcher(ExternalDevices, new List<string> { ConnectedProperty }, DeviceInformationKind.DeviceContainer);
        _watcher.Added += OnAdded;
        _watcher.Updated += OnUpdated;
        _watcher.Removed += OnRemoved;
        _watcher.EnumerationCompleted += OnEnumerationCompleted;
        _watcher.Start();
    }

    public event Action? Changed;

    /// <summary>Names of the connected devices; null until the first enumeration completes.</summary>
    public IReadOnlyList<string>? Devices { get; private set; }

    public void Dispose()
    {
        _watcher.Added -= OnAdded;
        _watcher.Updated -= OnUpdated;
        _watcher.Removed -= OnRemoved;
        _watcher.EnumerationCompleted -= OnEnumerationCompleted;
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            _watcher.Stop();
        }
    }

    private static bool IsConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(ConnectedProperty, out object? value) && value is true;

    // Windows also lists a few software endpoints as containers ("Windows.Devices.Sms.SmsDevice").
    private static bool IsUserFacing(string name) =>
        !string.IsNullOrWhiteSpace(name) && !name.StartsWith("Windows.", StringComparison.Ordinal);

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        lock (_lock)
        {
            _containers[info.Id] = (info.Name, IsConnected(info.Properties));
        }

        Publish();
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_lock)
        {
            if (!_containers.TryGetValue(update.Id, out var container) || !update.Properties.ContainsKey(ConnectedProperty))
            {
                return;
            }

            _containers[update.Id] = container with { Connected = IsConnected(update.Properties) };
        }

        Publish();
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_lock)
        {
            _containers.Remove(update.Id);
        }

        Publish();
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        _enumerated = true;
        Publish();
    }

    // The watcher reports each container on its own during enumeration; wait for the full list.
    private void Publish()
    {
        if (!_enumerated)
        {
            return;
        }

        List<string> devices;
        lock (_lock)
        {
            devices = _containers.Values
                .Where(c => c.Connected && IsUserFacing(c.Name))
                .Select(c => c.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        if (Devices is { } current && current.SequenceEqual(devices))
        {
            return;
        }

        Devices = devices;
        Changed?.Invoke();
    }
}

/// <summary>Battery charge and whether the PC is plugged in, from the Windows App SDK power events.</summary>
internal sealed class PowerSensor : IDisposable
{
    public PowerSensor()
    {
        Power = Read();
        PowerManager.RemainingChargePercentChanged += OnPowerChanged;
        PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
        PowerManager.BatteryStatusChanged += OnPowerChanged;
    }

    public event Action? Changed;

    public PowerState? Power { get; private set; }

    public void Dispose()
    {
        PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
        PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
        PowerManager.BatteryStatusChanged -= OnPowerChanged;
    }

    private static PowerState? Read()
    {
        try
        {
            bool hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
            return new PowerState(
                hasBattery ? PowerManager.RemainingChargePercent : null,
                PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent);
        }
        catch (Exception ex)
        {
            Log.Warn("Sensors", "Could not read power state", ex);
            return null;
        }
    }

    private void OnPowerChanged(object? sender, object e)
    {
        PowerState? power = Read();
        if (power == Power)
        {
            return;
        }

        Power = power;
        Changed?.Invoke();
    }
}
