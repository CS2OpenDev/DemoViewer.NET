namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>
///     What one evaluation of the open ruleset over one demo left behind, reduced to what the canvas
///     shows: per declared entry and per instance, how often it fired, when first and last, and what it
///     held at the end. Holds no engine object, so it can cross to the UI thread whole.
/// </summary>
/// <param name="DemoPath">The demo evaluated.</param>
/// <param name="ProfileId">The demo source profile the build resolved.</param>
/// <param name="TickRate">The demo's tick rate.</param>
/// <param name="FirstTick">The first message tick the evaluation saw, server clock.</param>
/// <param name="LastTick">The last message tick, server clock.</param>
/// <param name="Instances">The game instance first, then one per materialised player in slot order.</param>
/// <param name="Nodes">One track per declared entry key and instance that has an engine node.</param>
/// <param name="Unbound">Declared entry keys the run has no engine node for.</param>
/// <param name="BuildTime">Time to lower the rulesets for this demo.</param>
/// <param name="EvaluateTime">Time to replay the demo through the chain with snapshots on.</param>
/// <param name="ReduceTime">Time to reduce the run to this record.</param>
/// <param name="PeakBytes">Working set growth across build, evaluate and reduce.</param>
public sealed record LiveValuesRun(
    string DemoPath,
    string ProfileId,
    int TickRate,
    int FirstTick,
    int LastTick,
    IReadOnlyList<LiveInstance> Instances,
    IReadOnlyDictionary<(RulesetDocumentNodeKey Key, int Instance), NodeTrack> Nodes,
    IReadOnlyList<RulesetDocumentNodeKey> Unbound,
    TimeSpan BuildTime,
    TimeSpan EvaluateTime,
    TimeSpan ReduceTime,
    long PeakBytes)
{
    /// <summary>
    ///     One track per drawn wire and instance whose engine edge the run recorded. A wire with no entry has
    ///     no engine edge of its own (a read the engine folds into its consumer's trigger, a logic input with
    ///     no edge); the canvas then speaks from the nodes at its ends.
    /// </summary>
    public IReadOnlyDictionary<(LiveWireKey Wire, int Instance), WireTrack> Wires { get; init; } =
        new Dictionary<(LiveWireKey, int), WireTrack>();

    /// <summary>Whether <paramref name="tick" /> lies inside the ticks this run saw.</summary>
    public bool Covers(int tick) => tick >= FirstTick && tick <= LastTick;
}

/// <summary>One copy of the chain: the game, or one player's.</summary>
/// <param name="Index">Position in <see cref="LiveValuesRun.Instances" />; the game is 0.</param>
/// <param name="Slot">The player's slot, or null for the game.</param>
/// <param name="Name">The player's name as the demo recorded it, or null for the game.</param>
public sealed record LiveInstance(int Index, int? Slot, string? Name);

/// <summary>One entry's record in one instance.</summary>
/// <param name="FireCount">How many times an edge wrote the node, summed over its incoming edges.</param>
/// <param name="FirstTick">The server tick of the first applied fire, or null when it never fired.</param>
/// <param name="LastTick">The server tick of the last applied fire, or null when it never fired.</param>
/// <param name="FinalDisplay">The node's display value at the end of the demo.</param>
/// <param name="FinalNumeric">The node's numeric value at the end of the demo, when it has one.</param>
/// <param name="FinalActive">Whether the node was active at the end of the demo.</param>
public sealed record NodeTrack(int FireCount, int? FirstTick, int? LastTick, string? FinalDisplay, float? FinalNumeric, bool FinalActive)
{
    /// <summary>The node's value after each applied fire, bounded; empty when the run kept no snapshot for it.</summary>
    public ValueSeries Series { get; init; } = ValueSeries.Empty;

    /// <summary>For a define, how many stats' fires <see cref="FireCount" /> sums; null for every other node.</summary>
    public int? ViaStats { get; init; }

    /// <summary>How many fires turned the node on, counted over <see cref="Series" />: a flag's on count at fires.</summary>
    public int OnCount { get; init; }

    /// <summary>How many fires turned the node off, counted over <see cref="Series" />.</summary>
    public int OffCount { get; init; }

    public bool IsDefine => ViaStats is not null;
}

/// <summary>What a drawn wire carries, which decides what its track says.</summary>
public enum LiveWireKind
{
    Trigger,
    Gate,
    Read,
    Logic
}

/// <summary>A drawn wire's identity in a run: the two nodes it joins and what it carries.</summary>
public readonly record struct LiveWireKey(RulesetDocumentNodeKey Source, RulesetDocumentNodeKey Target, LiveWireKind Kind);

/// <summary>One wire's record in one instance, from the engine edge(s) it stands for.</summary>
/// <param name="FireCount">How many times the edge applied.</param>
/// <param name="FirstTick">The server tick of the first fire, or null when it never fired.</param>
/// <param name="LastTick">The server tick of the last fire, or null.</param>
/// <param name="Ticks">Every fire's server tick, bounded.</param>
public sealed record WireTrack(int FireCount, int? FirstTick, int? LastTick, TickList Ticks)
{
    /// <summary>For a gate, how many messages of the trigger's kind the run dispatched: the most the gate could have let through.</summary>
    public int? TriggerCount { get; init; }
}

/// <summary>One sampled value: the node after one applied fire.</summary>
public readonly record struct ValuePoint(int Tick, bool Active, float? Numeric, string? Display);

