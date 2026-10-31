namespace SetSett.Model;

/// <summary>
/// Ids of the built-in settings. They're saved in setters.json, so never rename one that has
/// shipped; add a new id instead.
/// </summary>
public static class SettingIds
{
    public const string AppsTheme = "theme.apps";
    public const string WindowsTheme = "theme.windows";
    public const string Transparency = "transparency";
    public const string Volume = "volume";
    public const string Mute = "mute";
    public const string MicrophoneMute = "microphone.mute";
    public const string WiFi = "wifi";
    public const string Bluetooth = "bluetooth";
    public const string ScreenOffPluggedIn = "screen.off.ac";
    public const string ScreenOffOnBattery = "screen.off.dc";
    public const string SleepPluggedIn = "sleep.ac";
    public const string SleepOnBattery = "sleep.dc";
    public const string KeepAwake = "keepawake";

    public const string Light = "light";
    public const string Dark = "dark";

    public const string AwakeOff = "off";
    public const string AwakeSystem = "system";
    public const string AwakeDisplay = "display";

    /// <summary>Choices for timeouts, in seconds; 0 is never.</summary>
    public static readonly IReadOnlyList<int> TimeoutChoices =
        [0, 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600, 7200, 10800, 18000];
}
