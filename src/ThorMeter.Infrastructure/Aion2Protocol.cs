using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ThorMeter.Core;

namespace ThorMeter.Infrastructure;

/// <summary>EU early access 2026-09-30, pinned from the MIT SkeeveTV reference.</summary>
public static class Aion2Protocol
{
    public const int ServerPort = 13328;
    public const int DamageOpcode = 0x0438;
    public const int DotOpcode = 0x0538;
    public const int NicknameOpcode = 17718;
    public const int AppearanceOpcode = 1165;
    public const int CharacterOpcode = 13110;
    public const int NpcSpawnOpcode = 16694;
    public const int BundleOpcode = 0xffff;
    public static readonly HashSet<int> KnownOpcodes = LoadOpcodes();
    private static HashSet<int> LoadOpcodes()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "eu-opcodes.json")));
            return doc.RootElement.GetProperty("syncOpcodes").EnumerateArray().Select(x => x.GetInt32()).ToHashSet();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { return [DamageOpcode, DotOpcode, NicknameOpcode, AppearanceOpcode, CharacterOpcode, NpcSpawnOpcode, BundleOpcode]; }
    }
}

/// <summary>In-memory summary of decoded heal events grouped by protocol identity.</summary>
public sealed record HealDiagnosticSnapshot(
    ulong SourceId, ulong TargetId, uint SkillId, int Opcode,
    long EventCount, long TotalHealing, long MaxHealing);

/// <summary>Converts only pinned, verified message fields. Unknown messages are counted and skipped.</summary>
public sealed class Aion2Decoder
{
    private readonly HashSet<int> healFamilies = LoadHealFamilies();
    private readonly HashSet<int> effectFamilies = LoadEffectFamilies();
    private readonly HashSet<ulong> players = new();
    private readonly HashSet<ulong> monsters = new();
    private const int MaxHealDiagnosticGroups = 8192;
    private readonly Dictionary<HealDiagnosticKey, HealDiagnosticAccumulator> healDiagnostics = new();
    private readonly object healDiagnosticsLock = new();
    public long Frames { get; private set; }
    public long UnknownOpcodes { get; private set; }
    public long MalformedFrames { get; private set; }
    public long Bundles { get; private set; }
    public long DamageEvents { get; private set; }
    public long HealingEvents { get; private set; }
    /// <summary>Bounded decoder-lifetime summaries; contains decoded amounts only and is never persisted.</summary>
    public IReadOnlyList<HealDiagnosticSnapshot> HealDiagnostics
    {
        get
        {
            lock (healDiagnosticsLock)
                return healDiagnostics.Select(pair => new HealDiagnosticSnapshot(pair.Key.SourceId, pair.Key.TargetId,
                    pair.Key.SkillId, pair.Key.Opcode, pair.Value.EventCount, pair.Value.TotalHealing, pair.Value.MaxHealing))
                    .OrderByDescending(x => x.EventCount).ThenBy(x => x.SourceId).ThenBy(x => x.TargetId)
                    .ThenBy(x => x.SkillId).ThenBy(x => x.Opcode).ToArray();
        }
    }
    public long FilteredEffects { get; private set; }
    /// <summary>Nickname/local-character frames examined by the conservative identity decoder.</summary>
    public long NameMessages { get; private set; }
    /// <summary>Identity names accepted after strict UTF-8 and character validation.</summary>
    public long NameCandidatesAccepted { get; private set; }
    /// <summary>Nickname/local-character frames that contained no validated name.</summary>
    public long NameMessagesWithoutName { get; private set; }

    public IReadOnlyList<CombatEvent> Decode(ReadOnlySpan<byte> frame, DateTimeOffset timestamp)
    {
        var result = new List<CombatEvent>();
        DecodeInto(frame, timestamp, result, false);
        return result;
    }

    private void DecodeInto(ReadOnlySpan<byte> frame, DateTimeOffset at, List<CombatEvent> output, bool nested)
    {
        Frames++;
        if (frame.Length < 2) { MalformedFrames++; return; }
        int opcode = BinaryPrimitives.ReadUInt16BigEndian(frame);
        if (opcode == Aion2Protocol.BundleOpcode && !nested) { DecodeBundle(frame, at, output); return; }
        switch (opcode)
        {
            case Aion2Protocol.DamageOpcode: DecodeDamage(frame, at, output); break;
            case Aion2Protocol.DotOpcode: DecodeDot(frame, at, output); break;
            case Aion2Protocol.NicknameOpcode: DecodeNickname(frame, at, output, false); break;
            case Aion2Protocol.AppearanceOpcode: DecodeAppearance(frame, at, output); break;
            case Aion2Protocol.CharacterOpcode: DecodeNickname(frame, at, output, true); break;
            case Aion2Protocol.NpcSpawnOpcode: DecodeSpawn(frame, at, output); break;
            default: UnknownOpcodes++; break;
        }
    }

