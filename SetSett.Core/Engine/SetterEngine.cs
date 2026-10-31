using SetSett.Model;

namespace SetSett.Engine;

/// <summary>Where the <see cref="RunState"/> lives between runs (a file in the app; memory in tests).</summary>
public interface IRunStateStore
{
    RunState Load();

    void Save(RunState state);
}

/// <summary>A setting that couldn't be changed, for the flyout to mention.</summary>
public sealed record SettingProblem(string SetterId, string SettingId, DateTime AtUtc);

/// <summary>
/// Carries out starts and stops against the settings, keeping the claims (snapshots) on disk so a
/// crash or restart can be recovered (<see cref="RecoverAsync"/>). Not thread-safe: the app calls
/// it from one place, one call at a time.
/// </summary>
public sealed class SetterEngine(ISettingCatalog catalog, IRunStateStore store)
{
    private readonly List<SettingProblem> _problems = [];

    public RunState State { get; private set; } = RunState.Empty;

    /// <summary>Settings that refused a change since the last <see cref="ClearProblems"/>.</summary>
    public IReadOnlyList<SettingProblem> Problems => _problems;

    public void ClearProblems() => _problems.Clear();

    /// <summary>
    /// Startup: loads the saved state and makes it true again. Claims on settings someone changed
    /// while Set Sett wasn't running are void; claims on values only Set Sett's process held (keep
    /// awake) died with that process and are re-applied for setters that are still on. Then a normal
    /// <see cref="StepAsync"/> stops whatever ended meanwhile (an expired timer puts its settings back).
    /// </summary>
    public async Task<IReadOnlyList<SetterTransition>> RecoverAsync(IReadOnlyList<Setter> setters, SensorSnapshot now)
    {
        State = store.Load();

        foreach (string settingId in State.Claims.Keys.ToList())
        {
            ISetting? setting = catalog.Find(settingId);
            if (setting is null || setting.Info.Holder == ValueHolder.SetSett)
            {
                State = State.WithClaims(settingId, []);
                continue;
            }

            State = ClaimLedger.Verify(State, settingId, await setting.ReadAsync());
        }

        foreach (SetterRun run in State.Active.ToList())
        {
            if (setters.FirstOrDefault(s => s.Id == run.SetterId) is { } setter)
            {
                await ApplyChangesAsync(setter, c => catalog.Find(c.SettingId)?.Info.Holder == ValueHolder.SetSett);
            }
        }

        store.Save(State);
        return await StepAsync(setters, now);
    }

    /// <summary>Evaluates every setter and starts or stops the ones whose moment has come.</summary>
    public async Task<IReadOnlyList<SetterTransition>> StepAsync(IReadOnlyList<Setter> setters, SensorSnapshot now)
    {
        var (transitions, state) = SetterPolicy.Step(setters, State, now);
        if (!ReferenceEquals(state, State))
        {
            State = state;
            store.Save(State);
        }

        foreach (SetterTransition transition in transitions)
        {
            if (transition.Start)
            {
                await StartAsync(setters.First(s => s.Id == transition.SetterId), now);
            }
            else
            {
                await StopAsync(transition.SetterId, transition.Reason);
            }
        }

        return transitions;
    }

    /// <summary>Starts a setter: snapshots each setting, saves the claim, then sets the value.</summary>
    public Task StartAsync(Setter setter, SensorSnapshot now) => StartAsync(setter, new SetterRun(setter.Id, now.UtcNow));

    /// <summary>
    /// A length picked from the setter's tray icon menu: starts it to stop that long from now, in place
    /// of its own "after a while" timer. If it's already on, only the time it stops changes; nothing is
    /// set again.
    /// </summary>
    public async Task StartForAsync(Setter setter, SensorSnapshot now, TimeSpan length)
    {
        DateTime ends = now.UtcNow + length;
        if (State.RunOf(setter.Id) is { } running)
        {
            State = State.WithRun(running with { TimerEndsUtc = ends });
            store.Save(State);
            return;
        }

        await StartAsync(setter, new SetterRun(setter.Id, now.UtcNow) { TimerEndsUtc = ends });
    }

