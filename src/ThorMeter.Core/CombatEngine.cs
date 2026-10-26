namespace ThorMeter.Core;

public enum PlayerClass { Unknown, Templar, Gladiator, Assassin, Ranger, Sorcerer, Spiritmaster, Cleric, Chanter }
public enum EntityKind { Unknown, Player, Npc, Pet }
public abstract record CombatEvent(DateTimeOffset Timestamp);
public sealed record DamageEvent(DateTimeOffset Timestamp, ulong SourceId, ulong TargetId, long Damage, uint? SkillId = null) : CombatEvent(Timestamp);
public sealed record HealEvent(DateTimeOffset Timestamp, ulong SourceId, ulong TargetId, long Healing, uint? SkillId = null) : CombatEvent(Timestamp);
public sealed record EntityMetadataEvent(DateTimeOffset Timestamp, ulong EntityId, string? Name, PlayerClass Class = PlayerClass.Unknown, EntityKind Kind = EntityKind.Unknown, bool IsLocalPlayer = false) : CombatEvent(Timestamp);
public sealed record OwnershipEvent(DateTimeOffset Timestamp, ulong EntityId, ulong OwnerId) : CombatEvent(Timestamp);
public sealed record ConnectionEvent(DateTimeOffset Timestamp, string ConnectionId, bool Connected) : CombatEvent(Timestamp);
public sealed record PauseEvent(DateTimeOffset Timestamp, bool Paused) : CombatEvent(Timestamp);
public sealed record ResetEvent(DateTimeOffset Timestamp) : CombatEvent(Timestamp);
public sealed record PlayerStatistics(ulong EntityId, string Name, PlayerClass Class, EntityKind Kind, bool IsLocalPlayer, long Damage, double Dps, long Healing = 0, double Hps = 0);
public sealed record TargetStatistics(ulong EntityId, string? Name, EntityKind Kind, long Damage);

/// <summary>Single consumer: callers serialize events and snapshots.</summary>
public sealed class CombatEngine
{
    private readonly Dictionary<ulong, EntityMetadataEvent> metadata = new();
    private readonly Dictionary<ulong, ulong> owners = new();
    private readonly HashSet<ulong> observedTargets = new();
    private readonly MetricAccumulator damage = new();
    private readonly MetricAccumulator healing = new();
    private string? connection;
    private bool paused;
    public const double IdleSeconds = 2;
    public bool IsPaused => paused;

    public void Apply(CombatEvent combatEvent)
    {
        switch (combatEvent)
        {
            case ConnectionEvent c:
                if (c.Connected && connection != c.ConnectionId)
                { Reset(); metadata.Clear(); owners.Clear(); observedTargets.Clear(); paused = false; }
                connection = c.Connected ? c.ConnectionId : null;
                break;
            case PauseEvent p:
                if (p.Paused && !paused) { damage.Suspend(); healing.Suspend(); }
                paused = p.Paused;
                break;
            case ResetEvent:
                Reset();
                break;
            case EntityMetadataEvent m:
                if (string.IsNullOrWhiteSpace(m.Name)) m = m with { Name = null };
                if (metadata.TryGetValue(m.EntityId, out var old))
                    m = m with { Name = string.IsNullOrWhiteSpace(m.Name) ? old.Name : m.Name,
                        Class = m.Class == PlayerClass.Unknown ? old.Class : m.Class,
                        Kind = m.Kind == EntityKind.Unknown ? old.Kind : m.Kind,
                        IsLocalPlayer = m.IsLocalPlayer || old.IsLocalPlayer };
                metadata[m.EntityId] = m;
                break;
            case OwnershipEvent o when o.EntityId != o.OwnerId:
                var ancestor = o.OwnerId;
                var seen = new HashSet<ulong> { o.EntityId };
                while (seen.Add(ancestor))
                {
                    if (!owners.TryGetValue(ancestor, out var next)) { owners[o.EntityId] = o.OwnerId; break; }
                    ancestor = next;
                }
                break;
            case DamageEvent d when d.Damage > 0 && !paused:
                var actor = Resolve(d.SourceId);
                if (metadata.TryGetValue(actor, out var info) && info.Kind == EntityKind.Npc) return;
                observedTargets.Add(d.TargetId);
                damage.Add(d.SourceId, d.TargetId, d.Damage, d.Timestamp);
                break;
            case HealEvent h when h.Healing > 0 && !paused:
                var healer = Resolve(h.SourceId);
                if (metadata.TryGetValue(healer, out var healerInfo) && healerInfo.Kind == EntityKind.Npc) return;
                // Healing is always shown across all recipients, so its activity follows the healer.
                healing.Add(h.SourceId, 0, h.Healing, h.Timestamp);
                break;
        }
    }

