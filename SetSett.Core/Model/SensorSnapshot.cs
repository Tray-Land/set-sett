namespace SetSett.Model;

public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>Where the user is, for sun times: picked from the city list or from one location fix.</summary>
public sealed record SavedPlace(string Name, double Latitude, double Longitude)
{
    public GeoPoint Point => new(Latitude, Longitude);
}

/// <summary>BatteryPercent is null on a PC without a battery.</summary>
public sealed record PowerState(int? BatteryPercent, bool PluggedIn);

public readonly record struct CpuSample(DateTime AtUtc, double Percent);

/// <summary>
/// The sensors a condition reads, so the app runs only the ones some watched condition needs.
/// </summary>
[Flags]
public enum Sensors
{
    None = 0,
    Clock = 1,
    Network = 2,
    Devices = 4,
    Power = 8,
    Processes = 16,
    Cpu = 32,
    Location = 64
}

/// <summary>
/// What the PC looks like right now, as far as the running sensors know. A null field is unknown
/// (its sensor is off or hasn't reported yet), and a condition that needs it answers null, so an
/// unknown never starts or stops anything.
/// </summary>
public sealed record SensorSnapshot(DateTime UtcNow)
{
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Connected network names: Wi-Fi SSIDs and the names Windows gives wired networks.</summary>
    public IReadOnlyCollection<string>? Networks { get; init; }

    /// <summary>Names of connected device containers, present plug-and-play devices, and active audio endpoints.</summary>
    public IReadOnlyCollection<string>? Devices { get; init; }

    /// <summary>Connected devices grouped for the editor; filtering never changes <see cref="Devices"/>.</summary>
    public IReadOnlyList<ConnectedDevice>? DeviceChoices { get; init; }

    public PowerState? Power { get; init; }

    /// <summary>The watched process names that are running (see <see cref="ProcessNames.Normalize"/>).</summary>
    public IReadOnlyCollection<string>? Processes { get; init; }

    /// <summary>Recent whole-PC CPU samples, oldest first.</summary>
    public IReadOnlyList<CpuSample>? Cpu { get; init; }

    /// <summary>A live location fix; only while location is on and some place condition needs it.</summary>
    public GeoPoint? Location { get; init; }

    /// <summary>The user's saved place, for sunrise and sunset.</summary>
    public SavedPlace? Home { get; init; }

    public DateTime LocalTime => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, TimeZone);

    /// <summary>Converts a local wall-clock time to UTC, moving a time skipped by a DST change forward an hour.</summary>
    public DateTime ToUtc(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }
}

/// <summary>
/// A snapshot plus, for a running setter, when it started (stop conditions such as "after 30 minutes"
/// need it) and, when it was started for a chosen length from its tray icon's menu, when that ends.
/// </summary>
public readonly record struct ConditionContext(SensorSnapshot Now, DateTime? ActiveSinceUtc = null, DateTime? TimerEndsUtc = null);

public static class ProcessNames
{
    /// <summary>"Blender.exe", "blender", " blender.EXE " all become "blender".</summary>
    public static string Normalize(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed.ToLowerInvariant();
    }
}
