using SetSett.Diagnostics;
using SetSett.Model;
using Windows.Devices.Geolocation;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace SetSett.Sensing;

internal enum LocationAccess
{
    Unknown,    // not asked yet
    Allowed,
    Denied      // off for Set Sett, or for the whole PC, in Settings > Privacy & security > Location
}

/// <summary>
/// Where the PC is, for place conditions (from traydog). Rather than hold a live position
/// subscription (which keeps the "location in use" indicator lit), it takes one quick fix at start,
/// on <see cref="Refresh"/> (the host calls it when the network changes), and every 10 minutes. It
/// only runs when Windows already allows location for Set Sett, so it never prompts in the background.
/// </summary>
internal sealed class LocationSensor : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FixTimeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    public LocationSensor() => _ = RunAsync(_cts.Token);

    public event Action? Changed;

    public GeoPoint? Location { get; private set; }

    /// <summary>Whether location is allowed for Set Sett right now, without asking.</summary>
    public static LocationAccess CurrentAccess
    {
        get
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            {
                return LocationAccess.Unknown;
            }

            try
            {
                return AppCapability.Create("location").CheckAccess() switch
                {
                    AppCapabilityAccessStatus.Allowed => LocationAccess.Allowed,
                    AppCapabilityAccessStatus.UserPromptRequired => LocationAccess.Unknown,
                    _ => LocationAccess.Denied,
                };
            }
            catch (Exception ex)
            {
                Log.Warn("Location", "Could not read the location permission", ex);
                return LocationAccess.Unknown;
            }
        }
    }

    /// <summary>Asks for access (may show the Windows prompt). Call on the UI thread, only from a user's click.</summary>
    public static async Task<LocationAccess> RequestAccessAsync()
    {
        try
        {
            return await Geolocator.RequestAccessAsync() switch
            {
                GeolocationAccessStatus.Allowed => LocationAccess.Allowed,
                GeolocationAccessStatus.Denied => LocationAccess.Denied,
                _ => LocationAccess.Unknown,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Location", "Location access request failed; treating as denied", ex);
            return LocationAccess.Denied;
        }
    }

    /// <summary>One fix, or null when location is off or none arrives in time.</summary>
    public static async Task<GeoPoint?> GetFixAsync(TimeSpan maximumAge)
    {
        try
        {
            var geolocator = new Geolocator { DesiredAccuracy = PositionAccuracy.Default };
            Geoposition position = await geolocator.GetGeopositionAsync(maximumAge, FixTimeout);
            BasicGeoposition point = position.Coordinate.Point.Position;
            return new GeoPoint(point.Latitude, point.Longitude);
        }
        catch (Exception ex)
        {
            // No coordinates in the log: only that a fix failed.
            Log.Debug("Location", $"No location fix: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public void Refresh()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                GeoPoint? location = await GetFixAsync(MaximumAge);
                ct.ThrowIfCancellationRequested();
                if (location is not null && location != Location)
                {
                    Location = location;
                    Changed?.Invoke();
                }

                await _wake.WaitAsync(RefreshInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Disposed.
        }
    }
}
