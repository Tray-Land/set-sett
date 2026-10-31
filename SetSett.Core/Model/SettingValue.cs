using System.Text.Json.Serialization;

namespace SetSett.Model;

/// <summary>
/// A setting's value. Values are records so two reads compare by value: that is how the engine
/// tells whether someone changed a setting after Set Sett set it (see <see cref="Matches"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OnOffValue), "onOff")]
[JsonDerivedType(typeof(NumberValue), "number")]
[JsonDerivedType(typeof(ChoiceValue), "choice")]
public abstract record SettingValue
{
    /// <summary>
    /// True when <paramref name="other"/>, read back from Windows, still counts as this value.
    /// Numbers allow a step of slack because some settings round (volume is stored as a float).
    /// </summary>
    public virtual bool Matches(SettingValue? other) => Equals(other);
}

public sealed record OnOffValue(bool On) : SettingValue;

/// <summary>A percentage (0–100) or a duration in seconds, depending on the setting's <see cref="ValueKind"/>.</summary>
public sealed record NumberValue(int Value) : SettingValue
{
    public override bool Matches(SettingValue? other) => other is NumberValue number && Math.Abs(number.Value - Value) <= 1;
}

/// <summary>One of the setting's <see cref="SettingInfo.Choices"/>, by id.</summary>
public sealed record ChoiceValue(string Id) : SettingValue
{
    public override bool Matches(SettingValue? other) =>
        other is ChoiceValue choice && string.Equals(choice.Id, Id, StringComparison.OrdinalIgnoreCase);
}
