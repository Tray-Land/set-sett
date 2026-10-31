namespace SetSett.Model;

/// <summary>
/// One line of a setter's "what it changes" list. A family member on its own is one change; a
/// family with every member set to the same value ("Always") is all of them as one line.
/// </summary>
/// <param name="Indexes">Positions in <see cref="Setter.Changes"/> this line stands for.</param>
/// <param name="Family">The family id, or null for a setting outside any family.</param>
/// <param name="Members">The family's setting ids this line covers (one, or all of them for Always).</param>
public sealed record ChangeLine(IReadOnlyList<int> Indexes, string? Family, IReadOnlyList<string> Members)
{
    public bool IsAlways(int familySize) => Family is not null && familySize > 1 && Members.Count == familySize;
}

/// <summary>
/// Pure rules for settings that come in a family (see <see cref="SettingInfo.Family"/>), kept out of
/// the UI so they're tested: which changes show as one line, and how a line's scope is changed.
/// </summary>
public static class SettingFamilies
{
    /// <summary>The ids of a family's members, in catalog order.</summary>
    public static IReadOnlyList<string> Members(ISettingCatalog catalog, string family) =>
        [.. catalog.All.Where(s => s.Info.Family == family).Select(s => s.Info.Id)];

    /// <summary>
    /// Groups changes into lines. Members of a family collapse into one line when the setter changes
    /// every member to the same value; otherwise each member keeps its own line.
    /// </summary>
    public static IReadOnlyList<ChangeLine> Lines(IReadOnlyList<SettingChange> changes, ISettingCatalog catalog)
    {
        List<ChangeLine> lines = [];
        HashSet<string> merged = [];
        for (int i = 0; i < changes.Count; i++)
        {
            string? family = catalog.Find(changes[i].SettingId)?.Info.Family;
            if (family is null)
            {
                lines.Add(new ChangeLine([i], null, [changes[i].SettingId]));
                continue;
            }

            if (merged.Contains(family))
            {
                continue;
            }

            IReadOnlyList<string> members = Members(catalog, family);
            int[] all = [.. members.Select(m => changes.ToList().FindIndex(c => string.Equals(c.SettingId, m, StringComparison.OrdinalIgnoreCase)))];
            bool everyone = members.Count > 1 && all.All(x => x >= 0) && all.All(x => changes[x].Value.Equals(changes[i].Value));
            if (everyone)
            {
                merged.Add(family);
                lines.Add(new ChangeLine([.. all.Order()], family, members));
            }
            else
            {
                lines.Add(new ChangeLine([i], family, [changes[i].SettingId]));
            }
        }

        return lines;
    }

    /// <summary>The members of a family that no line other than <paramref name="line"/> already uses.</summary>
    public static IReadOnlyList<string> FreeMembers(IReadOnlyList<SettingChange> changes, ISettingCatalog catalog, ChangeLine line)
    {
        if (line.Family is null)
        {
            return [];
        }

        HashSet<string> taken = [.. changes.Where((_, i) => !line.Indexes.Contains(i)).Select(c => c.SettingId)];
        return [.. Members(catalog, line.Family).Where(m => !taken.Contains(m))];
    }

    /// <summary>
    /// Points a line at other members of its family (all of them for Always), keeping its value and
    /// its place in the list.
    /// </summary>
    public static IReadOnlyList<SettingChange> Rescope(IReadOnlyList<SettingChange> changes, ChangeLine line, IReadOnlyList<string> members)
    {
        SettingValue value = changes[line.Indexes[0]].Value;
        int at = line.Indexes.Min();
        List<SettingChange> result = [];
        for (int i = 0; i < changes.Count; i++)
        {
            if (i == at)
            {
                result.AddRange(members.Select(m => new SettingChange(m, value)));
            }

            if (!line.Indexes.Contains(i))
            {
                result.Add(changes[i]);
            }
        }

        return result;
    }

    /// <summary>Replaces the value of every change a line stands for.</summary>
    public static IReadOnlyList<SettingChange> Revalue(IReadOnlyList<SettingChange> changes, ChangeLine line, SettingValue value) =>
        [.. changes.Select((c, i) => line.Indexes.Contains(i) ? c with { Value = value } : c)];

    /// <summary>
    /// Adds a setting from the editor's menu. A family member adds every member not already used
    /// (Always, if none is), so the common case is one click and a second add fills in what's left.
    /// </summary>
    public static IReadOnlyList<SettingChange> Add(IReadOnlyList<SettingChange> changes, ISettingCatalog catalog, ISetting setting)
    {
        SettingInfo info = setting.Info;
        if (info.Family is null)
        {
            return [.. changes, new SettingChange(info.Id, info.DefaultValue)];
        }

        HashSet<string> taken = [.. changes.Select(c => c.SettingId)];
        return
        [
            .. changes,
            .. Members(catalog, info.Family).Where(m => !taken.Contains(m)).Select(m => new SettingChange(m, catalog.Find(m)!.Info.DefaultValue)),
        ];
    }

    /// <summary>True when a family still has a member the setter doesn't change, so the menu can offer it.</summary>
    public static bool HasFreeMember(IReadOnlyList<SettingChange> changes, ISettingCatalog catalog, string family)
    {
        HashSet<string> taken = [.. changes.Select(c => c.SettingId)];
        return Members(catalog, family).Any(m => !taken.Contains(m));
    }
}
