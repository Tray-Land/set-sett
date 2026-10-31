using SetSett.Model;

namespace SetSett.Engine;

/// <summary>
/// The put-back rules, as pure functions over <see cref="RunState.Claims"/>.
/// <list type="bullet">
/// <item>A setter that sets a value records a claim: the value before (snapshot) and the value it set.</item>
/// <item>When it stops, it writes its snapshot back, but only if the setting still reads as the value
/// it set. If anyone changed the setting in the meantime, every claim on it is void: the user's value
/// stands and nothing is put back.</item>
/// <item>Claims stack. The most recent setter's value is in effect; when a setter underneath stops
/// first, the one above inherits its snapshot, so the last one to stop puts back what was there
/// before either started.</item>
/// </list>
/// </summary>
public static class ClaimLedger
{
    /// <summary>
    /// Records that <paramref name="setterId"/> is about to set <paramref name="applied"/>;
    /// <paramref name="current"/> is what the setting reads now (null when it can't be read, in
    /// which case nothing is recorded because there would be nothing reliable to put back).
    /// </summary>
    public static RunState Apply(RunState state, string settingId, string setterId, SettingValue? current, SettingValue applied)
    {
        if (current is null)
        {
            return state;
        }

        List<Claim> stack = [.. state.ClaimsOn(settingId)];

        // Changed since the claim in effect set it: the older claims are void.
        if (stack.Count > 0 && !stack[^1].Applied.Matches(current))
        {
            stack.Clear();
        }

        int existing = stack.FindIndex(c => c.SetterId == setterId);
        if (existing == stack.Count - 1 && existing >= 0)
        {
            // Setting again while on top (a restart re-applying): keep the original snapshot.
            stack[existing] = stack[existing] with { Applied = applied };
            return state.WithClaims(settingId, stack);
        }

        if (existing >= 0)
        {
            stack = Withdraw(stack, existing);
        }

        stack.Add(new Claim(setterId, current, applied));
        return state.WithClaims(settingId, stack);
    }

    /// <summary>
    /// <paramref name="setterId"/> stopped. Returns the new state and the value to write back, or
    /// null to leave the setting alone (someone changed it, a setter above still holds it, or there
    /// was no claim).
    /// </summary>
    public static (RunState State, SettingValue? PutBack) Release(RunState state, string settingId, string setterId, SettingValue? current)
    {
        List<Claim> stack = [.. state.ClaimsOn(settingId)];
        int index = stack.FindIndex(c => c.SetterId == setterId);
        if (index < 0)
        {
            return (state, null);
        }

        if (index < stack.Count - 1)
        {
            return (state.WithClaims(settingId, Withdraw(stack, index)), null);
        }

        Claim top = stack[index];
        if (current is null || !top.Applied.Matches(current))
        {
            // Changed by someone else (or unreadable, so unprovable): leave it as it is.
            return (state.WithClaims(settingId, []), null);
        }

        stack.RemoveAt(index);
        return (state.WithClaims(settingId, stack), top.Snapshot);
    }

    /// <summary>
    /// Drops the claim of a setter whose write failed, without putting anything back (the
    /// setting never changed).
    /// </summary>
    public static RunState Discard(RunState state, string settingId, string setterId)
    {
        List<Claim> stack = [.. state.ClaimsOn(settingId)];
        int index = stack.FindIndex(c => c.SetterId == setterId);
        return index < 0 ? state : state.WithClaims(settingId, Withdraw(stack, index));
    }

    /// <summary>Voids every claim on a setting whose value no longer matches the claim in effect (startup check).</summary>
    public static RunState Verify(RunState state, string settingId, SettingValue? current)
    {
        IReadOnlyList<Claim> stack = state.ClaimsOn(settingId);
        return stack.Count > 0 && (current is null || !stack[^1].Applied.Matches(current))
            ? state.WithClaims(settingId, [])
            : state;
    }

    // Removes a claim; the claim above it (if any) inherits its snapshot so it puts back the older value.
    private static List<Claim> Withdraw(List<Claim> stack, int index)
    {
        if (index < stack.Count - 1)
        {
            stack[index + 1] = stack[index + 1] with { Snapshot = stack[index].Snapshot };
        }

        stack.RemoveAt(index);
        return stack;
    }
}
