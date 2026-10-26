using System.Buffers.Binary;
using System.Text.Json;
using ThorMeter.Core;
using ThorMeter.Infrastructure;

namespace ThorMeter.Tests;
public class ReplayTests
{
    [Fact]
    public async Task ReplayIgnoresClientTrafficAndRetransmissions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            byte[] payload = [24, 0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0xd2, 0x09];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), 11010000);
            string serverLine = Line("127.0.0.1:13328", "127.0.0.1:40000", payload);
            string clientLine = Line("127.0.0.1:40000", "127.0.0.1:13328", payload);
            await File.WriteAllLinesAsync(path, [clientLine, serverLine, serverLine]);
            using var service = new MeterCaptureService();
            var events = new List<CombatEvent>();
            service.CombatEventReceived += events.Add;
            await service.ReplayAsync(path);
            Assert.Equal(1234, Assert.Single(events.OfType<DamageEvent>()).Damage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InvalidAndOversizedRecordsAreSkippedWithStickyWarning()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllLinesAsync(path, ["{}", new string('x', 2_000_001)]);
            using var service = new MeterCaptureService();
            var events = new List<CombatEvent>();
            service.CombatEventReceived += events.Add;
            await service.ReplayAsync(path);
            Assert.True(service.DataMayBeIncomplete);
            Assert.Empty(events.OfType<DamageEvent>());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EmptySynEstablishesBaselineForOutOfOrderFirstPayload()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            byte[] payload = [24, 0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0xd2, 0x09];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), 11010000);
            await File.WriteAllLinesAsync(path, [
                Line("127.0.0.1:13328", "127.0.0.1:40000", [], 99, syn: true),
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload[10..], 110),
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload[..10], 100)]);
            using var service = new MeterCaptureService();
            var events = new List<CombatEvent>();
            service.CombatEventReceived += events.Add;
            await service.ReplayAsync(path);
            Assert.Equal(1234, Assert.Single(events.OfType<DamageEvent>()).Damage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RecordedSelectionChangesResetSessionAndDecodeNewFlow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            byte[] payload = [24, 0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0xd2, 0x09];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), 11010000);
            await File.WriteAllLinesAsync(path, [
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload),
                Line("127.0.0.2:13328", "127.0.0.1:40001", [], selectionStart: true),
                Line("127.0.0.2:13328", "127.0.0.1:40001", payload)]);
            using var service = new MeterCaptureService();
            var engine = new CombatEngine();
            int hits = 0;
            service.CombatEventReceived += e => { engine.Apply(e); if (e is DamageEvent) hits++; };
            await service.ReplayAsync(path);
            Assert.Equal(2, hits);
            Assert.Equal(1234, Assert.Single(engine.Snapshot(DateTimeOffset.UnixEpoch)).Damage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReplayEmitsPauseMarkersInOrderAndAcceptsLegacyPacketLines()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            byte[] payload = [24, 0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0xd2, 0x09];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), 11010000);
            var before = DateTimeOffset.UnixEpoch;
            var pausedAt = before.AddSeconds(1);
            var resumedAt = before.AddSeconds(4);
            var packet = JsonSerializer.Deserialize<JsonElement>(Line("127.0.0.1:13328", "127.0.0.1:40000", payload));
            string pause = JsonSerializer.Serialize(new { Timestamp = pausedAt, PauseState = true });
            string resume = JsonSerializer.Serialize(new { Timestamp = resumedAt, PauseState = false });
            await File.WriteAllLinesAsync(path, [packet.GetRawText(), pause, resume]);

            using var service = new MeterCaptureService();
            var events = new List<CombatEvent>();
            service.CombatEventReceived += events.Add;
            await service.ReplayAsync(path);

            Assert.Single(events.OfType<DamageEvent>());
            Assert.Equal([new PauseEvent(pausedAt, true), new PauseEvent(resumedAt, false)], events.OfType<PauseEvent>());
            Assert.True(events.FindIndex(e => e is DamageEvent) < events.FindIndex(e => e is PauseEvent));
            Assert.False(service.IsPaused);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReplayAcceptsPauseMarkerWithoutOptionalFieldsOnOldRecords()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllLinesAsync(path, [
                "{\"Timestamp\":\"1970-01-01T00:00:00+00:00\",\"PauseState\":true}",
                "{\"Timestamp\":\"1970-01-01T00:00:01+00:00\",\"PauseState\":false}"]);
            using var service = new MeterCaptureService();
            var pauses = new List<PauseEvent>();
            service.CombatEventReceived += e => { if (e is PauseEvent pause) pauses.Add(pause); };
            await service.ReplayAsync(path);
            Assert.Equal([true, false], pauses.Select(e => e.Paused));
            Assert.False(service.DataMayBeIncomplete);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReplayPauseMarkersReproduceDiscardedCombatTotals()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            byte[] payload = [24, 0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0xd2, 0x09];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), 11010000);
            await File.WriteAllLinesAsync(path, [
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload, 100, timestamp: DateTimeOffset.UnixEpoch),
                JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(1), PauseState = true }),
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload, 121, timestamp: DateTimeOffset.UnixEpoch.AddSeconds(2)),
                JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(3), PauseState = false }),
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload, 142, timestamp: DateTimeOffset.UnixEpoch.AddSeconds(4))]);
            using var service = new MeterCaptureService();
            var engine = new CombatEngine();
            service.CombatEventReceived += engine.Apply;
            await service.ReplayAsync(path);
            Assert.Equal(2468, Assert.Single(engine.Snapshot(DateTimeOffset.UnixEpoch.AddSeconds(6))).Damage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ReplayResetMarkerClearsEarlierTotalsAndPreservesMarkerOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"thor-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            byte[] payload = [24, 0x04, 0x38, 90, 4, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0xd2, 0x09];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(7), 11010000);
            var resetAt = DateTimeOffset.UnixEpoch.AddSeconds(2);
            await File.WriteAllLinesAsync(path, [
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload, 100, timestamp: DateTimeOffset.UnixEpoch),
                JsonSerializer.Serialize(new { Timestamp = resetAt, ResetState = true }),
                Line("127.0.0.1:13328", "127.0.0.1:40000", payload, 121, timestamp: DateTimeOffset.UnixEpoch.AddSeconds(3))]);
            using var service = new MeterCaptureService();
            var engine = new CombatEngine();
            var events = new List<CombatEvent>();
            service.CombatEventReceived += e => { events.Add(e); engine.Apply(e); };
            await service.ReplayAsync(path);

            Assert.Equal(2, events.OfType<DamageEvent>().Count());
            Assert.Equal(resetAt, Assert.Single(events.OfType<ResetEvent>()).Timestamp);
            Assert.Equal(1234, Assert.Single(engine.Snapshot(DateTimeOffset.UnixEpoch.AddSeconds(4))).Damage);
            Assert.True(events.FindIndex(e => e is DamageEvent) < events.FindIndex(e => e is ResetEvent));
            Assert.True(events.FindIndex(e => e is ResetEvent) < events.FindLastIndex(e => e is DamageEvent));
        }
        finally { File.Delete(path); }
    }

    private static string Line(string source, string destination, byte[] payload, uint sequence = 100, bool syn = false, bool selectionStart = false, DateTimeOffset? timestamp = null) => JsonSerializer.Serialize(new
    {
        Timestamp = timestamp ?? DateTimeOffset.UnixEpoch, Source = source, Destination = destination,
        Sequence = sequence, PayloadBase64 = Convert.ToBase64String(payload), Syn = syn, Fin = false, Rst = false, SelectionStart = selectionStart
    });
}

