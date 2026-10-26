using ThorMeter.Core;

namespace ThorMeter.Tests;
public class CombatEngineTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static DamageEvent Hit(int seconds, ulong source = 1, long damage = 100) => new(Epoch.AddSeconds(seconds), source, 90, damage);

    [Fact]
    public void EachPlayerUsesTwoSecondIdleGapsWithoutOtherPlayersExtendingTime()
    {
        var engine = new CombatEngine();
        engine.Apply(Hit(0));
        Assert.Equal(0, engine.Snapshot(Epoch)[0].Dps);
        engine.Apply(Hit(1));
        engine.Apply(Hit(3, 2));
        engine.Apply(Hit(4, 2));
        Assert.Equal(200, engine.Snapshot(Epoch.AddSeconds(4)).Single(r => r.EntityId == 1).Dps);
        engine.Apply(Hit(20));
        engine.Apply(Hit(21));
        var rows = engine.Snapshot(Epoch.AddSeconds(27));
        Assert.Equal(3, engine.Duration(Epoch.AddSeconds(27)));
        Assert.Equal(200, rows.Single(r => r.EntityId == 1).Dps);
        Assert.Equal(200, rows.Single(r => r.EntityId == 2).Dps);
    }

    [Fact]
    public void IdentificationAndLateOwnershipPreserveAndReattributeDamage()
    {
        var engine = new CombatEngine();
        engine.Apply(Hit(0, 3));
        engine.Apply(Hit(1, 1));
        engine.Apply(new EntityMetadataEvent(Epoch, 1, "Flashmack", PlayerClass.Spiritmaster, EntityKind.Player, true));
        engine.Apply(new OwnershipEvent(Epoch, 3, 1));
        var row = Assert.Single(engine.Snapshot(Epoch.AddSeconds(6)));
        Assert.Equal(200, row.Damage);
        Assert.Equal("Flashmack", row.Name);
        Assert.True(row.IsLocalPlayer);
        engine.Reset();
        Assert.Empty(engine.Snapshot(Epoch));
        engine.Apply(Hit(10, 3));
        Assert.Equal("Flashmack", Assert.Single(engine.Snapshot(Epoch.AddSeconds(10))).Name);
    }

    [Fact]
    public void ConfirmedNpcIsExcludedAndLateNpcIdentificationRemovesRow()
    {
        var engine = new CombatEngine();
        engine.Apply(new EntityMetadataEvent(Epoch, 2, "Enemy", Kind: EntityKind.Npc));
        engine.Apply(Hit(0, 2));
        Assert.Empty(engine.Snapshot(Epoch));
        engine.Apply(Hit(1, 3));
        engine.Apply(new EntityMetadataEvent(Epoch, 3, "Another enemy", Kind: EntityKind.Npc));
        Assert.Empty(engine.Snapshot(Epoch.AddSeconds(2)));
    }

    [Fact]
    public void NewConnectionIsolatesReusedEntityIds()
    {
        var engine = new CombatEngine();
        engine.Apply(new ConnectionEvent(Epoch, "one", true));
        engine.Apply(new EntityMetadataEvent(Epoch, 1, "Old player", PlayerClass.Cleric));
        engine.Apply(Hit(0));
        engine.Apply(new ConnectionEvent(Epoch, "one", false));
        Assert.Equal("Old player", Assert.Single(engine.Snapshot(Epoch)).Name);
        engine.Apply(new ConnectionEvent(Epoch, "two", true));
        engine.Apply(Hit(1));
        var row = Assert.Single(engine.Snapshot(Epoch.AddSeconds(1)));
        Assert.StartsWith("Unknown", row.Name);
        Assert.Equal(100, row.Damage);
    }

    [Fact]
    public void InvalidAmountsLateEventsAndOwnershipCyclesDoNotCorruptTotals()
    {
        var engine = new CombatEngine();
        engine.Apply(Hit(2, damage: -1));
        engine.Apply(Hit(3));
        engine.Apply(Hit(1));
        engine.Apply(new OwnershipEvent(Epoch, 1, 2));
        engine.Apply(new OwnershipEvent(Epoch, 2, 1));
        var row = Assert.Single(engine.Snapshot(Epoch.AddSeconds(3)));
        Assert.Equal(2ul, row.EntityId);
        Assert.Equal(200, row.Damage);
    }

    [Fact]
    public void HealingUsesIndependentActiveTimeAndCanBeAttributedLate()
    {
        var engine = new CombatEngine();
        engine.Apply(Hit(0));
        engine.Apply(Hit(1));
        engine.Apply(new HealEvent(Epoch.AddSeconds(10), 3, 1, 100));
        engine.Apply(new HealEvent(Epoch.AddSeconds(11), 3, 3, 200));
        engine.Apply(new OwnershipEvent(Epoch, 3, 2));
        var rows = engine.Snapshot(Epoch.AddSeconds(17));
        Assert.Equal(1, engine.Duration(Epoch.AddSeconds(17)));
        Assert.Equal(1, engine.HealingDuration(Epoch.AddSeconds(17)));
        Assert.Equal(200, rows.Single(r => r.EntityId == 1).Dps);
        var healer = rows.Single(r => r.EntityId == 2);
        Assert.Equal(300, healer.Healing);
        Assert.Equal(300, healer.Hps);
    }

    [Fact]
    public void InferredClassCannotTurnConfirmedNpcIntoHealingPlayer()
    {
        var engine = new CombatEngine();
        engine.Apply(new EntityMetadataEvent(Epoch, 7, "Training target", Kind: EntityKind.Npc));
        engine.Apply(new EntityMetadataEvent(Epoch, 7, null, PlayerClass.Spiritmaster, EntityKind.Unknown));
        engine.Apply(new HealEvent(Epoch, 7, 7, 1_000_000));
        Assert.Empty(engine.Snapshot(Epoch.AddSeconds(1)));
    }

    [Fact]
    public void TargetFilterUsesTargetTimeWhileHealingStaysUnfiltered()
    {
        var engine = new CombatEngine();
        engine.Apply(Hit(0));
        engine.Apply(Hit(1));
        engine.Apply(new DamageEvent(Epoch.AddSeconds(1), 1, 91, 300));
        engine.Apply(new DamageEvent(Epoch.AddSeconds(2), 1, 91, 300));
        engine.Apply(new HealEvent(Epoch.AddSeconds(1), 1, 2, 40));
        engine.Apply(new HealEvent(Epoch.AddSeconds(2), 1, 2, 60));
        Assert.Equal(400, Assert.Single(engine.Snapshot(Epoch.AddSeconds(4))).Dps);
        var target90 = Assert.Single(engine.Snapshot(Epoch.AddSeconds(4), 90));
        Assert.Equal(200, target90.Damage);
        Assert.Equal(200, target90.Dps);
        Assert.Equal(100, target90.Healing);
        Assert.Equal(2, engine.Targets(Epoch.AddSeconds(4)).Count);
        engine.Reset();
        Assert.Empty(engine.Targets(Epoch.AddSeconds(4)));
    }

    [Fact]
    public void ResetEventClearsTotalsAndTargetsButPinnedRowsKeepIdentityAndOwnership()
    {
        var engine = new CombatEngine();
        engine.Apply(new EntityMetadataEvent(Epoch, 1, "Flashmack", PlayerClass.Spiritmaster, EntityKind.Player, true));
        engine.Apply(new OwnershipEvent(Epoch, 3, 1));
        engine.Apply(new EntityMetadataEvent(Epoch, 3, "Spirit", Kind: EntityKind.Pet));
        engine.Apply(new DamageEvent(Epoch, 3, 90, 400));
        engine.Apply(new HealEvent(Epoch, 1, 1, 200));
        engine.Apply(new ResetEvent(Epoch.AddSeconds(1)));

        var row = Assert.Single(engine.Snapshot(Epoch.AddSeconds(2), pinnedIds: [1]));
        Assert.Equal(1ul, row.EntityId);
        Assert.Equal("Flashmack", row.Name);
        Assert.Equal(PlayerClass.Spiritmaster, row.Class);
        Assert.Equal(EntityKind.Player, row.Kind);
        Assert.True(row.IsLocalPlayer);
        Assert.Equal(0, row.Damage);
        Assert.Equal(0, row.Healing);
        Assert.Empty(engine.Targets(Epoch.AddSeconds(2)));

        engine.Apply(new DamageEvent(Epoch.AddSeconds(3), 3, 91, 100));
        Assert.Equal("Flashmack", Assert.Single(engine.Snapshot(Epoch.AddSeconds(4))).Name);
    }

    [Fact]
    public void LatePetOwnerMergesOverlappingActivityIntervals()
    {
        var engine = new CombatEngine();
        engine.Apply(Hit(0, 1));
        engine.Apply(Hit(1, 1));
        engine.Apply(Hit(0, 3));
        engine.Apply(Hit(1, 3));
        engine.Apply(new OwnershipEvent(Epoch.AddSeconds(2), 3, 1));
        var row = Assert.Single(engine.Snapshot(Epoch.AddSeconds(4)));
        Assert.Equal(400, row.Damage);
        Assert.Equal(400, row.Dps);
    }

    [Fact]
    public void PauseDiscardsCombatButKeepsIdentityAndStartsNewInterval()
    {
        var engine = new CombatEngine();
        engine.Apply(Hit(0)); engine.Apply(Hit(1));
        engine.Apply(new PauseEvent(Epoch.AddSeconds(1), true));
        engine.Apply(Hit(2));
        engine.Apply(new EntityMetadataEvent(Epoch.AddSeconds(3), 1, "Known", Kind: EntityKind.Player));
        engine.Apply(new PauseEvent(Epoch.AddSeconds(10), false));
        engine.Apply(Hit(10)); engine.Apply(Hit(11));
        var row = Assert.Single(engine.Snapshot(Epoch.AddSeconds(13)));
        Assert.Equal("Known", row.Name);
        Assert.Equal(400, row.Damage);
        Assert.Equal(200, row.Dps);
    }

    [Fact]
    public void PinnedRankingKeepsOthersVisibleAndCanSortByHps()
    {
        PlayerStatistics[] players = [
            new(1, "One", PlayerClass.Cleric, EntityKind.Player, false, 100, 10, 300, 30),
            new(2, "Two", PlayerClass.Cleric, EntityKind.Player, false, 200, 20, 200, 20),
            new(3, "Three", PlayerClass.Cleric, EntityKind.Player, false, 300, 30, 500, 50)
        ];
        var ranked = PlayerRanking.Rank(players, new HashSet<ulong> { 1, 2 }, RankMetric.Hps);
        Assert.Equal([1ul, 2ul, 3ul], ranked.Select(r => r.Player.EntityId));
        Assert.Equal([1, 2, null], ranked.Select(r => r.Rank));
        Assert.Equal([3ul, 2ul, 1ul], PlayerRanking.Rank(players, new HashSet<ulong>(), RankMetric.Dps)
            .Select(r => r.Player.EntityId));
    }
}



