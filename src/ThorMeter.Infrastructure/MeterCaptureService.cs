using System.Text.Json;
using System.Text;
using System.Runtime.CompilerServices;
using PacketDotNet;
using SharpPcap;

namespace ThorMeter.Infrastructure;

public sealed record CaptureAdapterInfo(string Id, string Name);
public sealed record ObservedFlow(string Id, string Client, string Server, long Packets, bool Selected, bool VerifiedProtocol);

/// <summary>Passive Npcap capture of the pinned EU server port; only a selected flow reaches the decoder.</summary>
public sealed class MeterCaptureService : IDisposable
{
    private sealed class FlowState
    {
        public required string Id, Client, Server;
        public long Packets;
        public bool Verified;
        public readonly Dictionary<string, TcpReassembler> Streams = new();
        public readonly Dictionary<string, TcpReassembler> ProbeStreams = new();
        public readonly Queue<CapturedTcpSegment> Recent = new();
        public int RecentBytes;
        public bool CacheTruncated;
        public Aion2Decoder Decoder = new();
    }
    private readonly object gate = new();
    private readonly Dictionary<string, FlowState> flows = new();
    private ILiveDevice? device;
    private StreamWriter? recorder;
    private string? selected;
    private long packetErrors;
    private long lastReportedLoss;
    private long connectionGeneration;
    private bool disposed;
    private bool paused;
    private volatile bool incomplete;

    public event Action<ThorMeter.Core.CombatEvent>? CombatEventReceived;
    public event Action<string>? Diagnostic;
    public event Action? FlowsChanged;
    public event Action<DateTimeOffset>? CaptureTimeChanged;
    public DateTimeOffset? LastPacketTimestamp { get; private set; }
    public long PacketErrors => Interlocked.Read(ref packetErrors);
    public bool DataMayBeIncomplete => incomplete;
    public bool IsPaused { get { lock (gate) return paused; } }
    public string HealthSummary => CaptureText.Get(incomplete ? "HealthIncomplete" : "HealthNoLoss");
    public string ProgressSummary
    {
        get
        {
            lock (gate)
            {
                if (selected is null || !flows.TryGetValue(selected, out var flow))
                    return CaptureText.Format("ProgressNoSelection", flows.Count);
                return CaptureText.Format("ProgressSelected", flow.Packets, flow.Decoder.Frames, flow.Decoder.DamageEvents,
                    flow.Decoder.HealingEvents, flow.Decoder.UnknownOpcodes, flow.Decoder.MalformedFrames,
                    flow.Decoder.NameCandidatesAccepted, flow.Decoder.NameMessagesWithoutName);
            }
        }
    }
    public string? SelectedFlowId { get { lock (gate) return selected; } }
    public IReadOnlyList<ObservedFlow> ObservedFlows
    {
        get { lock (gate) return flows.Values.OrderByDescending(f => f.Packets)
            .Select(f => new ObservedFlow(f.Id, f.Client, f.Server, f.Packets, f.Id == selected, f.Verified)).ToArray(); }
    }
    public IReadOnlyList<HealDiagnosticSnapshot> HealDiagnostics
    {
        get { lock (gate) return selected is not null && flows.TryGetValue(selected, out var flow)
            ? flow.Decoder.HealDiagnostics : []; }
    }

