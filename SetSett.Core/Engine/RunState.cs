using SetSett.Model;

namespace SetSett.Engine;

/// <summary>A running setter and when it started.</summary>
public sealed record SetterRun(string SetterId, DateTime ActiveSinceUtc)
{
    /// <summary>
    /// For a run started (or re-timed) for a chosen length from the tray icon's menu: when it ends,
    /// in place of the setter's own "after a while" timer. Null for a run on the setter's own terms.
    /// </summary>
    public DateTime? TimerEndsUtc { get; set; }

    /// <summary>What this run's stop conditions are evaluated against.</summary>
    public ConditionContext Context(SensorSnapshot now) => new(now, ActiveSinceUtc, TimerEndsUtc);

    /// <summary>
    /// When this run ends by the clock: the soonest of its setter's "after a while" and "at a time"
    /// stops (a length picked from the tray menu included). Null when only a sensor can stop it.
    /// </summary>
    public DateTime? EndsAtUtc(Setter setter, SensorSnapshot now)
    {
        ConditionContext context = Context(now);
        return setter.Until.Select(c => c switch
        {
            ElapsedCondition e => e.EndsAtUtc(context),
            ClockCondition k => k.EndsAtUtc(context),
            _ => null,
        }).Min();
    }
}

/// <summary>
/// A setter's hold on one setting: what the setting was just before (<see cref="Snapshot"/>) and
/// what the setter set it to (<see cref="Applied"/>). Putting back writes the snapshot, but only
/// while the setting still reads as <see cref="Applied"/>.
/// </summary>
public sealed record Claim(string SetterId, SettingValue Snapshot, SettingValue Applied);

/// <summary>
/// Everything Set Sett needs to pick up where it left off after a crash or a restart: which setters
/// are running, which are waiting for their conditions to reset, and every claim on a setting. It is
/// saved to disk before Set Sett changes a setting, so a crash mid-change never loses a snapshot.
/// </summary>
public sealed record RunState
{
    public static readonly RunState Empty = new();

    public IReadOnlyList<SetterRun> Active { get; set; } = [];

    /// <summary>
    /// Automatic setters that stopped (by a stop condition, or by the user) while their start
    /// conditions still held. They wait for those conditions to end before they can start again.
    /// </summary>
    public IReadOnlyList<string> Latched { get; set; } = [];

    /// <summary>Per setting id, its claims from bottom (first) to top (most recent, the one in effect).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Claim>> Claims { get; set; } = new Dictionary<string, IReadOnlyList<Claim>>();

    public SetterRun? RunOf(string setterId) => Active.FirstOrDefault(r => r.SetterId == setterId);

    public bool IsActive(string setterId) => RunOf(setterId) is not null;

    public bool IsLatched(string setterId) => Latched.Contains(setterId);

    public IReadOnlyList<Claim> ClaimsOn(string settingId) => Claims.GetValueOrDefault(settingId) ?? [];

    /// <summary>The settings this setter holds a claim on.</summary>
    public IEnumerable<string> SettingsClaimedBy(string setterId) =>
        Claims.Where(pair => pair.Value.Any(c => c.SetterId == setterId)).Select(pair => pair.Key);

    public RunState WithRun(SetterRun run) => this with { Active = [.. Active.Where(r => r.SetterId != run.SetterId), run] };

    public RunState WithoutRun(string setterId) => this with { Active = [.. Active.Where(r => r.SetterId != setterId)] };

    // Returns this instance when nothing changes, so callers can skip a save.
    public RunState WithLatch(string setterId, bool latched) => Latched.Contains(setterId) == latched
        ? this
        : this with { Latched = latched ? [.. Latched, setterId] : [.. Latched.Where(id => id != setterId)] };

    public RunState WithClaims(string settingId, IReadOnlyList<Claim> claims)
    {
        var all = new Dictionary<string, IReadOnlyList<Claim>>(Claims);
        if (claims.Count == 0)
        {
            all.Remove(settingId);
        }
        else
        {
            all[settingId] = claims;
        }

        return this with { Claims = all };
    }
}
