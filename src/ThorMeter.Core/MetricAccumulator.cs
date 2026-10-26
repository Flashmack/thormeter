namespace ThorMeter.Core;

internal readonly record struct ActivityInterval(DateTimeOffset Start, DateTimeOffset End);
internal sealed record MetricEntry(ulong SourceId, ulong TargetId, long Total, IReadOnlyList<ActivityInterval> Intervals);

/// <summary>Totals and independently timed activity intervals for a combat metric.</summary>
internal sealed class MetricAccumulator
{
    private sealed class Series
    {
        public long Total;
        public DateTimeOffset? First, Last;
        public readonly List<ActivityInterval> Completed = new();

        public void Add(long amount, DateTimeOffset timestamp)
        {
            if (Last.HasValue && timestamp < Last.Value) timestamp = Last.Value;
            if (First is null) First = timestamp;
            else if ((timestamp - Last!.Value).TotalSeconds >= CombatEngine.IdleSeconds)
            {
                Completed.Add(new ActivityInterval(First.Value, Last.Value));
                First = timestamp;
            }
            Last = timestamp;
            Total = SaturatingAdd(Total, amount);
        }

        public void Suspend()
        {
            if (First.HasValue && Last.HasValue) Completed.Add(new ActivityInterval(First.Value, Last.Value));
            First = Last = null;
        }

        public IReadOnlyList<ActivityInterval> Intervals(DateTimeOffset now)
        {
            if (First is null || Last is null) return Completed;
            var end = now >= Last && (now - Last.Value).TotalSeconds < CombatEngine.IdleSeconds ? now : Last.Value;
            var intervals = new ActivityInterval[Completed.Count + 1];
            Completed.CopyTo(intervals);
            intervals[^1] = new ActivityInterval(First.Value, end);
            return intervals;
        }
    }

    private readonly Dictionary<(ulong Source, ulong Target), Series> series = new();
    private readonly Dictionary<ulong, Series> sourceSeries = new();

    public void Add(ulong source, ulong target, long amount, DateTimeOffset timestamp)
    {
        if (amount <= 0) return;
        if (!series.TryGetValue((source, target), out var entry)) series[(source, target)] = entry = new Series();
        entry.Add(amount, timestamp);
        if (!sourceSeries.TryGetValue(source, out var sourceEntry)) sourceSeries[source] = sourceEntry = new Series();
        sourceEntry.Add(amount, timestamp);
    }

    public IEnumerable<MetricEntry> Entries(DateTimeOffset now) => series.Select(pair =>
        new MetricEntry(pair.Key.Source, pair.Key.Target, pair.Value.Total, pair.Value.Intervals(now)));
    public IEnumerable<MetricEntry> AllEntries(DateTimeOffset now) => sourceSeries.Select(pair =>
        new MetricEntry(pair.Key, 0, pair.Value.Total, pair.Value.Intervals(now)));

    public double Duration(DateTimeOffset now) => UnionDuration(AllEntries(now).SelectMany(e => e.Intervals));
    public void Suspend() { foreach (var entry in series.Values) entry.Suspend(); foreach (var entry in sourceSeries.Values) entry.Suspend(); }
    public void Reset() { series.Clear(); sourceSeries.Clear(); }

    public static double UnionDuration(IEnumerable<ActivityInterval> intervals)
    {
        double seconds = 0;
        DateTimeOffset? start = null, end = null;
        foreach (var interval in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (start is null) { start = interval.Start; end = interval.End; }
            else if (interval.Start <= end)
            {
                if (interval.End > end) end = interval.End;
            }
            else
            {
                seconds += (end!.Value - start.Value).TotalSeconds;
                start = interval.Start; end = interval.End;
            }
        }
        if (start.HasValue) seconds += (end!.Value - start.Value).TotalSeconds;
        return seconds;
    }

    public static long SaturatingAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
