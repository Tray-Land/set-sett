using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SetSett.Model;

namespace SetSett.Settings;

/// <summary>
/// The default speaker or microphone's volume and mute, through Core Audio. Uses source-generated
/// COM (<see cref="GeneratedComInterfaceAttribute"/>) because Release builds are trimmed, which turns
/// off built-in COM interop. Each call opens the endpoint fresh on the thread pool and lets it go, so
/// nothing is held while idle and a changed default device is always the one used.
/// </summary>
internal static partial class CoreAudio
{
    public const int Render = 0;   // eRender: speakers / headphones
    public const int Capture = 1;  // eCapture: microphone

    private const int Console = 0; // eConsole role
    private const uint ClsctxAll = 0x17;

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Runs <paramref name="action"/> on the default endpoint; null when there's no device or a call fails.</summary>
    public static Task<T?> UseAsync<T>(int flow, Func<IAudioEndpointVolume, T> action) where T : struct =>
        Task.Run(() => Use(flow, action));

    private static T? Use<T>(int flow, Func<IAudioEndpointVolume, T> action) where T : struct
    {
        try
        {
            Guid enumeratorIid = typeof(IMMDeviceEnumerator).GUID;
            if (CoCreateInstance(MMDeviceEnumeratorClsid, 0, ClsctxAll, enumeratorIid, out nint pointer) < 0 || pointer == 0)
            {
                return null;
            }

            var enumerator = (IMMDeviceEnumerator)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            Marshal.Release(pointer);

            if (enumerator.GetDefaultAudioEndpoint(flow, Console, out IMMDevice? device) < 0 || device is null)
            {
                return null;
            }

            Guid volumeIid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(volumeIid, ClsctxAll, 0, out IAudioEndpointVolume? volume) < 0 || volume is null)
            {
                return null;
            }

            return action(volume);
        }
        catch
        {
            return null;
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(in Guid iid, uint clsContext, nint activationParams, out IAudioEndpointVolume? endpointVolume);
}

// Declared in vtable order up to GetMute; later members aren't used.
[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal partial interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(nint notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(nint notify);

    [PreserveSig]
    int GetChannelCount(out uint count);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, in Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, in Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, in Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, in Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute(int mute, in Guid eventContext);

    [PreserveSig]
    int GetMute(out int mute);
}

/// <summary>The default speaker's volume, 0–100.</summary>
internal sealed class VolumeSetting : ISetting
{
    public SettingInfo Info { get; } = new(SettingIds.Volume, "Volume", "Sound", Glyphs.Volume, ValueKind.Percent)
    {
        Description = "The volume of your default speakers or headphones.",
    };

    public async Task<bool> IsAvailableAsync() => await ReadAsync() is not null;

    public async Task<SettingValue?> ReadAsync() =>
        await CoreAudio.UseAsync(CoreAudio.Render, v => v.GetMasterVolumeLevelScalar(out float level) >= 0 ? (int)Math.Round(level * 100) : -1)
            is int percent and >= 0 ? new NumberValue(percent) : null;

    public async Task<bool> WriteAsync(SettingValue value) =>
        value is NumberValue { Value: >= 0 and <= 100 } number &&
        await CoreAudio.UseAsync(CoreAudio.Render, v => v.SetMasterVolumeLevelScalar(number.Value / 100f, Guid.Empty) >= 0) == true;
}

/// <summary>Mute for the default speakers (<see cref="CoreAudio.Render"/>) or microphone (<see cref="CoreAudio.Capture"/>).</summary>
internal sealed class MuteSetting(SettingInfo info, int flow) : ISetting
{
    public SettingInfo Info { get; } = info;

    public static MuteSetting Speakers() => new(
        new SettingInfo(SettingIds.Mute, "Mute", "Sound", Glyphs.Mute, ValueKind.OnOff)
        {
            Description = "Mutes your default speakers or headphones.",
            OnText = "Muted",
            OffText = "Not muted",
        },
        CoreAudio.Render);

    public static MuteSetting Microphone() => new(
        new SettingInfo(SettingIds.MicrophoneMute, "Microphone mute", "Sound", Glyphs.Microphone, ValueKind.OnOff)
        {
            Description = "Mutes your default microphone for every app.",
            OnText = "Muted",
            OffText = "Not muted",
        },
        CoreAudio.Capture);

    public async Task<bool> IsAvailableAsync() => await ReadAsync() is not null;

    public async Task<SettingValue?> ReadAsync() =>
        await CoreAudio.UseAsync(flow, v => v.GetMute(out int muted) >= 0 ? muted : -1) is int muted and >= 0
            ? new OnOffValue(muted != 0)
            : null;

    public async Task<bool> WriteAsync(SettingValue value) =>
        value is OnOffValue onOff &&
        await CoreAudio.UseAsync(flow, v => v.SetMute(onOff.On ? 1 : 0, Guid.Empty) >= 0) == true;
}
