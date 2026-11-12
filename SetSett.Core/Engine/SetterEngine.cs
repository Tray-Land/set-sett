using SetSett.Diagnostics;
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
        Log.Info("Engine", $"Recovering: {State.Active.Count} active run(s), {State.Claims.Count} claimed setting(s)");

        foreach (string settingId in State.Claims.Keys.ToList())
        {
            ISetting? setting = catalog.Find(settingId);
            if (setting is null || setting.Info.Holder == ValueHolder.SetSett)
            {
                Log.Info("Engine", $"Recovery dropped the claims on {settingId} ({(setting is null ? "unknown setting" : "held by Set Sett itself")})");
                State = State.WithClaims(settingId, []);
                continue;
            }

            int before = State.Claims.TryGetValue(settingId, out var claims) ? claims.Count : 0;
            State = ClaimLedger.Verify(State, settingId, await setting.ReadAsync());
            int after = State.Claims.TryGetValue(settingId, out var kept) ? kept.Count : 0;
            if (after != before)
            {
                Log.Warn("Engine", $"Recovery voided the claims on {settingId}: the setting changed while Set Sett wasn't running");
            }
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
        var (transitions, state) = SetterPolicy.Step(setters, State, now, TriggerWindows(setters, now));
        if (!ReferenceEquals(state, State))
        {
            State = state;
            store.Save(State);
        }

        foreach (SetterTransition transition in transitions)
        {
            Log.Info("Engine", transition.Renew
                ? $"Step: renew {Name(setters, transition.SetterId)}"
                : transition.Start
                    ? $"Step: start {Name(setters, transition.SetterId)}"
                    : $"Step: stop {Name(setters, transition.SetterId)} ({transition.Reason})");
            if (transition.Renew)
            {
                await RenewAsync(setters.First(s => s.Id == transition.SetterId), now);
            }
            else if (transition.Once)
            {
                await ApplyOnceAsync(setters.First(s => s.Id == transition.SetterId));
            }
            else if (transition.Start)
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

    /// <summary>
    /// When each setter's instants ("at 9:00 AM") were last checked, so a Step only fires the ones that
    /// passed since. In memory: after a restart nothing earlier fires. A setter new to this, or whose
    /// start conditions were just edited, counts from now, so adding "at 9:00 AM" at 9:03 doesn't fire it.
    /// </summary>
    private Dictionary<string, (DateTime CheckedUtc, IReadOnlyList<Condition> When)> _checked = [];

    private Dictionary<string, DateTime> TriggerWindows(IReadOnlyList<Setter> setters, SensorSnapshot now)
    {
        Dictionary<string, DateTime> since = [];
        Dictionary<string, (DateTime, IReadOnlyList<Condition>)> next = [];
        foreach (Setter setter in setters.Where(s => s.HasTrigger))
        {
            bool unchanged = _checked.TryGetValue(setter.Id, out var seen) && seen.When.SequenceEqual(setter.When);
            since[setter.Id] = unchanged ? seen.CheckedUtc : now.UtcNow;
            next[setter.Id] = (now.UtcNow, setter.When);
        }

        _checked = next;
        return since;
    }

    /// <summary>A click-to-toggle setter or an instant with no stop condition: changes values without a run or put-back.</summary>
    private async Task ApplyOnceAsync(Setter setter)
    {
        if (!setter.CanRun || !setter.Enabled)
        {
            return;
        }

        Log.Info("Engine", $"Applying {setter.Name} [{setter.Id}] once: {setter.Changes.Count} change(s)");
        await ApplyChangesAsync(setter, _ => true, claim: false);
    }

    /// <summary>Starts a run with saved snapshots, or switches a click-to-toggle setter once with no run.</summary>
    public Task StartAsync(Setter setter, SensorSnapshot now) => setter.IsToggle
        ? ApplyOnceAsync(setter)
        : StartAsync(setter, new SetterRun(setter.Id, now.UtcNow));

    /// <summary>
    /// A length picked from the setter's tray icon menu: starts it to stop that long from now, in place
    /// of its own "after a while" timer. If it's already on, only the time it stops changes; nothing is
    /// set again.
    /// </summary>
    public async Task StartForAsync(Setter setter, SensorSnapshot now, TimeSpan length)
    {
        if (setter.IsToggle)
        {
            Log.Warn("Engine", $"Timed start of {setter.Name} [{setter.Id}] ignored: click-to-toggle setters have no run");
            return;
        }

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
            Log.Debug("Engine", $"Start of {setter.Name} [{setter.Id}] ignored (canRun={setter.CanRun}, enabled={setter.Enabled}, active={State.IsActive(setter.Id)})");
            return;
        }

        Log.Info("Engine", $"Starting {setter.Name} [{setter.Id}]: {setter.Changes.Count} change(s), putBack={setter.PutBack}");
        State = State.WithRun(run).WithoutLatch(setter.Id);
        store.Save(State);
        await ApplyChangesAsync(setter, _ => true);
    }

    /// <summary>
    /// A running setter's conditions ended and began again unseen, so this is a new run. A setting that
    /// still has the setter's value keeps its snapshot and isn't written, so nothing flickers. One that
    /// changed is set again, with what it reads now as the snapshot, the same as entering a window.
    /// </summary>
    private async Task RenewAsync(Setter setter, SensorSnapshot now)
    {
        State = State.WithRun(new SetterRun(setter.Id, now.UtcNow));
        store.Save(State);
        await ApplyChangesAsync(setter, _ => true, skipIfAlreadySet: true);
    }

    /// <summary>
    /// Stops a setter and puts back each setting it holds a claim on (from the claims, not its
    /// current definition, so a deleted or edited setter still puts back what it changed).
    /// </summary>
    public Task StopAsync(string setterId, StopReason reason) => StopAsync(setterId, reason, pausedAtUtc: null);

    /// <summary>The user paused an automatic setter: it stops until its conditions next begin, or until it's resumed.</summary>
    public Task PauseAsync(string setterId, SensorSnapshot now) => StopAsync(setterId, StopReason.User, now.UtcNow);

    /// <summary>Ends a pause, and starts the setter straight away if its conditions hold.</summary>
    public Task<IReadOnlyList<SetterTransition>> ResumeAsync(IReadOnlyList<Setter> setters, string setterId, SensorSnapshot now)
    {
        State = State.WithoutLatch(setterId);
        store.Save(State);
        return StepAsync(setters, now);
    }

    private async Task StopAsync(string setterId, StopReason reason, DateTime? pausedAtUtc)
    {
        Log.Info("Engine", $"Stopping {setterId} ({reason}{(pausedAtUtc is null ? "" : ", paused")})");
        foreach (string settingId in State.SettingsClaimedBy(setterId).ToList())
        {
            ISetting? setting = catalog.Find(settingId);
            SettingValue? current = setting is null ? null : await setting.ReadAsync();
            (State, SettingValue? putBack) = ClaimLedger.Release(State, settingId, setterId, current);

            // Write first, then save: a crash in between leaves a claim whose value no longer
            // matches, which the next start treats as void. The other order could lose the put-back.
            if (putBack is null)
            {
                Log.Info("Engine", $"No put-back for {settingId} (setting changed since, or another setter holds it)");
            }
            else if (setting is not null && !await setting.WriteAsync(putBack))
            {
                Log.Warn("Engine", $"Put-back of {settingId} failed");
                _problems.Add(new SettingProblem(setterId, settingId, DateTime.UtcNow));
            }
            else
            {
                Log.Info("Engine", $"Put back {settingId}");
            }

            store.Save(State);
        }

        State = State.WithoutRun(setterId);
        if (pausedAtUtc is { } pausedAt)
        {
            State = State.WithLatch(setterId, pausedAt, paused: true);
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

        State = State.WithoutLatches();
        store.Save(State);
    }

    private static string Name(IReadOnlyList<Setter> setters, string id) =>
        setters.FirstOrDefault(s => s.Id == id) is { } s ? $"{s.Name} [{id}]" : id;

    private async Task ApplyChangesAsync(Setter setter, Func<SettingChange, bool> include, bool skipIfAlreadySet = false, bool claim = true)
    {
        bool hold = claim && setter.PutBack;
        // Unavailable family members wait until the loop ends: a PC without a battery has no
        // "on battery" member by design, which is fine when its sibling was applied.
        List<(string SettingId, string Family)> unavailableMembers = [];
        HashSet<string> appliedFamilies = [];
        foreach (SettingChange change in setter.Changes.Where(include))
        {
            ISetting? setting = catalog.Find(change.SettingId);
            if (setting is null || (setter.IsToggle ? !setting.Info.CanToggle : !setting.Info.Accepts(change.Value)))
            {
                // Skipped, not silent: report it like a refused write so the flyout can say so.
                Log.Warn("Engine", $"{change.SettingId} skipped for {setter.Name}: {(setting is null ? "unknown setting" : setter.IsToggle ? "setting cannot be toggled" : "value not accepted")}");
                _problems.Add(new SettingProblem(setter.Id, change.SettingId, DateTime.UtcNow));
                continue;
            }

            if (!await setting.IsAvailableAsync())
            {
                Log.Warn("Engine", $"{change.SettingId} is unavailable on this PC (setter {setter.Name})");
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

            SettingValue? current = hold || skipIfAlreadySet || setter.IsToggle ? await setting.ReadAsync() : null;
            SettingValue? value = setter.IsToggle ? setting.Info.OtherValue(current) : change.Value;
            if (value is null)
            {
                Log.Warn("Engine", $"{change.SettingId} could not be toggled for {setter.Name}: current value is unreadable or invalid");
                _problems.Add(new SettingProblem(setter.Id, change.SettingId, DateTime.UtcNow));
                continue;
            }

            if (hold)
            {
                State = ClaimLedger.Apply(State, change.SettingId, setter.Id, current, value);
                store.Save(State); // The snapshot is on disk before Windows changes.
            }

            if (skipIfAlreadySet && value.Matches(current))
            {
                Log.Debug("Engine", $"{change.SettingId} already set; not written");
                continue;
            }

            if (await setting.WriteAsync(value))
            {
                Log.Info("Engine", $"Set {change.SettingId} for {setter.Name}");
                if (setter.IsToggle && State.ClaimsOn(change.SettingId).Count > 0)
                {
                    // This is a lasting user change, even if a later click returns to a claimed value.
                    State = State.WithClaims(change.SettingId, []);
                    store.Save(State);
                }
            }
            else
            {
                Log.Warn("Engine", $"Windows refused {change.SettingId} for {setter.Name}");
                _problems.Add(new SettingProblem(setter.Id, change.SettingId, DateTime.UtcNow));
                if (hold)
                {
                    State = ClaimLedger.Discard(State, change.SettingId, setter.Id);
                    store.Save(State);
                }
            }
        }

        foreach ((string settingId, _) in unavailableMembers.Where(m => !appliedFamilies.Contains(m.Family)))
        {
            _problems.Add(new SettingProblem(setter.Id, settingId, DateTime.UtcNow));
        }
    }
}
