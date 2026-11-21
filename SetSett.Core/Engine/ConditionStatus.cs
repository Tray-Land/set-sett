using SetSett.Model;

namespace SetSett.Engine;

/// <summary>What the editor can say about a condition without guessing at missing sensor data.</summary>
public enum ConditionStatus
{
    Active,
    Inactive,
    Unknown,
    NotMonitored,
    Instant
}

public static class ConditionStatusPolicy
{
    public static ConditionStatus Start(Setter setter, Condition condition, SensorSnapshot now)
    {
        if (!setter.IsAutomatic || !setter.Enabled || !setter.CanRun || !condition.CanStart)
        {
            return ConditionStatus.NotMonitored;
        }

        return condition.IsTrigger ? ConditionStatus.Instant : Answer(condition.Evaluate(new ConditionContext(now)));
    }

    public static ConditionStatus Stop(Condition condition, SetterRun? run, SensorSnapshot now) =>
        run is null ? ConditionStatus.NotMonitored : Answer(condition.Evaluate(run.Context(now)));

    private static ConditionStatus Answer(bool? answer) => answer switch
    {
        true => ConditionStatus.Active,
        false => ConditionStatus.Inactive,
        null => ConditionStatus.Unknown,
    };
}