    public double Duration(DateTimeOffset now) => damage.Duration(now);
    public double Duration(DateTimeOffset now, ulong targetId) => MetricAccumulator.UnionDuration(
        damage.Entries(now).Where(e => e.TargetId == targetId).SelectMany(e => e.Intervals));
    public double HealingDuration(DateTimeOffset now) => healing.Duration(now);

    public IReadOnlyList<PlayerStatistics> Snapshot(DateTimeOffset now, ulong? targetId = null,
        IEnumerable<ulong>? pinnedIds = null)
    {
        var damageTotals = ResolveTotals(damage, now, targetId);
        var healingTotals = ResolveTotals(healing, now, null);
        var ids = damageTotals.Keys.Union(healingTotals.Keys).Union(pinnedIds ?? [])
            .Where(id => !metadata.TryGetValue(id, out var known) || known.Kind != EntityKind.Npc);
        return ids.Select(id =>
        {
            metadata.TryGetValue(id, out var m);
            var label = m?.Name ?? CoreText.Format("UnknownEntity", id);
            if (m?.Kind == EntityKind.Pet && !owners.ContainsKey(id)) label += CoreText.Get("UnresolvedPet");
            damageTotals.TryGetValue(id, out var dealt);
            healingTotals.TryGetValue(id, out var healed);
            var damageSeconds = dealt is null ? 0 : MetricAccumulator.UnionDuration(dealt.Intervals);
            var healingSeconds = healed is null ? 0 : MetricAccumulator.UnionDuration(healed.Intervals);
            var damageAmount = dealt?.Amount ?? 0;
            var healingAmount = healed?.Amount ?? 0;
            return new PlayerStatistics(id, label, m?.Class ?? PlayerClass.Unknown,
                m?.Kind ?? EntityKind.Unknown, m?.IsLocalPlayer ?? false, damageAmount,
                damageSeconds > 0 ? damageAmount / damageSeconds : 0, healingAmount,
                healingSeconds > 0 ? healingAmount / healingSeconds : 0);
        }).OrderByDescending(p => p.Damage).ThenBy(p => p.EntityId).ToArray();
    }

    public IReadOnlyList<TargetStatistics> Targets(DateTimeOffset now)
    {
        var totals = new Dictionary<ulong, long>();
        foreach (var entry in damage.Entries(now))
        {
            totals.TryGetValue(entry.TargetId, out var current);
            totals[entry.TargetId] = MetricAccumulator.SaturatingAdd(current, entry.Total);
        }
        return observedTargets.Select(id =>
        {
            metadata.TryGetValue(id, out var m);
            totals.TryGetValue(id, out var amount);
            return new TargetStatistics(id, m?.Name, m?.Kind ?? EntityKind.Unknown, amount);
        }).OrderByDescending(t => t.Damage).ThenBy(t => t.EntityId).ToArray();
    }

    private sealed class ResolvedMetric
    {
        public long Amount;
        public readonly List<ActivityInterval> Intervals = new();
    }

    private Dictionary<ulong, ResolvedMetric> ResolveTotals(MetricAccumulator metric, DateTimeOffset now, ulong? targetId)
    {
        var totals = new Dictionary<ulong, ResolvedMetric>();
        foreach (var entry in targetId.HasValue ? metric.Entries(now).Where(e => e.TargetId == targetId.Value) : metric.AllEntries(now))
        {
            var id = Resolve(entry.SourceId);
            if (metadata.TryGetValue(id, out var m) && m.Kind == EntityKind.Npc) continue;
            if (!totals.TryGetValue(id, out var total)) totals[id] = total = new ResolvedMetric();
            total.Amount = MetricAccumulator.SaturatingAdd(total.Amount, entry.Total);
            total.Intervals.AddRange(entry.Intervals);
        }
        return totals;
    }
    public void Reset()
    {
        damage.Reset();
        healing.Reset();
        observedTargets.Clear();
    }
    private ulong Resolve(ulong id)
    {
        var seen = new HashSet<ulong>();
        while (seen.Add(id) && owners.TryGetValue(id, out var owner)) id = owner;
        return id;
    }
}



