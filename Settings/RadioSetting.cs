using SetSett.Model;
using Windows.Devices.Radios;

namespace SetSett.Settings;

/// <summary>
/// The Wi-Fi or Bluetooth radio, through Windows.Devices.Radios (the "radios" device capability).
/// This is the same switch as Quick Settings; airplane mode or a hardware switch can still block it,
/// in which case the write reports failure.
/// </summary>
internal sealed class RadioSetting(SettingInfo info, RadioKind kind) : ISetting
{
    private static Task<bool>? _access;

    public SettingInfo Info { get; } = info;

    public static RadioSetting WiFi() => new(
        new SettingInfo(SettingIds.WiFi, "Wi-Fi", "Connections", Glyphs.WiFi, ValueKind.OnOff)
        {
            Description = "Turns the Wi-Fi radio on or off.",
        },
        RadioKind.WiFi);

    public static RadioSetting Bluetooth() => new(
        new SettingInfo(SettingIds.Bluetooth, "Bluetooth", "Connections", Glyphs.Bluetooth, ValueKind.OnOff)
        {
            Description = "Turns the Bluetooth radio on or off.",
        },
        RadioKind.Bluetooth);

    public async Task<bool> IsAvailableAsync() => await FindAsync() is not null;

    public async Task<SettingValue?> ReadAsync() => await FindAsync() is { } radio
        ? radio.State switch
        {
            RadioState.On => new OnOffValue(true),
            RadioState.Off or RadioState.Disabled => new OnOffValue(false),
            _ => null,
        }
        : null;

    public async Task<bool> WriteAsync(SettingValue value)
    {
        if (value is not OnOffValue onOff || await FindAsync() is not { } radio)
        {
            return false;
        }

        try
        {
            return await radio.SetStateAsync(onOff.On ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }

    private async Task<Radio?> FindAsync()
    {
        try
        {
            _access ??= RequestAccessAsync();
            if (!await _access)
            {
                return null;
            }

            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> RequestAccessAsync()
    {
        try
        {
            return await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }
}
