namespace SetSett.Model;

public enum ValueKind
{
    OnOff,
    Percent,    // NumberValue 0–100
    Duration,   // NumberValue in seconds; 0 means never
    Choice
}

/// <summary>Who keeps a value once it's set, which decides what a crash leaves behind.</summary>
public enum ValueHolder
{
    /// <summary>Windows keeps it after Set Sett exits (registry, power plan, volume, radios).</summary>
    Windows,

    /// <summary>
    /// Only Set Sett's process holds it (keep awake). It ends with the process, so nothing can
    /// get stuck; after a restart the engine re-applies it for setters that are still on.
    /// </summary>
    SetSett
}

public sealed record SettingChoice(string Id, string Name);

/// <summary>What a setting is, for the editor, the flyout, and the engine. <see cref="ISetting"/> does the work.</summary>
/// <param name="Id">Stable id saved in setters.json; never rename one that has shipped.</param>
/// <param name="Glyph">A Segoe Fluent Icons glyph.</param>
public sealed record SettingInfo(string Id, string Name, string Group, string Glyph, ValueKind Kind)
{
    public string? Description { get; init; }

    public IReadOnlyList<SettingChoice> Choices { get; init; } = [];

    /// <summary>Duration presets in seconds, for <see cref="ValueKind.Duration"/>; 0 is "never".</summary>
    public IReadOnlyList<int> Durations { get; init; } = [];

    public string OnText { get; init; } = "On";

    public string OffText { get; init; } = "Off";

    public ValueHolder Holder { get; init; } = ValueHolder.Windows;

    /// <summary>
    /// Settings that are one thing to the user but several to Windows share a family id ("screen.off"
    /// for the plugged in and on battery timeouts). The editor shows the family once, with an
    /// "Applies when" choice, and the engine still claims and puts back each member on its own.
    /// </summary>
    public string? Family { get; init; }

    /// <summary>For a family member, when it applies: "Plugged in", "On battery".</summary>
    public string? Scope { get; init; }

    /// <summary>The editor's label for the scope choice: "Applies when" (power) or "Applies to" (theme).</summary>
    public string ScopeLabel { get; init; } = "Applies when";

    /// <summary>The scope choice that covers every member: "Always" (power) or "Both" (theme).</summary>
    public string AllScopesLabel { get; init; } = "Always";

    /// <summary>The name with its scope: "Screen off after (plugged in)". Just the name for a setting outside a family.</summary>
    public string DisplayName => Scope is null ? Name : $"{Name} ({Scope.ToLowerInvariant()})";

    /// <summary>What a newly added change starts at in the editor.</summary>
    public SettingValue DefaultValue => Kind switch
    {
        ValueKind.OnOff => new OnOffValue(true),
        ValueKind.Percent => new NumberValue(50),
        ValueKind.Duration => new NumberValue(Durations.Count > 0 ? Durations[0] : 0),
        _ => new ChoiceValue(Choices.Count > 0 ? Choices[0].Id : ""),
    };

    /// <summary>True when <paramref name="value"/> is the right shape for this setting.</summary>
    public bool Accepts(SettingValue value) => (Kind, value) switch
    {
        (ValueKind.OnOff, OnOffValue) => true,
        (ValueKind.Percent, NumberValue { Value: >= 0 and <= 100 }) => true,
        (ValueKind.Duration, NumberValue { Value: >= 0 }) => true,
        (ValueKind.Choice, ChoiceValue choice) => Choices.Any(c => string.Equals(c.Id, choice.Id, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}