/// <summary>
///     A node's value at its fires, sorted by tick, kept to <see cref="Cap" /> points. Above the cap every
///     k-th point is kept, the first and last always, and <see cref="IsSampled" /> says so. The value at a
///     tick is the last point at or before it.
/// </summary>
public sealed class ValueSeries
{
    /// <summary>Enough for a sparkline at any node width; 512 points is 12 KB with their strings.</summary>
    public const int Cap = 512;

    public static ValueSeries Empty { get; } = new([], [], false);

    private readonly int[] _ticks;

    private ValueSeries(int[] ticks, ValuePoint[] points, bool sampled)
    {
        _ticks = ticks;
        Points = points;
        IsSampled = sampled;
    }

    public IReadOnlyList<ValuePoint> Points { get; }

    /// <summary>The fire ticks, sorted, one per point.</summary>
    public IReadOnlyList<int> Ticks => _ticks;

    public bool IsSampled { get; }

    public int Count => _ticks.Length;

    /// <summary>Builds from points already sorted by tick, decimating above <see cref="Cap" />.</summary>
    public static ValueSeries From(IReadOnlyList<ValuePoint> points) => Build(points, false);

    /// <summary>As <see cref="From" />, for points that were already thinned from a longer list.</summary>
    public static ValueSeries Sampled(IReadOnlyList<ValuePoint> points) => Build(points, true);

    private static ValueSeries Build(IReadOnlyList<ValuePoint> points, bool sampled)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return Empty;
        }

        IReadOnlyList<int> keep = LiveLookup.Decimate(points.Count, Cap);
        ValuePoint[] kept = new ValuePoint[keep.Count];
        int[] ticks = new int[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            kept[i] = points[keep[i]];
            ticks[i] = kept[i].Tick;
        }

        return new ValueSeries(ticks, kept, sampled || keep.Count < points.Count);
    }

    /// <summary>The index of the last point at or before <paramref name="tick" />, or -1 before the first fire.</summary>
    public int IndexAt(int tick) => LiveLookup.IndexAtOrBefore(_ticks, tick);

    /// <summary>The value at <paramref name="tick" />, or null before the first fire.</summary>
    public ValuePoint? At(int tick)
    {
        int index = IndexAt(tick);
        return index < 0 ? null : Points[index];
    }
}

/// <summary>
///     The server ticks a wire fired at, sorted, kept to <see cref="Cap" />. Above the cap every k-th tick
///     is kept, the first and last always, and <see cref="IsSampled" /> says so; the count stays exact on
///     the track.
/// </summary>
public sealed class TickList
{
    /// <summary>2048 ints is 8 KB; a 64-slot per-player ruleset with 40 backed edges stays under 20 MB at worst.</summary>
    public const int Cap = 2048;

    public static TickList Empty { get; } = new([], false);

    private readonly int[] _ticks;

    private TickList(int[] ticks, bool sampled)
    {
        _ticks = ticks;
        IsSampled = sampled;
    }

    public IReadOnlyList<int> Ticks => _ticks;

    public bool IsSampled { get; }

    public int Count => _ticks.Length;

    /// <summary>Builds from sorted ticks, decimating above <see cref="Cap" />.</summary>
    public static TickList From(IReadOnlyList<int> ticks)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (ticks.Count == 0)
        {
            return Empty;
        }

        IReadOnlyList<int> keep = LiveLookup.Decimate(ticks.Count, Cap);
        int[] kept = new int[keep.Count];
        for (int i = 0; i < keep.Count; i++)
        {
            kept[i] = ticks[keep[i]];
        }

        return new TickList(kept, keep.Count < ticks.Count);
    }

    /// <summary>How many fires happened at or before <paramref name="tick" />, as the list has them.</summary>
    public int CountThrough(int tick) => LiveLookup.IndexAtOrBefore(_ticks, tick) + 1;

    /// <summary>The first fire after <paramref name="tick" />, or null.</summary>
    public int? After(int tick)
    {
        int index = LiveLookup.IndexAtOrBefore(_ticks, tick) + 1;
        return index < _ticks.Length ? _ticks[index] : null;
    }

    /// <summary>The last fire before <paramref name="tick" />, or null.</summary>
    public int? Before(int tick)
    {
        int index = LiveLookup.IndexAtOrBefore(_ticks, tick - 1);
        return index >= 0 ? _ticks[index] : null;
    }

    public int? First => _ticks.Length > 0 ? _ticks[0] : null;
}

/// <summary>Binary search over sorted ticks, and the decimation both bounded lists share. Pure.</summary>
public static class LiveLookup
{
    /// <summary>The index of the last tick at or before <paramref name="tick" />, or -1 when every tick is after it.</summary>
    public static int IndexAtOrBefore(ReadOnlySpan<int> sorted, int tick)
    {
        int lo = 0, hi = sorted.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (sorted[mid] <= tick)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    ///     The indices to keep out of <paramref name="count" /> so at most <paramref name="cap" /> remain: all
    ///     of them under the cap, else every k-th with the first and the last.
    /// </summary>
    public static IReadOnlyList<int> Decimate(int count, int cap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 2);
        if (count <= cap)
        {
            int[] all = new int[count];
            for (int i = 0; i < count; i++)
            {
                all[i] = i;
            }

            return all;
        }

        // Every k-th from the start fills cap - 1 slots at most; the last index closes the list.
        int step = (count + cap - 2) / (cap - 1);
        List<int> keep = new(cap);
        for (int i = 0; i < count - 1; i += step)
        {
            keep.Add(i);
        }

        keep.Add(count - 1);
        return keep;
    }
}