    private void DecodeDamage(ReadOnlySpan<byte> f, DateTimeOffset at, List<CombatEvent> result)
    {
        int p = 2;
        if (!ReadVarint(f, ref p, out ulong target) || p + 2 > f.Length) { MalformedFrames++; return; }
        int flags = f[p]; p += 2;
        if ((flags & 4) == 0) { FilteredEffects++; return; }
        if (!ReadVarint(f, ref p, out ulong actor) || p + 6 > f.Length) { MalformedFrames++; return; }
        uint skill = BinaryPrimitives.ReadUInt32LittleEndian(f[p..]); p += 4;
        int family = (int)(skill / 10000 * 10000);
        if (skill is >= 2_000_000 and < 3_000_000 || effectFamilies.Contains(family)) { FilteredEffects++; return; }
        int marker = -1;
        for (int i = p + 2; i + 4 <= f.Length; i++)
            if (f[i] is >= 1 and <= 9 && f[i + 1] == 0 && f[i + 2] == 0 && f[i + 3] == 0) { marker = i; break; }
        int q = marker + 6;
        if (marker < 0 || q >= f.Length || !ReadVarint(f, ref q, out ulong amount) || amount is 0 or > 100_000_000)
        { MalformedFrames++; return; }
        if (skill == 11000100) { FilteredEffects++; return; }
        if (healFamilies.Contains(family) && !monsters.Contains(target))
        {
            if (ClassOf(skill) is PlayerClass healerClass)
            {
                players.Add(actor);
                result.Add(new EntityMetadataEvent(at, actor, null, healerClass, EntityKind.Unknown));
            }
            result.Add(new HealEvent(at, actor, target, (long)amount, skill));
            HealingEvents++;
            RecordHealDiagnostic(actor, target, skill, Aion2Protocol.DamageOpcode, (long)amount);
            return;
        }
        if (ClassOf(skill) is PlayerClass cls)
        {
            players.Add(actor);
            result.Add(new EntityMetadataEvent(at, actor, null, cls, EntityKind.Unknown));
        }
        if (actor != target)
        { result.Add(new DamageEvent(at, actor, target, (long)amount, skill)); DamageEvents++; }
    }

    private void DecodeDot(ReadOnlySpan<byte> f, DateTimeOffset at, List<CombatEvent> result)
    {
        int p = 2;
        if (!ReadVarint(f, ref p, out ulong target) || p >= f.Length) { MalformedFrames++; return; }
        int flags = f[p++];
        if ((flags & 0x0a) != 0x0a) { FilteredEffects++; return; }
        if (!ReadVarint(f, ref p, out ulong actor) || !ReadVarint(f, ref p, out _) || p + 4 > f.Length)
        { MalformedFrames++; return; }
        uint effect = BinaryPrimitives.ReadUInt32LittleEndian(f[p..]); p += 4;
        if (!ReadVarint(f, ref p, out ulong amount)) { MalformedFrames++; return; }
        if ((flags & 1) != 0 && !ReadVarint(f, ref p, out _)) { MalformedFrames++; return; }
        if (p + 4 != f.Length) { MalformedFrames++; return; }
        uint skill = BinaryPrimitives.ReadUInt32LittleEndian(f[p..]);
        if (amount is 0 or > 100_000_000 || (flags & 1) != 0)
        { FilteredEffects++; return; }
        if (healFamilies.Contains((int)(skill / 10000 * 10000)) && !monsters.Contains(target))
        {
            if (ClassOf(skill) is PlayerClass healerClass)
            { players.Add(actor); result.Add(new EntityMetadataEvent(at, actor, null, healerClass, EntityKind.Unknown)); }
            result.Add(new HealEvent(at, actor, target, (long)amount, skill)); HealingEvents++;
            RecordHealDiagnostic(actor, target, skill, Aion2Protocol.DotOpcode, (long)amount);
            return;
        }
        if (actor == target || effect < 1_000_000_000 && ClassOf(skill) is not null
            || players.Contains(actor) && players.Contains(target)) { FilteredEffects++; return; }
        if (ClassOf(skill) is PlayerClass cls)
        { players.Add(actor); result.Add(new EntityMetadataEvent(at, actor, null, cls, EntityKind.Unknown)); }
        result.Add(new DamageEvent(at, actor, target, (long)amount, skill)); DamageEvents++;
    }

