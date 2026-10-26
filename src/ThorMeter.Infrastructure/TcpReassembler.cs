namespace ThorMeter.Infrastructure;

public readonly record struct CapturedTcpSegment(DateTimeOffset Timestamp, string Source, string Destination,
    uint Sequence, byte[] Payload, bool Syn = false, bool Fin = false, bool Rst = false);

/// <summary>One direction of TCP; bounded memory and duplicate suppression.</summary>
public sealed class TcpReassembler
{
    private const int MaxBytes = 256 * 1024;
    private readonly SortedDictionary<uint, byte[]> pending = new();
    private readonly List<byte> bytes = new();
    private uint? next;
    private bool synced;
    public int Retransmissions { get; private set; }
    public int Gaps { get; private set; }
    public int Desyncs { get; private set; }
    public int Frames { get; private set; }
    public int BufferedBytes => bytes.Count + pending.Values.Sum(p => p.Length);

    /// <summary>Use the SYN sequence so an out-of-order first payload cannot set a false baseline.</summary>
    public void Prime(uint nextSequence) { Reset(); next = nextSequence; }

    public IReadOnlyList<byte[]> Push(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) return Array.Empty<byte[]>();
        next ??= sequence;
        int delta = (int)(sequence - next.Value);
        if (delta < 0)
        {
            Retransmissions++;
            int overlap = -delta;
            if (overlap >= payload.Length) return Array.Empty<byte[]>();
            payload = payload[overlap..]; sequence = unchecked(sequence + (uint)overlap); delta = 0;
        }
        if (delta == 0)
        {
            Add(payload); next = unchecked(sequence + (uint)payload.Length); Drain();
        }
        else if (!pending.ContainsKey(sequence)) pending.Add(sequence, payload.ToArray());
        else Retransmissions++;
        if (pending.Count > 64 || BufferedBytes > MaxBytes)
        { Gaps++; Reset(); return Array.Empty<byte[]>(); }
        return CutFrames();
    }

    public void Reset() { pending.Clear(); bytes.Clear(); next = null; synced = false; }
    private void Add(ReadOnlySpan<byte> span) { foreach (byte b in span) bytes.Add(b); }
    private void Drain()
    {
        while (pending.Count > 0)
        {
            // Choose by modular distance from the next TCP sequence, not numeric uint order.
            var item = pending.OrderBy(x => (int)(x.Key - next!.Value) > 0 ? 1 : 0)
                .ThenBy(x => Math.Abs((long)(int)(x.Key - next!.Value))).First();
            int delta = (int)(item.Key - next!.Value);
            if (delta > 0) break;
            pending.Remove(item.Key); int overlap = -delta;
            if (overlap >= item.Value.Length) { Retransmissions++; continue; }
            Add(item.Value.AsSpan(overlap)); next = unchecked(item.Key + (uint)item.Value.Length);
        }
    }

    private IReadOnlyList<byte[]> CutFrames()
    {
        var frames = new List<byte[]>();
        while (bytes.Count > 0)
        {
            if (!synced)
            {
                int start = FindSync();
                if (start < 0)
                {
                    if (bytes.Count > 8192) { Desyncs++; bytes.RemoveRange(0, bytes.Count - 4096); }
                    break;
                }
                if (start > 0) { Desyncs++; bytes.RemoveRange(0, start); }
                synced = true;
            }
            if (!TryLength(bytes, 0, out int prefix, out int total))
            {
                if (bytes.Count >= 5) { Desyncs++; synced = false; bytes.RemoveAt(0); continue; }
                break;
            }
            if (total - prefix < 2 || total > 16384)
            { Desyncs++; synced = false; bytes.RemoveAt(0); continue; }
            if (bytes.Count < total) break;
            frames.Add(bytes.Skip(prefix).Take(total - prefix).ToArray());
            bytes.RemoveRange(0, total); Frames++;
        }
        return frames;
    }

    private int FindSync()
    {
        for (int start = 0; start < Math.Min(bytes.Count, 4096); start++)
        {
            int p = start;
            for (int n = 0; n < 3; n++)
            {
                if (!TryLength(bytes, p, out int prefix, out int total) || total - prefix < 2 || total > 16384 || p + total > bytes.Count) break;
                int op = bytes[p + prefix] << 8 | bytes[p + prefix + 1];
                if (!Aion2Protocol.KnownOpcodes.Contains(op)) break;
                p += total;
                if (p == bytes.Count || n == 2) return start;
            }
        }
        return -1;
    }

    internal static bool TryLength(IReadOnlyList<byte> source, int start, out int prefix, out int total)
    {
        prefix = 0; total = 0; long length = 0;
        for (; prefix < 5 && start + prefix < source.Count; prefix++)
        {
            byte b = source[start + prefix]; length |= (long)(b & 127) << (7 * prefix);
            if ((b & 128) == 0)
            {
                prefix++; long size = length + prefix - 4;
                total = size is >= 0 and <= int.MaxValue ? (int)size : int.MaxValue;
                return true;
            }
        }
        return false;
    }
}
