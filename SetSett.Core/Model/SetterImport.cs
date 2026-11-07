using System.Text.Json;
using SetSett.Storage;

namespace SetSett.Model;

/// <summary>Validates and copies a setter definition from a portable .sett file.</summary>
public static class SetterImport
{
    public static Setter Read(string json, ISettingCatalog catalog)
    {
        IReadOnlyList<Setter> setters = ReadAll(json, catalog);
        if (setters.Count != 1)
        {
            throw new JsonException("The .sett file contains more than one setter.");
        }

        return setters[0];
    }

    /// <summary>Reads either a current one-or-many .sett file or an older raw single-setter file.</summary>
    public static IReadOnlyList<Setter> ReadAll(string json, ISettingCatalog catalog)
    {
        bool isBundle;
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The .sett file must contain a JSON object.");
            }

            isBundle = document.RootElement.TryGetProperty("setters", out _);
        }

        IReadOnlyList<Setter> setters = isBundle
            ? SetterJson.ReadSetters(json)
            : [SetterJson.ReadSetter(json)];
        if (setters.Count == 0)
        {
            throw new JsonException("The .sett file does not contain any setters.");
        }

        foreach (Setter setter in setters)
        {
            if (setter is null)
            {
                throw new JsonException("The .sett file contains an empty setter.");
            }

            if (Problem(setter, catalog) is { } problem)
            {
                throw new JsonException(problem);
            }
        }

        return setters;
    }

    /// <summary>Null when a definition can be added, otherwise a user-facing reason it cannot.</summary>
    public static string? Problem(Setter setter, ISettingCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(setter.Name))
        {
            return "The setter needs a name.";
        }

        if (setter.Changes is null || setter.Changes.Count == 0)
        {
            return "The setter does not change any settings.";
        }

        if (setter.Glyph is null || setter.When is null || setter.Until is null ||
            setter.TimerChoices is null || setter.ActiveLook is null || setter.InactiveLook is null)
        {
            return "The setter definition is incomplete.";
        }

        if (!Glyphs.IsValid(setter.Glyph) || !Glyphs.IsValid(setter.ActiveLook.Glyph) || !Glyphs.IsValid(setter.InactiveLook.Glyph))
        {
            return "The setter's icon is not valid.";
        }

        HashSet<string> settings = new(StringComparer.OrdinalIgnoreCase);
        foreach (SettingChange change in setter.Changes)
        {
            if (change is null || string.IsNullOrWhiteSpace(change.SettingId) || change.Value is null)
            {
                return "The setter contains an incomplete setting change.";
            }

            if (!settings.Add(change.SettingId))
            {
                return $"The setting \"{change.SettingId}\" is listed more than once.";
            }

            if (catalog.Find(change.SettingId) is not { } setting)
            {
                return $"This version of Set Sett does not know the setting \"{change.SettingId}\".";
            }

            if (!setting.Info.Accepts(change.Value))
            {
                return $"The value for \"{setting.Info.DisplayName}\" is not valid.";
            }
        }

        if (setter.When.Any(condition => condition is null) || setter.Until.Any(condition => condition is null))
        {
            return "The setter contains an incomplete condition.";
        }

        if (setter.IsAutomatic && setter.When.Count == 0)
        {
            return "An automatic setter needs a start condition.";
        }

        if (setter.When.Any(condition => !condition.CanStart))
        {
            return "A stop condition cannot be used to start a setter.";
        }

        return null;
    }
}
