using SetSett.Diagnostics;
using SetSett.Model;
using SetSett.Places;
using SetSett.Sensing;

namespace SetSett.Services;

/// <summary>
/// The user's place for sunrise and sunset. The easy default is picking a city from the offline list
/// (no permission, no network); "Use my location once" takes one fix and names it after the nearest
/// city. The city list is loaded only while a picker needs it.
/// </summary>
internal static class PlaceService
{
    private static IReadOnlyList<City>? _cities;

    /// <summary>Raised on the thread that changed <see cref="Home"/> (always the UI thread).</summary>
    public static event Action? HomeChanged;

    public static SavedPlace? Home
    {
        get => SettingsService.Home;
        set
        {
            SettingsService.Home = value;
            HomeChanged?.Invoke();
        }
    }

    /// <summary>True when Windows already allows location for Set Sett, so place conditions can be offered.</summary>
    public static bool LocationAllowed => LocationSensor.CurrentAccess == LocationAccess.Allowed;

    public static async Task<IReadOnlyList<City>> LoadCitiesAsync()
    {
        if (_cities is { } cached)
        {
            return cached;
        }

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "Data", "world_cities.csv");
        _cities = await Task.Run(() =>
        {
            try
            {
                using StreamReader reader = File.OpenText(path);
                return CityCatalog.Parse(reader);
            }
            catch (Exception ex)
            {
                Log.Warn("Places", "Could not load the city list", ex);
                return (IReadOnlyList<City>)[];
            }
        });
        return _cities;
    }

    /// <summary>Lets the list go when the last picker closes; it's a few MB the idle app doesn't need.</summary>
    public static void ReleaseCities() => _cities = null;

    /// <summary>
    /// Takes one location fix (asking for permission if Windows hasn't yet) and saves it as Home.
    /// Returns null with a reason when location is off or no fix arrives.
    /// </summary>
    public static async Task<(SavedPlace? Place, string? Problem)> UseLocationOnceAsync()
    {
        if (await LocationSensor.RequestAccessAsync() != LocationAccess.Allowed)
        {
            return (null, "Location is off for Set Sett. Turn it on in Settings > Privacy & security > Location, or pick a city.");
        }

        if (await LocationSensor.GetFixAsync(TimeSpan.FromMinutes(5)) is not { } fix)
        {
            return (null, "Couldn't get a location fix. Try again, or pick a city.");
        }

        City? nearest = CityCatalog.Nearest(await LoadCitiesAsync(), fix);
        var place = new SavedPlace(nearest is null ? "Your location" : $"Near {nearest.Display}", fix.Latitude, fix.Longitude);
        Home = place;
        return (place, null);
    }
}
