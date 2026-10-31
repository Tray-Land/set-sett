using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.Registry;

namespace SetSett.Services;

/// <summary>
/// Reports the Windows (taskbar and Start) theme, which can differ from the app theme, and raises
/// <see cref="Changed"/> when it flips. Tray icons and shell-style flyouts follow this, not the
/// app theme.
/// </summary>
internal static class SystemThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeValue = "SystemUsesLightTheme";
    private const string ColorPrevalenceValue = "ColorPrevalence";

    private static readonly RegistryKey? PersonalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
    private static readonly AutoResetEvent KeyChanged = new(false);
    private static bool _isLight = ReadFlag(SystemUsesLightThemeValue);

    // One UISettings for the process: it's a COM object, and ColorValuesChanged only fires while it's alive.
    private static readonly Windows.UI.ViewManagement.UISettings? Colors = CreateColors();
    private static uint? _accent = ReadAccent();

    static SystemThemeService()
    {
        if (Colors is not null)
        {
            Colors.ColorValuesChanged += (_, _) => OnColorsChanged();
        }

        if (PersonalizeKey is null)
        {
            return;
        }

        ThreadPool.RegisterWaitForSingleObject(KeyChanged, (_, _) => OnKeyChanged(), null, Timeout.Infinite, executeOnlyOnce: false);
        WatchKey();
    }

    /// <summary>Raised on a background thread when the Windows theme switches between light and dark.</summary>
    public static event EventHandler? Changed;

    /// <summary>True when the Windows theme is light; false when dark or unknown.</summary>
    public static bool IsLight => _isLight;

    /// <summary>
    /// True when "Show accent color on Start and taskbar" is on, which Windows only allows in dark
    /// mode. Shell flyouts take an accent tint then.
    /// </summary>
    public static bool ShowsAccentColor => !IsLight && ReadFlag(ColorPrevalenceValue);

    /// <summary>Raised on a background thread when the Windows accent color changes.</summary>
    public static event EventHandler? AccentChanged;

    /// <summary>The Windows accent as opaque 0xAARRGGBB, or null when it can't be read. Cached.</summary>
    public static uint? Accent => _accent;

    private static Windows.UI.ViewManagement.UISettings? CreateColors()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings();
        }
        catch
        {
            return null;
        }
    }

    private static uint? ReadAccent()
    {
        try
        {
            Windows.UI.Color accent = Colors?.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent) ?? default;
            return Colors is null ? null : 0xFF000000 | ((uint)accent.R << 16) | ((uint)accent.G << 8) | accent.B;
        }
        catch
        {
            return null;
        }
    }

    // Fires for theme and accent alike; only an actual accent change is passed on.
    private static void OnColorsChanged()
    {
        uint? accent = ReadAccent();
        if (accent != _accent)
        {
            _accent = accent;
            AccentChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void OnKeyChanged()
    {
        WatchKey();
        bool isLight = ReadFlag(SystemUsesLightThemeValue);
        if (isLight != _isLight)
        {
            _isLight = isLight;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    // One-shot: the key must be re-watched after every notification.
    private static void WatchKey() =>
        PInvoke.RegNotifyChangeKeyValue(
            PersonalizeKey!.Handle,
            false,
            REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_FILTER.REG_NOTIFY_THREAD_AGNOSTIC,
            KeyChanged.SafeWaitHandle,
            true);

    private static bool ReadFlag(string name) => PersonalizeKey?.GetValue(name) is int value && value != 0;
}