    private async Task StartAsync(Setter setter, SetterRun run)
    {
        if (!setter.CanRun || !setter.Enabled || State.IsActive(setter.Id))
        {
            return;
        }

        State = State.WithRun(run).WithLatch(setter.Id, false);
        store.Save(State);
        await ApplyChangesAsync(setter, _ => true);
    }

    /// <summary>
    /// Stops a setter and puts back each setting it holds a claim on (from the claims, not its
    /// current definition, so a deleted or edited setter still puts back what it changed).
    /// </summary>
    /// <param name="latch">For an automatic setter stopped by the user: wait for its conditions to end before it can start again.</param>
    public async Task StopAsync(string setterId, StopReason reason, bool latch = false)
    {
        foreach (string settingId in State.SettingsClaimedBy(setterId).ToList())
        {
            ISetting? setting = catalog.Find(settingId);
            SettingValue? current = setting is null ? null : await setting.ReadAsync();
            (State, SettingValue? putBack) = ClaimLedger.Release(State, settingId, setterId, current);

            // Write first, then save: a crash in between leaves a claim whose value no longer
            // matches, which the next start treats as void. The other order could lose the put-back.
            if (putBack is not null && setting is not null && !await setting.WriteAsync(putBack))
            {
                _problems.Add(new SettingProblem(setterId, settingId, DateTime.UtcNow));
            }

            store.Save(State);
        }

        State = State.WithoutRun(setterId);
        if (latch)
        {
            State = State.WithLatch(setterId, true);
        }

        store.Save(State);
    }

    /// <summary>Exit: stops every running setter, putting settings back, and forgets latches.</summary>
    public async Task StopAllAsync()
    {
        foreach (SetterRun run in State.Active.ToList())
        {
            await StopAsync(run.SetterId, StopReason.Exit);
        }

        State = State with { Latched = [] };
        store.Save(State);
    }

    private async Task ApplyChangesAsync(Setter setter, Func<SettingChange, bool> include)
    {
        // Unavailable family members wait until the loop ends: a PC without a battery has no
        // "on battery" member by design, which is fine when its sibling was applied.
        List<(string SettingId, string Family)> unavailableMembers = [];
        HashSet<string> appliedFamilies = [];
        foreach (SettingChange change in setter.Changes.Where(include))
        {
            ISetting? setting = catalog.Find(change.SettingId);
            if (setting is null || !setting.Info.Accepts(change.Value))
            {
                // Skipped, not silent: report it like a refused write so the flyout can say so.
                _problems.Add(new SettingProblem(setter.Id, change.SettingId, DateTime.UtcNow));
                continue;
            }

            if (!await setting.IsAvailableAsync())
            {
                if (setting.Info.Family is { } family)
                {
                    unavailableMembers.Add((change.SettingId, family));
                }
                else
                {
                    _problems.Add(new SettingProblem(setter.Id, change.SettingId, DateTime.UtcNow));
                }

                continue;
            }

            if (setting.Info.Family is { } available)
            {
                appliedFamilies.Add(available);
            }

            if (setter.PutBack)
            {
                SettingValue? current = await setting.ReadAsync();
                State = ClaimLedger.Apply(State, change.SettingId, setter.Id, current, change.Value);
                store.Save(State); // The snapshot is on disk before Windows changes.
            }

            if (!await setting.WriteAsync(change.Value))
            {
                _problems.Add(new SettingProblem(setter.Id, change.SettingId, DateTime.UtcNow));
                State = ClaimLedger.Discard(State, change.SettingId, setter.Id);
                store.Save(State);
            }
        }

        foreach ((string settingId, _) in unavailableMembers.Where(m => !appliedFamilies.Contains(m.Family)))
        {
            _problems.Add(new SettingProblem(setter.Id, settingId, DateTime.UtcNow));
        }
    }
}
