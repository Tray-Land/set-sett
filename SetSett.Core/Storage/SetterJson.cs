using System.Text.Json;
using System.Text.Json.Serialization;
using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Storage;

/// <summary>setters.json: the user's setters. Version goes up on a breaking change so an older Set Sett can tell.</summary>
public sealed record SetterFile(int Version, IReadOnlyList<Setter> Setters);

/// <summary>state.json: what's running and the snapshots to put back (<see cref="RunState"/>).</summary>
public sealed record RunStateFile(int Version, RunState State);

/// <summary>Versioned definitions for the editor's ready-made setters.</summary>
public sealed record SetterTemplateFile(int Version, IReadOnlyList<SetterTemplates.Template> Templates);

/// <summary>
/// Reading and writing Set Sett's files through a source-generated context (trim-safe, no
/// reflection). Each Read throws JsonException (or NotSupportedException for an unknown kind or a
/// newer version) on a file it can't read; the caller keeps that file aside instead of overwriting it.
/// </summary>
public static class SetterJson
{
    public const int Version = 1;

    public static string WriteSetter(Setter setter) =>
        JsonSerializer.Serialize(setter, SetterJsonContext.Default.Setter);

    public static Setter ReadSetter(string json) =>
        JsonSerializer.Deserialize(json, SetterJsonContext.Default.Setter)
            ?? throw new JsonException("Empty setter file.");

    public static string WriteSetters(IReadOnlyList<Setter> setters) =>
        JsonSerializer.Serialize(new SetterFile(Version, setters), SetterJsonContext.Default.SetterFile);

    public static IReadOnlyList<Setter> ReadSetters(string json)
    {
        SetterFile file = JsonSerializer.Deserialize(json, SetterJsonContext.Default.SetterFile)
            ?? throw new JsonException("Empty setters file.");
        CheckVersion(file.Version);
        return file.Setters ?? [];
    }

    public static string WriteState(RunState state) =>
        JsonSerializer.Serialize(new RunStateFile(Version, state), SetterJsonContext.Default.RunStateFile);

    public static RunState ReadState(string json)
    {
        RunStateFile file = JsonSerializer.Deserialize(json, SetterJsonContext.Default.RunStateFile)
            ?? throw new JsonException("Empty state file.");
        CheckVersion(file.Version);
        return file.State ?? RunState.Empty;
    }

    public static IReadOnlyList<SetterTemplates.Template> ReadTemplates(string json)
    {
        SetterTemplateFile file = JsonSerializer.Deserialize(json, SetterJsonContext.Default.SetterTemplateFile)
            ?? throw new JsonException("Empty setter template file.");
        CheckVersion(file.Version);
        return file.Templates ?? [];
    }

    public static string WritePlace(SavedPlace place) => JsonSerializer.Serialize(place, SetterJsonContext.Default.SavedPlace);

    public static SavedPlace? ReadPlace(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, SetterJsonContext.Default.SavedPlace);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CheckVersion(int version)
    {
        if (version > Version)
        {
            throw new NotSupportedException($"File version {version} is newer than {Version}.");
        }
    }
}

// IgnoreReadOnlyProperties leaves out computed members (Setter.CanRun, Condition.Sensors).
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(SetterFile))]
[JsonSerializable(typeof(Setter))]
[JsonSerializable(typeof(RunStateFile))]
[JsonSerializable(typeof(SetterTemplateFile))]
[JsonSerializable(typeof(SavedPlace))]
public partial class SetterJsonContext : JsonSerializerContext;
