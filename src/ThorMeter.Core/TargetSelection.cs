using System.Globalization;

namespace ThorMeter.Core;

public static class TargetSelection
{
    /// <summary>Highest-damage NPC or unresolved target, for a user-invoked suggestion only.</summary>
    public static TargetStatistics? MainCandidate(IEnumerable<TargetStatistics> targets) => targets
        .Where(t => t.Damage > 0 && t.Kind is EntityKind.Npc or EntityKind.Unknown)
        .OrderByDescending(t => t.Damage).ThenBy(t => t.EntityId).FirstOrDefault();

    public static bool Matches(EntityKind kind, string label, ulong id, EntityKind? category, string? query)
    {
        if (category is not null && kind != category) return false;
        var term = query?.Trim();
        return string.IsNullOrEmpty(term) || label.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || id.ToString(CultureInfo.InvariantCulture).Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
