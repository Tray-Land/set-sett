using SetSett.Model;

namespace SetSett.Engine;

public enum StopReason
{
    /// <summary>A stop condition was met.</summary>
    Until,

    /// <summary>An automatic setter's start conditions stopped holding.</summary>
    ConditionsEnded,

    /// <summary>The user paused an automatic setter.</summary>
    Disabled,

    /// <summary>The setter was deleted.</summary>
    Removed,

    /// <summary>The setter was edited in a way that changes what it does, or it can no longer run.</summary>
    Edited,

    /// <summary>The user stopped it (flyout, tray icon).</summary>
    User,

    /// <summary>Set Sett is exiting.</summary>
    Exit
}

public readonly record struct SetterTransition(string SetterId, bool Start, StopReason Reason = StopReason.Until);

/// <summary>
/// Decides which setters start and stop given what the sensors see. Pure, so it's unit tested; the
/// <see cref="SetterEngine"/> carries the decisions out.
/// </summary>
public static class SetterPolicy
{
    /// <summary>The longest the clock goes unchecked while any clock condition is watched (sleep, clock changes).</summary>
    public static readonly TimeSpan MaxClockWait = TimeSpan.FromHours(1);

    /// <summary>Combines condition answers; null (unknown) only when the known answers can't decide.</summary>
    public static bool? Combine(IEnumerable<bool?> answers, ConditionMatch match)
    {
        bool sawUnknown = false;
        bool sawAny = false;
        foreach (bool? answer in answers)
        {
            sawAny = true;
            switch (answer)
            {
                case null:
                    sawUnknown = true;
                    break;
                case false when match == ConditionMatch.All:
                    return false;
                case true when match == ConditionMatch.Any:
                    return true;
            }
        }

        if (!sawAny)
        {
            return false;
        }

        return sawUnknown ? null : match == ConditionMatch.All;
    }

    public static bool? StartConditionsHold(Setter setter, SensorSnapshot now) =>
        Combine(setter.When.Where(c => c.CanStart).Select(c => c.Evaluate(new ConditionContext(now))), setter.Match);

    public static bool StopConditionMet(Setter setter, SetterRun run, SensorSnapshot now) =>
        setter.Until.Any(c => c.Evaluate(run.Context(now)) == true);

    /// <summary>
    /// What should start and stop now. Setters act on edges: an automatic setter starts when its
    /// conditions become true; one stopped early (latched) waits for them to end first. An unknown
    /// answer changes nothing, so a sensor that hasn't reported yet never flips a setter.
    /// </summary>
    public static (IReadOnlyList<SetterTransition> Transitions, RunState State) Step(
        IReadOnlyList<Setter> setters, RunState state, SensorSnapshot now)
    {
        List<SetterTransition> transitions = [];
        var byId = setters.ToDictionary(s => s.Id);

        foreach (SetterRun run in state.Active)
        {
            if (!byId.ContainsKey(run.SetterId))
            {
                transitions.Add(new(run.SetterId, false, StopReason.Removed));
            }
        }

        foreach (string latched in state.Latched)
        {
            if (!byId.ContainsKey(latched))
            {
                state = state.WithLatch(latched, false);
            }
        }

        foreach (Setter setter in setters)
        {
            SetterRun? run = state.RunOf(setter.Id);
            if (run is not null)
            {
                if (!setter.CanRun)
                {
                    transitions.Add(new(setter.Id, false, StopReason.Edited));
                }
                else if (setter.IsAutomatic && !setter.Enabled)
                {
                    transitions.Add(new(setter.Id, false, StopReason.Disabled));
                }
                else if (setter.IsAutomatic && StartConditionsHold(setter, now) == false)
                {
                    transitions.Add(new(setter.Id, false, StopReason.ConditionsEnded));
                }
                else if (StopConditionMet(setter, run, now))
                {
                    transitions.Add(new(setter.Id, false, StopReason.Until));
                    if (setter.IsAutomatic)
                    {
                        state = state.WithLatch(setter.Id, true);
                    }
                }

                continue;
            }

            if (!setter.IsAutomatic)
            {
                continue;
            }

            if (!setter.Enabled || !setter.CanRun)
            {
                state = state.WithLatch(setter.Id, false);
                continue;
            }

            bool? holds = StartConditionsHold(setter, now);
            if (state.IsLatched(setter.Id))
            {
                if (holds == false)
                {
                    state = state.WithLatch(setter.Id, false);
                }
            }
            else if (holds == true)
            {
                transitions.Add(new(setter.Id, true));
            }
        }

        return (transitions, state);
    }

    /// <summary>The conditions being watched right now: start conditions of enabled automatic setters, stop conditions of running ones.</summary>
    public static IEnumerable<(Condition Condition, ConditionContext Context)> Watched(
        IReadOnlyList<Setter> setters, RunState state, SensorSnapshot now)
    {
        foreach (Setter setter in setters)
        {
            SetterRun? run = state.RunOf(setter.Id);
            if (setter.IsAutomatic && setter.Enabled && setter.CanRun)
            {
                foreach (Condition condition in setter.When)
                {
                    yield return (condition, new ConditionContext(now));
                }
            }

            if (run is not null)
            {
                foreach (Condition condition in setter.Until)
                {
                    yield return (condition, run.Context(now));
                }
            }
        }
    }

    public static Sensors SensorsNeeded(IReadOnlyList<Setter> setters, RunState state, SensorSnapshot now) =>
        Watched(setters, state, now).Aggregate(Sensors.None, (all, w) => all | w.Condition.Sensors);

    /// <summary>Process names some watched condition asks about, normalized.</summary>
    public static IReadOnlySet<string> ProcessesWatched(IReadOnlyList<Setter> setters, RunState state, SensorSnapshot now) =>
        Watched(setters, state, now)
            .Select(w => w.Condition)
            .OfType<ProcessCondition>()
            .Select(p => p.Normalized)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>When to look again by the clock alone (UTC), or null when nothing watched depends on the clock.</summary>
    public static DateTime? NextWake(IReadOnlyList<Setter> setters, RunState state, SensorSnapshot now)
    {
        DateTime? next = null;
        bool watchesClock = false;
        foreach (var (condition, context) in Watched(setters, state, now))
        {
            if (!condition.Sensors.HasFlag(Sensors.Clock))
            {
                continue;
            }

            watchesClock = true;
            if (condition.NextChange(context) is { } change && change > now.UtcNow && (next is null || change < next))
            {
                next = change;
            }
        }

        if (!watchesClock)
        {
            return null;
        }

        DateTime latest = now.UtcNow + MaxClockWait;
        return next is { } at && at < latest ? at : latest;
    }
}
