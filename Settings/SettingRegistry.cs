using SetSett.Diagnostics;
using SetSett.Model;

namespace SetSett.Settings;

/// <summary>
/// Every setting Set Sett can change: the one place a new <see cref="ISetting"/> is registered
/// (tier 1 plugins). The editor's "Add a setting" menu lists them in this order, grouped by
/// <see cref="SettingInfo.Group"/>. Settings with no public API yet are in docs\ROADMAP.md.
/// </summary>
internal static class SettingRegistry
{
    private static Dictionary<string, bool>? _availability;

    public static ISettingCatalog Catalog { get; } = new SettingCatalog(
    [
        PersonalizeSetting.Theme(SettingIds.WindowsTheme, "Windows", "SystemUsesLightTheme",
            "Light or dark for Windows and apps. Pick Windows or Apps to set only one."),
        PersonalizeSetting.Theme(SettingIds.AppsTheme, "Apps", "AppsUseLightTheme",
            "Light or dark for Windows and apps. Pick Windows or Apps to set only one."),
        PersonalizeSetting.Toggle(SettingIds.Transparency, "Transparency effects", Glyphs.Color, "EnableTransparency",
            "See-through windows and surfaces."),
        new VolumeSetting(),
        MuteSetting.Speakers(),
        MuteSetting.Microphone(),
        RadioSetting.WiFi(),
        RadioSetting.Bluetooth(),
        PowerTimeoutSetting.ScreenOff(onBattery: false),
        PowerTimeoutSetting.ScreenOff(onBattery: true),
        PowerTimeoutSetting.Sleep(onBattery: false),
        PowerTimeoutSetting.Sleep(onBattery: true),
        new KeepAwakeSetting(),
    ]);

    /// <summary>Whether each setting exists on this PC, as of the last <see cref="RefreshAvailabilityAsync"/>.</summary>
    public static bool IsAvailable(string id) => _availability?.GetValueOrDefault(id, true) ?? true;

    /// <summary>Checks every setting once (radios and audio devices come and go); the editor calls this when it opens.</summary>
    public static async Task RefreshAvailabilityAsync()
    {
        var availability = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (ISetting setting in Catalog.All)
        {
            try
            {
                availability[setting.Info.Id] = await setting.IsAvailableAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Registry", $"Availability check for {setting.Info.Id} failed", ex);
                availability[setting.Info.Id] = false;
            }
        }

        _availability = availability;
        Log.Info("Registry", $"Settings available here: {availability.Count(p => p.Value)} of {availability.Count} " +
            $"(unavailable: {string.Join(", ", availability.Where(p => !p.Value).Select(p => p.Key))})");
    }
}
