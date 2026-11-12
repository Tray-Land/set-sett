using Microsoft.Windows.System.Power;
using SetSett.Diagnostics;
using SetSett.Model;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
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
/// Connected containers, present plug-and-play devices, and active playback / recording endpoints.
/// Audio selectors track jack state too, when the driver supports jack-presence detection.
/// </summary>
internal sealed class DeviceSensor : IDisposable
{
    private const string ExternalDevices = "System.Devices.LocalMachine:=System.StructuredQueryType.Boolean#False";
    private const string ConnectedProperty = "System.Devices.Connected";
    private const string PresentDevices = "System.Devices.Present:=System.StructuredQueryType.Boolean#True";
    private const string LocalProperty = "System.Devices.InLocalMachineContainer";
    private const string ClassProperty = "System.Devices.ClassGuid";
    private const string ContainerProperty = "System.Devices.ContainerId";

    private readonly Dictionary<DeviceWatcher, Dictionary<string, DeviceInformation>> _sources = [];
    private readonly HashSet<DeviceWatcher> _enumerated = [];
    private readonly Lock _lock = new();
    private readonly DeviceWatcher _containers;
    private readonly DeviceWatcher _plugAndPlay;
    private bool _failed;
    private bool _disposed;

    public DeviceSensor()
    {
        try
        {
            // Lists cross into WinRT, including in trimmed builds.
            _containers = Add(DeviceInformation.CreateWatcher(ExternalDevices,
                new List<string> { ConnectedProperty }, DeviceInformationKind.DeviceContainer));
            _plugAndPlay = Add(DeviceInformation.CreateWatcher(PresentDevices,
                new List<string> { LocalProperty, ClassProperty, ContainerProperty }, DeviceInformationKind.Device));
            Add(DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector()));
            Add(DeviceInformation.CreateWatcher(MediaDevice.GetAudioCaptureSelector()));
            foreach (DeviceWatcher watcher in _sources.Keys)
            {
                watcher.Start();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public event Action? Changed;

    /// <summary>Null until every source finishes enumerating, or if a watcher fails.</summary>
    public IReadOnlyList<ConnectedDevice>? Devices { get; private set; }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        foreach (DeviceWatcher watcher in _sources.Keys)
        {
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            watcher.EnumerationCompleted -= OnEnumerationCompleted;
            watcher.Stopped -= OnStopped;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
    }

    private DeviceWatcher Add(DeviceWatcher watcher)
    {
        _sources.Add(watcher, []);
        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        watcher.EnumerationCompleted += OnEnumerationCompleted;
        watcher.Stopped += OnStopped;
        return watcher;
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info)
    {
        bool changed;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _sources[sender][info.Id] = info;
            changed = Publish();
        }

        if (changed) Changed?.Invoke();
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        bool changed;
        lock (_lock)
        {
            if (_disposed || !_sources[sender].TryGetValue(update.Id, out DeviceInformation? info))
            {
                return;
            }

            info.Update(update);
            changed = Publish();
        }

        if (changed) Changed?.Invoke();
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        bool changed;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _sources[sender].Remove(update.Id);
            changed = Publish();
        }

        if (changed) Changed?.Invoke();
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        bool changed;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _enumerated.Add(sender);
            changed = Publish();
        }

        if (changed) Changed?.Invoke();
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _failed = true;
            Devices = null;
        }

        Log.Error("Sensors", $"Device watcher stopped unexpectedly ({sender.Status}); device conditions are unknown");
        Changed?.Invoke();
    }

    // Called with the lock held. Never publish a partial initial list as a disconnection.
    private bool Publish()
    {
        if (_failed || _enumerated.Count != _sources.Count)
        {
            return false;
        }

        Dictionary<Guid, bool> connections = _sources[_containers].Values
            .Where(info => Guid.TryParse(info.Id, out _))
            .ToDictionary(info => Guid.Parse(info.Id), info =>
                info.Properties.TryGetValue(ConnectedProperty, out object? connected) && connected is true);
        IReadOnlyList<ConnectedDevice> devices = DeviceNames.Merge(_sources.SelectMany(source => source.Value.Values
            .Where(info => source.Key == _containers
                ? info.Properties.TryGetValue(ConnectedProperty, out object? connected) && connected is true
                : source.Key != _plugAndPlay || DeviceNames.IsPlugAndPlayDevice(DeviceClass(info), info.Id,
                    info.Properties.TryGetValue(ContainerProperty, out object? id) && id is Guid container &&
                    connections.TryGetValue(container, out bool containerConnected) ? containerConnected : null))
            .Select(info => Describe(source.Key, info, connections))));
        if (Devices is { } current && current.SequenceEqual(devices))
        {
            return false;
        }

        Devices = devices;
        return true;
    }

    private ConnectedDevice Describe(DeviceWatcher source, DeviceInformation info, IReadOnlyDictionary<Guid, bool> connections)
    {
        bool audio = source != _containers && source != _plugAndPlay;
        bool peripheral = source == _containers;
        if (source == _plugAndPlay)
        {
            bool external = info.Properties.TryGetValue(LocalProperty, out object? local) && local is false;
            bool hasContainer = info.Properties.TryGetValue(ContainerProperty, out object? id) && id is Guid container &&
                connections.TryGetValue(container, out bool connected) && connected;
            peripheral = DeviceNames.IsPeripheral(external, DeviceClass(info), hasContainer);
        }

        return new ConnectedDevice(info.Name,
            peripheral && !info.Name.StartsWith("Windows.", StringComparison.Ordinal), audio);
    }

    private static Guid DeviceClass(DeviceInformation info) =>
        info.Properties.TryGetValue(ClassProperty, out object? value) && value is Guid guid ? guid : Guid.Empty;
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
