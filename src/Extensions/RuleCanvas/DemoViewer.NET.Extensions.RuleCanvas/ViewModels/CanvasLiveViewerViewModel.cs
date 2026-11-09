#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>One row of a viewer's per-player breakdown: the player, their final value, and the bar's share of the largest.</summary>
/// <param name="Name">The player's name, or <c>slot n</c>.</param>
/// <param name="Value">The final value, in the badge's words.</param>
/// <param name="Fraction">The value over the largest in the breakdown, 0 to 1.</param>
/// <param name="IsSelected">Whether this is the instance the badges show.</param>
public sealed record CanvasViewerBar(string Name, string Value, double Fraction, bool IsSelected)
{
    /// <summary>The bar's width in the viewer, from the fraction.</summary>
    public double BarWidth => Math.Round(Fraction * CanvasLiveViewerViewModel.BarWidth, 1);
}

/// <summary>
///     The panel a pinned viewer shows under a node, built once from the stored series and the run: a
///     sparkline of the node's value at its fires with the playhead as a marker, the fire count and ticks,
///     the final value, and for a per-player stat the players' finals as a bar list. Nothing here walks the
///     demo; a playhead move sets one thickness.
/// </summary>
public sealed partial class CanvasLiveViewerViewModel : ObservableObject
{
    /// <summary>The sparkline box, in the node's device-independent pixels.</summary>
    public const double Width = 160;

    public const double Height = 36;

    /// <summary>The longest bar in the breakdown.</summary>
    public const double BarWidth = 90;

    /// <summary>At most this many players in the breakdown, the largest finals first.</summary>
    public const int BreakdownRows = 10;

    private readonly int _firstTick;
    private readonly int _lastTick;

    private CanvasLiveViewerViewModel(string message, bool stale)
    {
        Message = message;
        IsStale = stale;
        SparkPoints = [];
        Breakdown = [];
    }

    private CanvasLiveViewerViewModel(LiveValuesRun run, NodeTrack track, bool isBool, bool stale,
        IReadOnlyList<Point> points, IReadOnlyList<CanvasViewerBar> breakdown)
    {
        Message = "";
        IsStale = stale;
        _firstTick = run.FirstTick;
        _lastTick = Math.Max(run.LastTick, run.FirstTick + 1);
        Fires = LiveValuesText.ViewerFires(track);
        FinalText = LiveValuesText.ViewerFinal(track, isBool);
        IsSampled = track.Series.IsSampled;
        SparkPoints = points;
        Breakdown = breakdown;
    }

    /// <summary>Why there is nothing to draw (<see cref="LiveValuesText.NoRun" />, <see cref="LiveValuesText.NoData" />), or empty.</summary>
    public string Message { get; }

    public bool HasMessage => Message.Length > 0;

    /// <summary>The fire line: <c>fired 3 times, first tick 100, last tick 900</c>.</summary>
    public string Fires { get; } = "";

    /// <summary>The value line: <c>final 3</c>.</summary>
    public string FinalText { get; } = "";

    /// <summary>Whether the series behind the sparkline was thinned, said as <see cref="SampledNote" />.</summary>
    public bool IsSampled { get; }

    public string SampledNote => IsSampled ? LiveValuesText.Sampled : "";

    /// <summary>True when the run predates the text drawn; the viewer dims with the badges.</summary>
    public bool IsStale { get; }

    /// <summary>The sparkline as a stepped polyline inside the box; empty when the track kept no series.</summary>
    public IReadOnlyList<Point> SparkPoints { get; }

    public bool HasSeries => SparkPoints.Count > 0;

    /// <summary>The per-player finals, largest first, at most <see cref="BreakdownRows" />; empty for a game stat.</summary>
    public IReadOnlyList<CanvasViewerBar> Breakdown { get; }

    public bool HasBreakdown => Breakdown.Count > 0;

    /// <summary>Where the playhead marker sits: its left offset inside the box.</summary>
    [ObservableProperty]
    private Thickness _playheadMargin;

    /// <summary>True while the playhead lies inside the run.</summary>
    [ObservableProperty]
    private bool _hasPlayhead;

