using SetSett.Model;

namespace SetSett.Engine;

public enum StopReason
{
    /// <summary>A stop condition was met.</summary>
    Until,

    /// <summary>An automatic setter's start conditions stopped holding.</summary>
    ConditionsEnded,

    /// <summary>The user disabled the setter.</summary>
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

/// <param name="Renew">
/// With <paramref name="Start"/>: the setter is already running, but its conditions ended and began again
/// without Set Sett seeing it (the PC slept through the gap), or its time came round again, so its run starts afresh.
/// </param>
/// <param name="Once">
/// With <paramref name="Start"/>: the setter has no stop condition, so it sets its values and is done:
/// no run begins and nothing is held to put back.
/// </param>
public readonly record struct SetterTransition(
    string SetterId, bool Start, StopReason Reason = StopReason.Until, bool Renew = false, bool Once = false);

/// <summary>
/// Decides which setters start and stop given what the sensors see. Pure, so it's unit tested; the
/// <see cref="SetterEngine"/> carries the decisions out.
/// </summary>
public static class SetterPolicy
{
    /// <summary>The longest the clock goes unchecked while any clock condition is watched (sleep, clock changes).</summary>
    public static readonly TimeSpan MaxClockWait = TimeSpan.FromHours(1);

    // Years of daily windows. Only a window that never ends, such as 24 hours every day, gets near it.
    private const int MaxReplayedBoundaries = 1000;

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

    /// <summary>
    /// Whether the start conditions that hold over time are true. An instant ("at 9:00 AM") isn't one of
    /// them; a setter with one asks its other conditions only at the moment it fires, and with no others
    /// there is nothing to hold, so this is true.
    /// </summary>
    public static bool? StartConditionsHold(Setter setter, SensorSnapshot now)
    {
        List<Condition> levels = [.. setter.When.Where(c => c.CanStart && !c.IsTrigger)];
        return levels.Count == 0 && setter.HasTrigger
            ? true
            : Combine(levels.Select(c => c.Evaluate(new ConditionContext(now))), setter.Match);
    }

    /// <summary>Whether any of the setter's instants fired after <paramref name="sinceUtc"/> and up to now.</summary>
    public static bool TriggerFired(Setter setter, DateTime sinceUtc, SensorSnapshot now) =>
        setter.When.OfType<TimeWindowCondition>().Any(t => t.AtTime && t.FiredSince(sinceUtc, now) is not null);

    public static bool StopConditionMet(Setter setter, SetterRun run, SensorSnapshot now) =>
        setter.Until.Any(c => c.Evaluate(run.Context(now)) == true);

    /// <summary>
    /// True when the start conditions stopped holding at a clock boundary after <paramref name="sinceUtc"/>.
    /// Set Sett sees a boundary as it passes while the PC is awake, so this finds the ends it slept
    /// through. Only the clock can be replayed: what a sensor saw back then is unknown, so a sensor
    /// condition never proves an end.
    /// </summary>
    public static bool ConditionsEndedSince(Setter setter, DateTime sinceUtc, SensorSnapshot now)
    {
        List<Condition> clock = [.. setter.When.Where(c => c.CanStart && !c.IsTrigger && c.Sensors.HasFlag(Sensors.Clock))];
        DateTime at = sinceUtc;
        for (int i = 0; clock.Count > 0 && i < MaxReplayedBoundaries; i++)
        {
            var context = new ConditionContext(now with { UtcNow = at });
            DateTime? next = clock.Select(c => c.NextChange(context)).Where(change => change > at).Min();
            if (next is not { } boundary || boundary > now.UtcNow)
            {
                return false;
            }

            at = boundary;
            context = new ConditionContext(now with { UtcNow = at });
            bool? held = Combine(
                setter.When.Where(c => c.CanStart && !c.IsTrigger).Select(c => c.Sensors.HasFlag(Sensors.Clock) ? c.Evaluate(context) : null),
                setter.Match);
            if (held == false)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What should start and stop now. Setters act on edges: an automatic setter starts when its
    /// conditions become true; one stopped early (latched) waits for them to end first. An end the PC
    /// slept through still counts: a run from an earlier window starts afresh, and a latch clears. An
    /// unknown answer changes nothing, so a sensor that hasn't reported yet never flips a setter.
    /// A setter with an instant ("at 9:00 AM") starts when the clock crosses it, if its other conditions
    /// hold then, and the instant passing doesn't stop it. <paramref name="triggersSince"/> says, per setter,
    /// when its instants were last checked; a setter missing from it fires nothing this step.
    /// </summary>
    public static (IReadOnlyList<SetterTransition> Transitions, RunState State) Step(
        IReadOnlyList<Setter> setters, RunState state, SensorSnapshot now, IReadOnlyDictionary<string, DateTime>? triggersSince = null)
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
                state = state.WithoutLatch(latched);
            }
        }

        foreach (Setter setter in setters)
        {
            bool? holds = setter.IsAutomatic ? StartConditionsHold(setter, now) : null;
            bool trigger = setter.HasTrigger;
            bool fired = trigger && triggersSince is not null && triggersSince.TryGetValue(setter.Id, out DateTime since0) &&
                TriggerFired(setter, since0, now);
            SetterRun? run = state.RunOf(setter.Id);
            if (run is not null)
            {
                if (!setter.CanRun)
                {
                    transitions.Add(new(setter.Id, false, StopReason.Edited));
                }
                else if (!setter.Enabled)
                {
                    transitions.Add(new(setter.Id, false, StopReason.Disabled));
                }
                else if (!trigger && holds == false)
                {
                    transitions.Add(new(setter.Id, false, StopReason.ConditionsEnded));
                }
                else if (trigger && fired && holds == true)
                {
                    // Its time came round again while it's still on: a fresh run, so its timer starts over.
                    transitions.Add(new(setter.Id, true, Renew: true));
                }
                else if (!trigger && holds == true && ConditionsEndedSince(setter, run.ActiveSinceUtc, now))
                {
                    // Before the stop conditions: a timer left over from the earlier window must not end this one.
                    transitions.Add(new(setter.Id, true, Renew: true));
                }
                else if (StopConditionMet(setter, run, now))
                {
                    transitions.Add(new(setter.Id, false, StopReason.Until));

                    // An instant has no conditions to wait out: the next one starts it again.
                    if (setter.IsAutomatic && !trigger)
                    {
                        state = state.WithLatch(setter.Id, now.UtcNow);
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
                state = state.WithoutLatch(setter.Id);
                continue;
            }

            if (trigger)
            {
                // A pause ends when the conditions next begin, and for an instant that is its next firing.
                if (fired && holds == true)
                {
                    state = state.WithoutLatch(setter.Id);
                    transitions.Add(new(setter.Id, true, Once: setter.AppliesOnce));
                }

                continue;
            }

            if (state.IsLatched(setter.Id))
            {
                bool ended = holds == false ||
                    (holds == true && state.LatchedSinceUtc(setter.Id) is { } since && ConditionsEndedSince(setter, since, now));
                if (!ended)
                {
                    continue;
                }

                state = state.WithoutLatch(setter.Id);
            }

            if (holds == true)
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
