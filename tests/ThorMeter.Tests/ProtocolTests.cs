using System.Buffers.Binary;
using ThorMeter.Core;
using ThorMeter.Infrastructure;

namespace ThorMeter.Tests;
public class ProtocolTests
{
    // Synthetic wire fixtures based on the pinned EU layouts; not live compatibility evidence.
    [Fact]
    public void DirectDamagePreservesActorTargetAmountAndClass()
    {
        var decoder = new Aion2Decoder();
        byte[] frame = [0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0xd2, 0x09];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), 11010000);
        var events = decoder.Decode(frame, DateTimeOffset.UnixEpoch);
        var hit = Assert.Single(events.OfType<DamageEvent>());
        Assert.Equal(1ul, hit.SourceId);
        Assert.Equal(90ul, hit.TargetId);
        Assert.Equal(1234, hit.Damage);
        Assert.Equal(PlayerClass.Gladiator, Assert.Single(events.OfType<EntityMetadataEvent>()).Class);
        // A known heal-family variant on an unidentified target must not be counted as damage.
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), 17100000);
        Assert.Empty(decoder.Decode(frame, DateTimeOffset.UnixEpoch).OfType<DamageEvent>());
        var heal = Assert.Single(decoder.Decode(frame, DateTimeOffset.UnixEpoch).OfType<HealEvent>());
        Assert.Equal(1234, heal.Healing);
        Assert.Equal(1ul, heal.SourceId);
    }

    [Fact]
    public void DotTickUsesItsOwnLayout()
    {
        var decoder = new Aion2Decoder();
        byte[] frame = [0x05, 0x38, 90, 0x0a, 1, 0, 0, 0, 0, 0, 0xac, 0x02, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), 1_000_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), 11010000);
        var hit = Assert.Single(decoder.Decode(frame, DateTimeOffset.UnixEpoch).OfType<DamageEvent>());
        Assert.Equal(300, hit.Damage);
        Assert.Equal(90ul, hit.TargetId);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), 17100000);
        var heal = Assert.Single(decoder.Decode(frame, DateTimeOffset.UnixEpoch).OfType<HealEvent>());
        Assert.Equal(300, heal.Healing);
    }

    [Fact]
    public void ClassInferenceDoesNotConfirmPlayerKindAndHealDiagnosticsAggregateByOpcode()
    {
        var decoder = new Aion2Decoder();
        byte[] spawn = [0x41, 0x36, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(EntityKind.Npc, Assert.Single(decoder.Decode(spawn, DateTimeOffset.UnixEpoch)
            .OfType<EntityMetadataEvent>()).Kind);

        byte[] direct = [0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 100, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(direct.AsSpan(6), 17100000);
        var directEvents = decoder.Decode(direct, DateTimeOffset.UnixEpoch);
        Assert.Equal(EntityKind.Unknown, Assert.Single(directEvents.OfType<EntityMetadataEvent>()).Kind);
        Assert.Equal(100, Assert.Single(directEvents.OfType<HealEvent>()).Healing);

        byte[] dot = [0x05, 0x38, 90, 0x0a, 1, 0, 0, 0, 0, 0, 25, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(dot.AsSpan(11), 17100000);
        var dotEvents = decoder.Decode(dot, DateTimeOffset.UnixEpoch);
        Assert.Equal(EntityKind.Unknown, Assert.Single(dotEvents.OfType<EntityMetadataEvent>()).Kind);
        Assert.Equal(25, Assert.Single(dotEvents.OfType<HealEvent>()).Healing);

        var diagnostics = decoder.HealDiagnostics;
        Assert.Equal(2, diagnostics.Count);
        var directDiagnostic = Assert.Single(diagnostics, x => x.Opcode == Aion2Protocol.DamageOpcode);
        Assert.Equal(1, directDiagnostic.EventCount);
        Assert.Equal(100, directDiagnostic.TotalHealing);
        Assert.Equal(100, directDiagnostic.MaxHealing);
        var dotDiagnostic = Assert.Single(diagnostics, x => x.Opcode == Aion2Protocol.DotOpcode);
        Assert.Equal(25, dotDiagnostic.TotalHealing);
        Assert.Equal(25, dotDiagnostic.MaxHealing);
    }

    [Fact]
    public void HealDiagnosticsBoundDistinctGroupsAndKeepExistingGroups()
    {
        var decoder = new Aion2Decoder();
        for (int id = 1; id <= 8192; id++)
        {
            byte[] frame = [0x04, 0x38, 0, 0, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];
            frame[2] = (byte)((id & 0x7f) | 0x80);
            frame[3] = (byte)(id >> 7);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), 17100000);
            decoder.Decode(frame, DateTimeOffset.UnixEpoch);
        }
        Assert.Equal(8192, decoder.HealDiagnostics.Count);
        byte[] repeated = [0x04, 0x38, 1, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 2, 0];
        BinaryPrimitives.WriteUInt32LittleEndian(repeated.AsSpan(6), 17100000);
        decoder.Decode(repeated, DateTimeOffset.UnixEpoch);
        Assert.Equal(8192, decoder.HealDiagnostics.Count);
        var first = Assert.Single(decoder.HealDiagnostics, x => x.TargetId == 1);
        Assert.Equal(2, first.EventCount);
        Assert.Equal(3, first.TotalHealing);
        Assert.Equal(2, first.MaxHealing);
    }
    [Fact]
    public void UnknownAndTruncatedFramesDoNotEmitDamage()
    {
        var decoder = new Aion2Decoder();
        Assert.Empty(decoder.Decode(new byte[] { 0x7f, 0xfe, 0, 0 }, DateTimeOffset.UnixEpoch));
        Assert.Empty(decoder.Decode(new byte[] { 0x04, 0x38 }, DateTimeOffset.UnixEpoch));
        Assert.Equal(1, decoder.UnknownOpcodes);
        Assert.Equal(1, decoder.MalformedFrames);
    }

    [Fact]
    public void MalformedCompressedBundleLengthCannotEscapeDecoder()
    {
        var decoder = new Aion2Decoder();
        // A valid LZ4 literal block containing an overflowing nested record length.
        byte[] bundle = [0xff, 0xff, 5, 0, 0, 0, 0x50, 0xff, 0xff, 0xff, 0xff, 0x7f];
        Assert.Empty(decoder.Decode(bundle, DateTimeOffset.UnixEpoch));
        Assert.True(decoder.MalformedFrames > 0);
    }

    [Fact]
    public void OversizedCompressedOutputIsRejected()
    {
        var decoder = new Aion2Decoder();
        byte[] bundle = [0xff, 0xff, 0xff, 0xff, 0xff, 0x7f, 0];
        Assert.Empty(decoder.Decode(bundle, DateTimeOffset.UnixEpoch));
        Assert.Equal(1, decoder.MalformedFrames);
    }

    [Fact]
    public void NicknameRequiresStrictUtf8AndConservativeNameCharacters()
    {
        var decoder = new Aion2Decoder();
        byte[] valid = [0x45, 0x36, 1, 0, 0, 0, 5, (byte)'A', (byte)'l', (byte)'e', (byte)'x', (byte)'7', 0, 0, 0, 0];
        var metadata = Assert.Single(decoder.Decode(valid, DateTimeOffset.UnixEpoch).OfType<EntityMetadataEvent>());
        Assert.Equal("Alex7", metadata.Name);
        Assert.Equal(1, decoder.NameCandidatesAccepted);

        byte[] punctuation = [0x45, 0x36, 1, 0, 0, 0, 5, (byte)'A', (byte)'l', (byte)'e', (byte)'x', (byte)'!', 0, 0, 0, 0];
        Assert.Empty(decoder.Decode(punctuation, DateTimeOffset.UnixEpoch).OfType<EntityMetadataEvent>());
        byte[] invalidUtf8 = [0x45, 0x36, 1, 0, 0, 0, 3, 0xc3, 0x28, (byte)'x', 0, 0, 0, 0];
        Assert.Empty(decoder.Decode(invalidUtf8, DateTimeOffset.UnixEpoch).OfType<EntityMetadataEvent>());
        Assert.Equal(3, decoder.NameMessages);
        Assert.Equal(2, decoder.NameMessagesWithoutName);
    }

    [Fact]
    public void NpcSpawnPreservesValidatedNameForObservedTargets()
    {
        var decoder = new Aion2Decoder();
        byte[] frame = [0x41, 0x36, 90, 0, 0, 1, 4, (byte)'B', (byte)'o', (byte)'s', (byte)'s', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var metadata = Assert.Single(decoder.Decode(frame, DateTimeOffset.UnixEpoch).OfType<EntityMetadataEvent>());
        Assert.Equal(90ul, metadata.EntityId);
        Assert.Equal("Boss", metadata.Name);
        Assert.Equal(EntityKind.Npc, metadata.Kind);
    }

    [Fact]
    public void TruncatedAndRandomKnownMessagesDoNotThrow()
    {
        var random = new Random(412);
        var decoder = new Aion2Decoder();
        int[] opcodes = [Aion2Protocol.DamageOpcode, Aion2Protocol.DotOpcode, Aion2Protocol.NicknameOpcode,
            Aion2Protocol.AppearanceOpcode, Aion2Protocol.CharacterOpcode, Aion2Protocol.NpcSpawnOpcode, Aion2Protocol.BundleOpcode];
        foreach (int opcode in opcodes)
            for (int size = 2; size < 96; size++)
            {
                var bytes = new byte[size]; random.NextBytes(bytes);
                BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)opcode);
                var events = decoder.Decode(bytes, DateTimeOffset.UnixEpoch);
                Assert.All(events.OfType<DamageEvent>(), e => Assert.InRange(e.Damage, 1L, 100_000_000L));
            }
    }
}

