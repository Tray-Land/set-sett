using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Tests;

/// <summary>A setting that lives in memory; tests change <see cref="Value"/> to play the user.</summary>
internal sealed class FakeSetting(SettingInfo info, SettingValue initial) : ISetting
{
    public SettingValue Value { get; set; } = initial;

    public bool Available { get; set; } = true;

    public bool Refuse { get; set; }

    public int Writes { get; private set; }

    public SettingInfo Info { get; } = info;

    public static FakeSetting Theme(string id = SettingIds.AppsTheme, string value = SettingIds.Light) => new(
        new SettingInfo(id, "Theme", "Personalization", Glyphs.Color, ValueKind.Choice)
        {
            Choices = [new(SettingIds.Light, "Light"), new(SettingIds.Dark, "Dark")],
        },
        new ChoiceValue(value));

    public static FakeSetting Volume(int value = 40) =>
        new(new SettingInfo(SettingIds.Volume, "Volume", "Sound", Glyphs.Volume, ValueKind.Percent), new NumberValue(value));

    public static FakeSetting KeepAwake() => new(
        new SettingInfo(SettingIds.KeepAwake, "Keep awake", "Power", Glyphs.Power, ValueKind.Choice)
        {
            Choices = [new(SettingIds.AwakeOff, "Off"), new(SettingIds.AwakeSystem, "On")],
            Holder = ValueHolder.SetSett,
        },
        new ChoiceValue(SettingIds.AwakeOff));

    public Task<bool> IsAvailableAsync() => Task.FromResult(Available);

    public Task<SettingValue?> ReadAsync() => Task.FromResult<SettingValue?>(Available ? Value : null);

    public Task<bool> WriteAsync(SettingValue value)
    {
        if (Refuse || !Available)
        {
            return Task.FromResult(false);
        }

        Writes++;
        Value = value;
        return Task.FromResult(true);
    }
}

internal sealed class MemoryStore : IRunStateStore
{
    public RunState Saved { get; private set; } = RunState.Empty;

    public int Saves { get; private set; }

    public RunState Load() => Saved;

    public void Save(RunState state)
    {
        Saved = state;
        Saves++;
    }
}

internal static class At
{
    public static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

    /// <summary>A snapshot at a Central-time wall clock.</summary>
    public static SensorSnapshot Local(int year, int month, int day, int hour, int minute = 0) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, hour, minute, 0), Central)) { TimeZone = Central };

    public static SensorSnapshot Plus(this SensorSnapshot now, TimeSpan by) => now with { UtcNow = now.UtcNow + by };
}