    private void RecordHealDiagnostic(ulong sourceId, ulong targetId, uint skillId, int opcode, long amount)
    {
        lock (healDiagnosticsLock)
        {
            var key = new HealDiagnosticKey(sourceId, targetId, skillId, opcode);
            if (!healDiagnostics.TryGetValue(key, out var entry))
            {
                if (healDiagnostics.Count >= MaxHealDiagnosticGroups) return;
                healDiagnostics.Add(key, entry = new HealDiagnosticAccumulator());
            }
            entry.EventCount = entry.EventCount == long.MaxValue ? long.MaxValue : entry.EventCount + 1;
            entry.TotalHealing = amount > 0 && entry.TotalHealing > long.MaxValue - amount
                ? long.MaxValue : entry.TotalHealing + amount;
            entry.MaxHealing = Math.Max(entry.MaxHealing, amount);
        }
    }

    private readonly record struct HealDiagnosticKey(ulong SourceId, ulong TargetId, uint SkillId, int Opcode);
    private sealed class HealDiagnosticAccumulator
    {
        public long EventCount;
        public long TotalHealing;
        public long MaxHealing;
    }

    private void DecodeNickname(ReadOnlySpan<byte> f, DateTimeOffset at, List<CombatEvent> result, bool local)
    {
        NameMessages++;
        int p = 2;
        if (!ReadVarint(f, ref p, out ulong id)) { MalformedFrames++; NameMessagesWithoutName++; return; }
        for (int k = p; k < Math.Min(f.Length - (local ? 12 : 4), p + (local ? 16 : 24)); k++)
        {
            if (!ReadName(f, k, out var name, local ? 2 : 3)) continue;
            PlayerClass cls = PlayerClass.Unknown;
            if (local)
            {
                int after = k + 1 + f[k];
                if (after + 11 > f.Length || f[after + 6] != 1) continue;
                int code = BinaryPrimitives.ReadInt32LittleEndian(f[(after + 2)..]);
                int level = BinaryPrimitives.ReadInt32LittleEndian(f[(after + 7)..]);
                if (code % 4 is not (1 or 2) || code / 4 is < 1 or > 8 || level is < 1 or > 200) continue;
                cls = ClassFromCode(code);
            }
            players.Add(id);
            result.Add(new EntityMetadataEvent(at, id, name, cls, EntityKind.Player, local));
            NameCandidatesAccepted++;
            return;
        }
        NameMessagesWithoutName++;
    }

    private void DecodeAppearance(ReadOnlySpan<byte> f, DateTimeOffset at, List<CombatEvent> result)
    {
        NameMessages++;
        int p = 2;
        if (!ReadVarint(f, ref p, out _) || p + 4 > f.Length) { MalformedFrames++; NameMessagesWithoutName++; return; }
        p += 4;
        if (!ReadVarint(f, ref p, out ulong id) || id == 0 || p + 3 > f.Length
            || !ReadName(f, p + 2, out string name, 2)) { MalformedFrames++; NameMessagesWithoutName++; return; }
        players.Add(id);
        result.Add(new EntityMetadataEvent(at, id, name, Kind: EntityKind.Player));
        NameCandidatesAccepted++;
    }