    /// <summary>A viewer that says <paramref name="message" /> and draws nothing.</summary>
    public static CanvasLiveViewerViewModel Said(string message, bool stale)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        return new CanvasLiveViewerViewModel(message, stale);
    }

    /// <summary>
    ///     The viewer for <paramref name="track" /> of <paramref name="key" /> in <paramref name="run" />, with the
    ///     breakdown over the run's players when any player has a track of their own for the key.
    /// </summary>
    public static CanvasLiveViewerViewModel Of(LiveValuesRun run, RulesetDocumentNodeKey key, NodeTrack track, LiveInstance instance,
        bool isBool, bool stale, int? tick)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(instance);
        CanvasLiveViewerViewModel viewer = new(run, track, isBool, stale, Sparkline(run, track.Series), BreakdownOf(run, key, instance, isBool));
        viewer.SetPlayhead(tick);
        return viewer;
    }

    /// <summary>Moves the marker to <paramref name="tick" />, or hides it for null. One thickness per call, no string.</summary>
    public void SetPlayhead(int? tick)
    {
        if (tick is not { } at || !HasSeries)
        {
            HasPlayhead = false;
            return;
        }

        double x = Math.Round(X(at, _firstTick, _lastTick), 1);
        if (!HasPlayhead || PlayheadMargin.Left != x)
        {
            PlayheadMargin = new Thickness(x, 0, 0, 0);
        }

        HasPlayhead = true;
    }

    // The x axis is the run's ticks, so the marker and the points share it; a value holds until the next fire.
    private static List<Point> Sparkline(LiveValuesRun run, ValueSeries series)
    {
        if (series.Count == 0)
        {
            return [];
        }

        int first = run.FirstTick;
        int last = Math.Max(run.LastTick, first + 1);
        double min = double.MaxValue, max = double.MinValue;
        foreach (ValuePoint point in series.Points)
        {
            double v = ValueOf(point);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        List<Point> points = new(series.Count * 2 + 1);
        double previousY = 0;
        for (int i = 0; i < series.Count; i++)
        {
            ValuePoint point = series.Points[i];
            double x = Math.Round(X(point.Tick, first, last), 1);
            double y = Math.Round(Y(ValueOf(point), min, max), 1);
            if (i > 0)
            {
                points.Add(new Point(x, previousY));
            }

            points.Add(new Point(x, y));
            previousY = y;
        }

        points.Add(new Point(Width, previousY));
        return points;
    }

    private static IReadOnlyList<CanvasViewerBar> BreakdownOf(LiveValuesRun run, RulesetDocumentNodeKey key, LiveInstance selected, bool isBool)
    {
        List<(LiveInstance Instance, NodeTrack Track, double Value)> players = [];
        foreach (LiveInstance instance in run.Instances)
        {
            if (instance.Slot is not null && run.Nodes.TryGetValue((key, instance.Index), out NodeTrack? own))
            {
                players.Add((instance, own, own.FinalNumeric ?? (own.FinalActive ? 1 : 0)));
            }
        }

        if (players.Count == 0)
        {
            return [];
        }

        double top = players.Max(p => p.Value);
        return
        [
            .. players.OrderByDescending(p => p.Value).ThenBy(p => LiveValuesText.Name(p.Instance), StringComparer.Ordinal)
                .Take(BreakdownRows)
                .Select(p => new CanvasViewerBar(LiveValuesText.Name(p.Instance), LiveValuesText.BreakdownValue(p.Track, isBool),
                    top > 0 ? Math.Clamp(p.Value / top, 0, 1) : 0, p.Instance.Index == selected.Index))
        ];
    }

    private static double ValueOf(ValuePoint point) => point.Numeric ?? (point.Active ? 1 : 0);

    private static double X(int tick, int first, int last) => Math.Clamp((double)(tick - first) / (last - first), 0, 1) * Width;

    // A flat series sits mid-box; otherwise the largest value touches the top with a 2px inset.
    private static double Y(double value, double min, double max) =>
        max > min ? 2 + (Height - 4) * (1 - (value - min) / (max - min)) : Height / 2;
}
