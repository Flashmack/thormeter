namespace ThorMeter.Core;

public enum RankMetric { Dps, Hps, Damage, Healing }
public sealed record RankedPlayer(PlayerStatistics Player, int? Rank, bool IsPinned);

public static class PlayerRanking
{
    public static IReadOnlyList<RankedPlayer> Rank(IEnumerable<PlayerStatistics> players,
        IReadOnlySet<ulong> pinnedIds, RankMetric metric)
    {
        var hasPins = pinnedIds.Count > 0;
        int rank = 0;
        return players.OrderByDescending(p => pinnedIds.Contains(p.EntityId))
            .ThenByDescending(p => metric switch
            {
                RankMetric.Hps => p.Hps,
                RankMetric.Damage => p.Damage,
                RankMetric.Healing => p.Healing,
                _ => p.Dps
            })
            .ThenBy(p => p.EntityId)
            .Select(p => new RankedPlayer(p, !hasPins || pinnedIds.Contains(p.EntityId) ? ++rank : null,
                pinnedIds.Contains(p.EntityId))).ToArray();
    }
}