    public static IReadOnlyList<CaptureAdapterInfo> ListAdapters()
    {
        try { return CaptureDeviceList.Instance.Cast<ILiveDevice>()
            .Select(d => new CaptureAdapterInfo(d.Name, d.Description ?? d.Name)).ToArray(); }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or PcapException)
        { return []; }
    }

    public void Start(string adapterId, string? recordingPath = null)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (device is not null) throw new InvalidOperationException(CaptureText.Get("AlreadyActive"));
            flows.Clear(); selected = null; LastPacketTimestamp = null; incomplete = false; paused = false;
            lastReportedLoss = 0; Interlocked.Exchange(ref packetErrors, 0); FlowsChanged?.Invoke();
            var chosen = CaptureDeviceList.Instance.Cast<ILiveDevice>()
                .FirstOrDefault(x => x.Name == adapterId)
                ?? throw new InvalidOperationException(CaptureText.Get("AdapterNotFound"));
            try
            {
                if (!string.IsNullOrWhiteSpace(recordingPath))
                    recorder = new StreamWriter(new FileStream(recordingPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
                chosen.Open(new DeviceConfiguration { ReadTimeout = 250, Mode = DeviceModes.None });
                chosen.Filter = $"tcp and port {Aion2Protocol.ServerPort}";
                chosen.OnPacketArrival += OnPacketArrival;
                chosen.StartCapture();
                device = chosen;
                Report(CaptureText.Format("Capturing", chosen.Description ?? chosen.Name, Aion2Protocol.ServerPort));
            }
            catch
            {
                chosen.OnPacketArrival -= OnPacketArrival;
                recorder?.Dispose(); recorder = null;
                try { chosen.Close(); } catch (PcapException) { }
                throw;
            }
        }
    }

    /// <summary>Pauses combat aggregation while capture, decoding, identity updates, and raw recording continue.</summary>
    public void SetPaused(bool value, DateTimeOffset timestamp)
    {
        lock (gate)
        {
            if (paused == value) return;
            paused = value;
            Emit(new ThorMeter.Core.PauseEvent(timestamp, value));
            WriteRecord(RecordedSegment.Pause(timestamp, value));
        }
    }

    /// <summary>Resets current combat totals and records the same boundary for replay.</summary>
    public void ResetTotals(DateTimeOffset timestamp)
    {
        lock (gate)
        {
            Emit(new ThorMeter.Core.ResetEvent(timestamp));
            WriteRecord(RecordedSegment.Reset(timestamp));
        }
    }

    public bool SelectFlow(string flowId)
    {
        lock (gate)
        {
            if (!flows.TryGetValue(flowId, out FlowState? flow)) return false;
            if (selected == flowId) return true;
            selected = flowId;
            flow.Streams.Clear(); flow.Decoder = new Aion2Decoder();
            Emit(new ThorMeter.Core.ConnectionEvent(LastPacketTimestamp ?? DateTimeOffset.UtcNow,
                flowId + "#" + (++connectionGeneration), true));
            if (paused) Emit(new ThorMeter.Core.PauseEvent(LastPacketTimestamp ?? DateTimeOffset.UtcNow, true));
            WriteRecord(new RecordedSegment(LastPacketTimestamp ?? DateTimeOffset.UtcNow, flow.Server, flow.Client, 0, "", false, false, false, true));
            foreach (var prior in flow.Recent)
                DecodeSelected(flow, prior, prior.Source == flow.Server, true);
            flow.Recent.Clear(); flow.RecentBytes = 0; flow.ProbeStreams.Clear();
            if (flow.CacheTruncated) { incomplete = true; Report(CaptureText.Get("BufferTruncated")); }
            Report(CaptureText.Format("SelectedConnection", flow.Server, CaptureText.Get(flow.Verified ? "FramingMatched" : "AwaitingFraming")));
            FlowsChanged?.Invoke();
            return true;
        }
    }

    public void Stop()
    {
        ILiveDevice? current;
        StreamWriter? oldRecorder;
        lock (gate)
        {
            current = device; device = null; oldRecorder = recorder; recorder = null;
            if (current is not null && selected is not null && flows.TryGetValue(selected, out var stoppedFlow)
                && stoppedFlow.Streams.Values.Any(s => s.BufferedBytes > 0)) incomplete = true;
            if (current is not null) current.OnPacketArrival -= OnPacketArrival;
        }
        if (current is not null)
        {
            try { current.StopCapture(); current.Close(); }
            catch (PcapException ex) { Report(CaptureText.Format("StoppedAfterError", ex.Message)); }
        }
        oldRecorder?.Dispose();
        Report(CaptureText.Get("Stopped"));
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        try
        {
            var raw = capture.GetPacket();
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            var ip = packet.Extract<IPPacket>(); var tcp = packet.Extract<TcpPacket>();
            if (ip is null || tcp is null) return;
            string source = Endpoint(ip.SourceAddress.ToString(), tcp.SourcePort);
            string destination = Endpoint(ip.DestinationAddress.ToString(), tcp.DestinationPort);
            var segment = new CapturedTcpSegment(new DateTimeOffset(raw.Timeval.Date.ToUniversalTime()), source,
                destination, tcp.SequenceNumber, tcp.PayloadData ?? [], tcp.Synchronize, tcp.Finished, tcp.Reset);
            Process(segment, tcp.SourcePort == Aion2Protocol.ServerPort, false);
        }
        catch (Exception ex)
        {
            long count = Interlocked.Increment(ref packetErrors);
            incomplete = true;
            if (count == 1 || count % 100 == 0) Report(CaptureText.Format("PacketErrors", count, ex.GetType().Name));
        }
    }

    private static string Endpoint(string ip, int port) => ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";

    private void Process(CapturedTcpSegment segment, bool fromServer, bool replay)
    {
        lock (gate)
        {
            string server = fromServer ? segment.Source : segment.Destination;
            string client = fromServer ? segment.Destination : segment.Source;
            string id = client + "|" + server;
            if (!flows.TryGetValue(id, out FlowState? flow))
            {
                if (flows.Count >= 16) { incomplete = true; Report(CaptureText.Get("FlowLimit")); return; }
                flow = new FlowState { Id = id, Client = client, Server = server }; flows.Add(id, flow);
                FlowsChanged?.Invoke();
            }
            flow.Packets++;
            LastPacketTimestamp = segment.Timestamp;
            CaptureTimeChanged?.Invoke(segment.Timestamp);
            if (selected != id)
            {
                if (selected is not null) return;
                int cost = segment.Payload.Length + 128;
                if (cost <= 1 << 20)
                {
                    while (flow.RecentBytes + cost > 1 << 20 && flow.Recent.Count > 0)
                    { flow.RecentBytes -= flow.Recent.Dequeue().Payload.Length + 128; flow.CacheTruncated = true; }
                    flow.Recent.Enqueue(segment); flow.RecentBytes += cost;
                }
                else flow.CacheTruncated = true;
                if (fromServer)
                {
                    string direction = segment.Source + ">" + segment.Destination;
                    if (segment.Syn) { flow.ProbeStreams.Clear(); flow.ProbeStreams[direction] = new TcpReassembler(); flow.ProbeStreams[direction].Prime(unchecked(segment.Sequence + 1)); }
                    if (!flow.ProbeStreams.TryGetValue(direction, out var probe))
                        flow.ProbeStreams.Add(direction, probe = new TcpReassembler());
                    if (segment.Payload.Length > 0)
                        foreach (var frame in probe.Push(segment.Syn ? unchecked(segment.Sequence + 1) : segment.Sequence, segment.Payload))
                            if (frame.Length >= 2 && Aion2Protocol.KnownOpcodes.Contains(frame[0] << 8 | frame[1])) flow.Verified = true;
                    if (probe.Gaps > 0 || probe.Desyncs > 0) incomplete = true;
                }
                if (replay || flow.Verified && flows.Count == 1)
                {
                    if (replay || flow.Verified)
                    { Report(CaptureText.Get(replay ? "ReplayFlow" : "AutoFlow")); SelectFlow(id); }
                }
                else if (flow.Verified && flows.Count > 1)
                    Report(CaptureText.Get("MultipleFlows"));
                return;
            }
            DecodeSelected(flow, segment, fromServer, false);
        }
    }

    private void DecodeSelected(FlowState flow, CapturedTcpSegment segment, bool fromServer, bool fromCache)
    {
            if (segment.Rst || segment.Syn)
            {
                flow.Streams.Clear(); flow.Decoder = new Aion2Decoder();
                if (segment.Syn)
                {
                    Emit(new ThorMeter.Core.ConnectionEvent(segment.Timestamp,
                        flow.Id + "#" + (++connectionGeneration), true));
                    if (paused) Emit(new ThorMeter.Core.PauseEvent(segment.Timestamp, true));
                    Report(CaptureText.Get("ConnectionRestarted"));
                }
            }
            if (recorder is not null)
            {
                var line = new RecordedSegment(segment.Timestamp, segment.Source, segment.Destination,
                    segment.Sequence, Convert.ToBase64String(segment.Payload), segment.Syn, segment.Fin, segment.Rst);
                WriteRecord(line);
            }
            if (segment.Fin || segment.Rst) Report(CaptureText.Get("ConnectionClosed"));
            if (!fromServer) return;
            string direction = segment.Source + ">" + segment.Destination;
            if (!flow.Streams.TryGetValue(direction, out var stream))
                flow.Streams.Add(direction, stream = new TcpReassembler());
            if (segment.Syn) stream.Prime(unchecked(segment.Sequence + 1));
            if (segment.Payload.Length == 0) return;
            int oldGaps = stream.Gaps, oldDesyncs = stream.Desyncs;
            foreach (var frame in stream.Push(segment.Syn ? unchecked(segment.Sequence + 1) : segment.Sequence, segment.Payload))
            {
                if (!flow.Verified) { flow.Verified = true; FlowsChanged?.Invoke(); }
                foreach (var combatEvent in flow.Decoder.Decode(frame, segment.Timestamp)) Emit(combatEvent);
            }
            if (stream.Gaps > oldGaps || stream.Desyncs > oldDesyncs)
            {
                incomplete = true;
                lastReportedLoss++;
                if (lastReportedLoss <= 5 || lastReportedLoss % 50 == 0)
                    Report(CaptureText.Format("Gaps", flow.Streams.Values.Sum(x => x.Gaps), flow.Streams.Values.Sum(x => x.Desyncs)));
            }
            if (flow.Decoder.MalformedFrames > 0) incomplete = true;
            if (flow.Decoder.MalformedFrames > 0 && flow.Decoder.MalformedFrames % 100 == 1)
                Report(CaptureText.Format("MalformedFrames", flow.Decoder.MalformedFrames));
            if (flow.Decoder.Frames > 0 && flow.Decoder.Frames % 1000 == 0)
                Report(CaptureText.Format("DecodedFrames", flow.Decoder.Frames, flow.Decoder.DamageEvents,
                    flow.Decoder.HealingEvents, flow.Decoder.UnknownOpcodes));
    }

    private sealed record RecordedSegment(DateTimeOffset Timestamp, string Source, string Destination,
        uint Sequence, string PayloadBase64, bool Syn, bool Fin, bool Rst, bool SelectionStart = false,
        bool? PauseState = null, bool? ResetState = null)
    {
        public static RecordedSegment Pause(DateTimeOffset timestamp, bool paused) =>
            new(timestamp, "", "", 0, "", false, false, false, false, paused);
        public static RecordedSegment Reset(DateTimeOffset timestamp) =>
            new(timestamp, "", "", 0, "", false, false, false, ResetState: true);
    }

    public async Task ReplayAsync(string path, CancellationToken cancellationToken = default)
    {
        Stop();
        lock (gate) { flows.Clear(); selected = null; LastPacketTimestamp = null; incomplete = false; paused = false;
            lastReportedLoss = 0; Interlocked.Exchange(ref packetErrors, 0); FlowsChanged?.Invoke(); }
        int lineNumber = 0;
        await foreach (var line in ReadBoundedLines(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested(); lineNumber++;
            if (line is null) { incomplete = true; Report(CaptureText.Format("ReplayLineTooLong", lineNumber)); continue; }
            try
            {
                var entry = JsonSerializer.Deserialize<RecordedSegment>(line);
                if (entry?.ResetState is true)
                {
                    Emit(new ThorMeter.Core.ResetEvent(entry.Timestamp));
                    continue;
                }
                if (entry?.PauseState is bool pauseState)
                {
                    lock (gate) paused = pauseState;
                    Emit(new ThorMeter.Core.PauseEvent(entry.Timestamp, pauseState));
                    continue;
                }
                if (entry is null || string.IsNullOrWhiteSpace(entry.Source) || entry.Source.Length > 128
                    || string.IsNullOrWhiteSpace(entry.Destination) || entry.Destination.Length > 128
                    || entry.PayloadBase64 is null) throw new JsonException();
                byte[] payload = Convert.FromBase64String(entry.PayloadBase64);
                if (payload.Length > 65535) throw new JsonException();
                bool fromServer = entry.Source.EndsWith(":" + Aion2Protocol.ServerPort, StringComparison.Ordinal);
                Process(new CapturedTcpSegment(entry.Timestamp, entry.Source, entry.Destination, entry.Sequence,
                    payload, entry.Syn, entry.Fin, entry.Rst), fromServer, true);
                if (entry.SelectionStart)
                    SelectFlow(fromServer ? entry.Destination + "|" + entry.Source : entry.Source + "|" + entry.Destination);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
            { incomplete = true; Report(CaptureText.Format("InvalidReplayLine", lineNumber)); }
        }
        lock (gate) { if (selected is not null && flows.TryGetValue(selected, out var replayFlow)
            && replayFlow.Streams.Values.Any(s => s.BufferedBytes > 0)) incomplete = true; }
        Report(CaptureText.Format("ReplayComplete", lineNumber));
    }

    private void WriteRecord(RecordedSegment entry)
    {
        if (recorder is null) return;
        try { recorder.WriteLine(JsonSerializer.Serialize(entry)); recorder.Flush(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var failed = recorder; recorder = null;
            try { failed.Dispose(); } catch (IOException) { }
            incomplete = true;
            Report(CaptureText.Format("RecordingFailed", ex.Message));
        }
    }
    private static async IAsyncEnumerable<string?> ReadBoundedLines(string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var chunk = new byte[65536];
        using var line = new MemoryStream();
        bool oversized = false;
        int count;
        while ((count = await file.ReadAsync(chunk, cancellationToken)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                if (chunk[i] == 10)
                {
                    yield return oversized ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                    line.SetLength(0); oversized = false;
                }
                else if (!oversized)
                {
                    if (line.Length >= 2_000_000) oversized = true;
                    else line.WriteByte(chunk[i]);
                }
            }
        }
        if (line.Length > 0 || oversized)
            yield return oversized ? null : Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
    }

    private void Emit(ThorMeter.Core.CombatEvent e) => CombatEventReceived?.Invoke(e);
    private void Report(string message) => Diagnostic?.Invoke(message);
    public void Dispose() { if (disposed) return; Stop(); disposed = true; }
}