    private void DecodeSpawn(ReadOnlySpan<byte> f, DateTimeOffset at, List<CombatEvent> result)
    {
        int p = 2;
        if (!ReadVarint(f, ref p, out ulong id) || p + 7 > f.Length) { MalformedFrames++; return; }
        string? name = null;
        if (f[p + 2] == 1 && ReadName(f, p + 3, out var spawnName, 2))
        { name = spawnName; p += 1 + f[p + 3]; }
        if (p + 7 > f.Length) { MalformedFrames++; return; }
        p += 3;
        // Entity is known to be non-player, but a verified owner can reclassify it as a pet.
        int marker = f[(p + 4)..].IndexOf(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 });
        int q = marker < 0 ? -1 : p + 4 + marker + 16;
        ulong owner = 0;
        bool hasOwner = q > 0 && q < f.Length && ReadVarint(f, ref q, out owner) && owner != 0 && owner != id;
        if (hasOwner)
        {
            result.Add(new EntityMetadataEvent(at, id, name, Kind: EntityKind.Pet));
            result.Add(new OwnershipEvent(at, id, owner));
        }
        else { monsters.Add(id); result.Add(new EntityMetadataEvent(at, id, name, Kind: EntityKind.Npc)); }
    }

    private void DecodeBundle(ReadOnlySpan<byte> f, DateTimeOffset at, List<CombatEvent> result)
    {
        if (f.Length < 7) { MalformedFrames++; return; }
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(f[2..]);
        if (size > 1 << 20 || !Aion2Lz4.TryDecompress(f[6..], (int)size, out byte[] raw)) { MalformedFrames++; return; }
        Bundles++;
        int p = 0;
        while (p < raw.Length)
        {
            if (!ReadVarint(raw, ref p, out ulong length) || length < 4) { MalformedFrames++; return; }
            if (length < 4 || length - 4 > (ulong)(raw.Length - p)) { MalformedFrames++; return; }
            int end = p + (int)length - 4;
            if (end > raw.Length || end - p < 2) { MalformedFrames++; return; }
            DecodeInto(raw.AsSpan(p, end - p), at, result, true); p = end;
        }
    }

    private static bool ReadVarint(ReadOnlySpan<byte> bytes, ref int p, out ulong value)
    {
        value = 0;
        for (int n = 0; n < 5 && p < bytes.Length; n++)
        {
            byte b = bytes[p++]; value |= (ulong)(b & 127) << (n * 7);
            if ((b & 128) == 0) return true;
        }
        return false;
    }
    private static bool ReadName(ReadOnlySpan<byte> f, int p, out string name, int min)
    {
        name = "";
        if (p >= f.Length || f[p] < min || f[p] > 24 || p + 1 + f[p] > f.Length) return false;
        try { name = new UTF8Encoding(false, true).GetString(f.Slice(p + 1, f[p])).Normalize(NormalizationForm.FormC); }
        catch (DecoderFallbackException) { return false; }
        // Keep acceptance deliberately narrow: Unicode letters, combining marks and digits only.
        // This prevents arbitrary payload bytes from becoming identity labels.
        return name.Length is > 0 and <= 24 && name.All(c => char.IsLetterOrDigit(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);
    }
    private static PlayerClass? ClassOf(uint skill)
    {
        int code = (int)(skill / 1_000_000);
        return skill >= 10_000_000 ? code switch
        {
            11 => PlayerClass.Gladiator, 12 => PlayerClass.Templar, 13 => PlayerClass.Assassin,
            14 => PlayerClass.Ranger, 15 => PlayerClass.Sorcerer, 16 => PlayerClass.Spiritmaster,
            17 => PlayerClass.Cleric, 18 => PlayerClass.Chanter, _ => null
        } : null;
    }
    private static PlayerClass ClassFromCode(int code) => (code / 4) switch
    {
        1 => PlayerClass.Gladiator, 2 => PlayerClass.Templar, 3 => PlayerClass.Ranger,
        4 => PlayerClass.Assassin, 5 => PlayerClass.Spiritmaster, 6 => PlayerClass.Sorcerer,
        7 => PlayerClass.Cleric, 8 => PlayerClass.Chanter, _ => PlayerClass.Unknown
    };
    private static HashSet<int> LoadHealFamilies()
    {
        try { using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "heal_skill_families.json")));
            return d.RootElement.GetProperty("heal").EnumerateArray().Select(x => x.GetInt32()).ToHashSet(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { throw new InvalidOperationException(CaptureText.Get("HealTableMissing"), ex); }
    }
    private static HashSet<int> LoadEffectFamilies()
    {
        try { using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rdps_watchlist.json")));
            return d.RootElement.GetProperty("buffs").EnumerateArray()
                .Where(x => !x.TryGetProperty("kind", out var kind) || kind.GetString() != "target_debuff")
                .Select(x => x.GetProperty("skillId").GetInt32() / 10000 * 10000).ToHashSet(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { throw new InvalidOperationException(CaptureText.Get("EffectTableMissing"), ex); }
    }
}
