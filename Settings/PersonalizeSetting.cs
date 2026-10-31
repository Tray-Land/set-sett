using System.Runtime.InteropServices;
using Microsoft.Win32;
using SetSett.Model;

namespace SetSett.Settings;

/// <summary>
/// A DWORD under HKCU\...\Themes\Personalize (light/dark mode, transparency). Writes reach the real
/// registry from the MSIX package (HKCU isn't virtualized for keys the package doesn't own; checked
/// with Invoke-CommandInDesktopPackage), then a WM_SETTINGCHANGE "ImmersiveColorSet" broadcast tells
/// running apps and the shell to repaint.
/// </summary>
internal sealed partial class PersonalizeSetting(
    SettingInfo info,
    string valueName,
    int missingValue,
    Func<int, SettingValue> fromRegistry,
    Func<SettingValue, int?> toRegistry) : ISetting
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const nint HwndBroadcast = 0xFFFF;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    public SettingInfo Info { get; } = info;

    /// <summary>Light or dark, stored as 1 (light) or 0 (dark).</summary>
    public static PersonalizeSetting Theme(string id, string name, string valueName, string description) => new(
        new SettingInfo(id, name, "Personalization", Glyphs.Color, ValueKind.Choice)
        {
            Description = description,
            Choices = [new(SettingIds.Light, "Light"), new(SettingIds.Dark, "Dark")],
        },
        valueName,
        missingValue: 1,
        dword => new ChoiceValue(dword == 0 ? SettingIds.Dark : SettingIds.Light),
        value => value is ChoiceValue choice ? (choice.Id == SettingIds.Dark ? 0 : 1) : null);

    public static PersonalizeSetting Toggle(string id, string name, string glyph, string valueName, string description) => new(
        new SettingInfo(id, name, "Personalization", glyph, ValueKind.OnOff) { Description = description },
        valueName,
        missingValue: 1,
        dword => new OnOffValue(dword != 0),
        value => value is OnOffValue onOff ? (onOff.On ? 1 : 0) : null);

    public Task<bool> IsAvailableAsync() => Task.FromResult(true);

    public Task<SettingValue?> ReadAsync()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            int dword = key?.GetValue(valueName) is int value ? value : missingValue;
            return Task.FromResult<SettingValue?>(fromRegistry(dword));
        }
        catch
        {
            return Task.FromResult<SettingValue?>(null);
        }
    }

    public async Task<bool> WriteAsync(SettingValue value)
    {
        if (toRegistry(value) is not { } dword)
        {
            return false;
        }

        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true))
            {
                key.SetValue(valueName, dword, RegistryValueKind.DWord);
            }

            // A hung window can hold a broadcast up; keep it off the UI thread.
            await Task.Run(() => SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "ImmersiveColorSet", SmtoAbortIfHung, 1000, out _));
            return true;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(nint hWnd, uint msg, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}
