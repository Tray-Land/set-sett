namespace SetSett.Model;

/// <summary>A connected device's name and the picker groups it belongs to.</summary>
public sealed record ConnectedDevice(string Name, bool Peripheral, bool Audio = false);

public enum DeviceFilter
{
    Peripherals,
    Audio,
    All,
}

public static class DeviceNames
{
    private static readonly Guid AudioEndpointClass = new("c166523c-fe0c-4a94-a586-f1a80cfbbf3e");
    private static readonly HashSet<Guid> HardwareClasses =
    [
        new("4d36e97d-e325-11ce-bfc1-08002be10318"), // System
        new("4d36e966-e325-11ce-bfc1-08002be10318"), // Computer
        new("50127dc3-0f36-415e-a6cc-4cb3be910b65"), // Processor
        new("36fc9e60-c465-11cf-8056-444553540000"), // USB controllers / hubs
        new("5c4c3332-344d-483c-8739-259e934c9cc8"), // Software device
    ];

    /// <summary>Merge the names reported by containers, plug-and-play, and audio endpoints.</summary>
    public static IReadOnlyList<ConnectedDevice> Merge(IEnumerable<ConnectedDevice> devices) => devices
        .Where(d => !string.IsNullOrWhiteSpace(d.Name))
        .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
        .Select(g => new ConnectedDevice(g.Key, g.Any(d => d.Peripheral), g.Any(d => d.Audio)))
        .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>Filters affect suggestions only, never whether a saved condition can match.</summary>
    public static IReadOnlyList<string> Choices(IEnumerable<ConnectedDevice> devices, DeviceFilter filter) => devices
        .Where(d => filter switch
        {
            DeviceFilter.Peripherals => d.Peripheral || d.Audio,
            DeviceFilter.Audio => d.Audio,
            DeviceFilter.All => true,
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        })
        .Select(d => d.Name)
        .ToList();

    /// <summary>Show a container once, rather than all of its low-level functions as separate peripherals.</summary>
    public static bool IsPeripheral(bool external, Guid deviceClass, bool hasContainer = false) =>
        external && !hasContainer && !HardwareClasses.Contains(deviceClass);

    /// <summary>Audio and paired Bluetooth devnodes can stay present after disconnecting.</summary>
    public static bool IsPlugAndPlayDevice(Guid deviceClass, string instanceId, bool? containerConnected)
    {
        bool bluetooth = instanceId.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
            instanceId.StartsWith("BTHLE", StringComparison.OrdinalIgnoreCase) ||
            instanceId.StartsWith("BTHHFENUM\\", StringComparison.OrdinalIgnoreCase);
        return deviceClass != AudioEndpointClass && (!bluetooth || containerConnected == true);
    }
}