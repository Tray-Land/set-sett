namespace SetSett.Model;

/// <summary>
/// One Windows setting Set Sett can change: the plugin seam for "what is set". The app implements
/// these against Windows (registry, Core Audio, radios, power plans); tests use fakes.
/// </summary>
/// <remarks>
/// To add a setting: implement this in the app's <c>Settings\</c> folder, give it a new stable
/// <see cref="SettingInfo.Id"/>, and register it in <c>SettingRegistry</c>. Everything else (the
/// editor's "Add a setting" menu, the flyout, snapshots and putting back) works from
/// <see cref="Info"/> and these three calls. Calls are async and take and return plain records, so
/// a setting could later live in another process without changing the engine.
/// </remarks>
public interface ISetting
{
    SettingInfo Info { get; }

    /// <summary>False when this PC can't have it (no Bluetooth radio, no battery, no audio device).</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>The current value, or null when it can't be read right now.</summary>
    Task<SettingValue?> ReadAsync();

    /// <summary>Sets the value. False when Windows refused or the setting isn't available.</summary>
    Task<bool> WriteAsync(SettingValue value);
}

public interface ISettingCatalog
{
    IReadOnlyList<ISetting> All { get; }

    ISetting? Find(string id);
}

public sealed class SettingCatalog(IEnumerable<ISetting> settings) : ISettingCatalog
{
    private readonly Dictionary<string, ISetting> _byId = settings.ToDictionary(s => s.Info.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ISetting> All { get; } = [.. settings];

    public ISetting? Find(string id) => _byId.GetValueOrDefault(id);
}
