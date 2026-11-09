#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>The words the canvas puts on a track: the header badge, the badge's hover text, the wire's line, the seek menu.</summary>
public static class LiveValuesText
{
    /// <summary>The badge for a node the run has no engine node for.</summary>
    public const string NoData = "no data";

    /// <summary>Said under a tip when the run predates the text drawn.</summary>
    public const string StaleLine = "stale: from the text before your edit";

    /// <summary>The badge before a node's first fire, when the playhead is inside the run.</summary>
    public const string NotYet = "-";

    /// <summary>The audit's budget for an always-visible badge.</summary>
    public const int BadgeLength = 12;

    private static readonly CultureInfo _inv = CultureInfo.InvariantCulture;

    /// <summary>The final value, short: on/off for a bool, the display or numeric value otherwise, cut to <see cref="BadgeLength" />.</summary>
    public static string Badge(NodeTrack track, bool isBool) => Fit(Final(track, isBool));

    /// <summary>
    ///     The value at <paramref name="tick" />: the last fire's value at or before it, <see cref="NotYet" />
    ///     before the first fire, and the final value when the track kept no series.
    /// </summary>
    public static string Badge(NodeTrack track, bool isBool, int tick)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Series.Count == 0)
        {
            return Badge(track, isBool);
        }

        return BadgeAt(track, isBool, track.Series.IndexAt(tick));
    }

    /// <summary>The badge for the series point at <paramref name="index" />; <see cref="NotYet" /> for -1.</summary>
    public static string BadgeAt(NodeTrack track, bool isBool, int index)
    {
        ArgumentNullException.ThrowIfNull(track);
        return index < 0 ? NotYet : Fit(Value(track.Series.Points[index], track, isBool));
    }

    /// <summary>The tip's first line at the playhead: the value at <paramref name="index" />, or that nothing fired yet.</summary>
    public static string PlayheadLine(NodeTrack track, bool isBool, int tick, int index)
    {
        ArgumentNullException.ThrowIfNull(track);
        return index < 0
            ? string.Create(_inv, $"not yet fired at tick {tick}")
            : string.Create(_inv, $"at tick {tick}: {Value(track.Series.Points[index], track, isBool)}");
    }

    /// <summary>The wire tip's first line at the playhead: the fires so far.</summary>
    public static string FiresByLine(int count, int tick) => string.Create(_inv, $"{count} fires by tick {tick}");

    /// <summary>The badge's hover text for <paramref name="instance" />, with the stale line when <paramref name="stale" />.</summary>
    public static string Tip(NodeTrack track, LiveInstance instance, bool isBool, bool stale) => Tip(track, instance, isBool, stale, null);

    /// <summary>As <see cref="Tip(NodeTrack, LiveInstance, bool, bool)" />, with the value at <paramref name="tick" /> first when the playhead is inside the run.</summary>
    public static string Tip(NodeTrack track, LiveInstance instance, bool isBool, bool stale, int? tick)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(instance);
        List<string> lines = [];
        if (tick is { } at && track.Series.Count > 0)
        {
            lines.Add(PlayheadLine(track, isBool, at, track.Series.IndexAt(at)));
        }

        string fires = track.FireCount == 0 || track.FirstTick is null
            ? string.Create(_inv, $"fired {track.FireCount} times")
            : string.Create(_inv, $"fired {track.FireCount} times, first tick {track.FirstTick}, last tick {track.LastTick}");
        if (track.ViaStats is { } via)
        {
            fires = string.Create(_inv, $"{fires} via {via} {(via == 1 ? "stat" : "stats")}");
        }

        string line = string.Create(_inv, $"{Name(instance)}: {fires}, final {Final(track, isBool)}");
        if (isBool && track.Series.Count > 0)
        {
            line = string.Create(_inv, $"{line}, on {track.OnCount} times, off {track.OffCount} times");
        }

        lines.Add(line);
        if (stale)
        {
            lines.Add(StaleLine);
        }

        return string.Join('\n', lines);
    }

    /// <summary>A folded dependency's badge: the fires summed over the entries the open document reads.</summary>
    public static string Fires(int count) => Fit(string.Create(_inv, $"{count} fires"));

    /// <summary>
    ///     A folded dependency's hover text: one line per entry read, as its own tip would say it, then the sum
    ///     of the fires. The entries are the dependency's; the instance is the one the badges show.
    /// </summary>
    public static string DependencyTip(IReadOnlyList<(string Entry, NodeTrack Track)> tracks, LiveInstance instance, bool stale)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(instance);
        List<string> lines = [];
        foreach ((string entry, NodeTrack track) in tracks)
        {
            lines.Add(entry + ": " + Tip(track, instance, false, false));
        }

        lines.Add(string.Create(_inv, $"sum of fires over the read entries: {tracks.Sum(t => t.Track.FireCount)}"));
        if (stale)
        {
            lines.Add(StaleLine);
        }

        return string.Join('\n', lines);
    }

    /// <summary>The wire's hover line from its own track, worded by what the wire carries.</summary>
    public static string WireTip(WireTrack track, LiveWireKind kind, bool stale) => WireTip(track, kind, stale, null);

    /// <summary>As <see cref="WireTip(WireTrack, LiveWireKind, bool)" />, with the fires so far first when the playhead is inside the run.</summary>
    public static string WireTip(WireTrack track, LiveWireKind kind, bool stale, int? tick)
    {
        ArgumentNullException.ThrowIfNull(track);
        List<string> lines = [];
        if (tick is { } at && track.Ticks.Count > 0)
        {
            lines.Add(FiresByLine(track.Ticks.CountThrough(at), at));
        }

        lines.Add(kind switch
        {
            LiveWireKind.Gate => track.TriggerCount is { } total
                ? string.Create(_inv, $"open on {track.FireCount} of the trigger's {total} fires")
                : string.Create(_inv, $"open on {track.FireCount} fires"),
            LiveWireKind.Read => string.Create(_inv, $"read {track.FireCount} times"),
            _ => Fired(track.FireCount, track.FirstTick, track.LastTick)
        });
        if (stale)
        {
            lines.Add(StaleLine);
        }

        return string.Join('\n', lines);
    }

    /// <summary>A read wire with no engine edge of its own: what its source held when it last fired.</summary>
    public static string ReadFallback(NodeTrack source, bool isBool, bool stale)
    {
        ArgumentNullException.ThrowIfNull(source);
        string line = string.Create(_inv, $"value at last fire: {Final(source, isBool)}");
        return stale ? line + "\n" + StaleLine : line;
    }

    /// <summary>A logic wire with no engine edge of its own: its target's fires, said as the target's.</summary>
    public static string TargetFallback(NodeTrack target, bool stale)
    {
        ArgumentNullException.ThrowIfNull(target);
        string line = target.FireCount == 0 || target.FirstTick is null
            ? string.Create(_inv, $"target fired {target.FireCount} times")
            : string.Create(_inv, $"target fired {target.FireCount} times, first tick {target.FirstTick}, last tick {target.LastTick}");
        return stale ? line + "\n" + StaleLine : line;
    }

    /// <summary>The strip's position word: at the playhead's tick while it is inside the run, final otherwise.</summary>
    public static string Position(int? tick, LiveValuesRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return tick is { } at && run.Covers(at) ? string.Create(_inv, $"at tick {at}") : "final";
    }

    /// <summary>A seek item's header: the verb, the reason it is off in brackets, and sampled when the list was thinned.</summary>
    public static string SeekItem(string verb, string? reason, bool sampled)
    {
        ArgumentException.ThrowIfNullOrEmpty(verb);
        string text = reason is null ? verb : verb + " (" + reason + ")";
        return sampled ? text + " (sampled)" : text;
    }

    /// <summary>The instance picker's label: the player's name, or <c>game</c>.</summary>
    public static string Name(LiveInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Name ?? (instance.Slot is { } slot ? string.Create(_inv, $"slot {slot}") : "game");
    }

    private static string Fired(int fires, int? first, int? last) =>
        fires == 0 || first is null
            ? fires == 0 ? "never fired" : string.Create(_inv, $"fired {fires} times")
            : string.Create(_inv, $"fired {fires} times, first tick {first}, last tick {last}");

    private static string Fit(string value) => value.Length <= BadgeLength ? value : value[..(BadgeLength - 1)] + "…";

    private static string Final(NodeTrack track, bool isBool)
    {
        if (track.IsDefine)
        {
            return string.Create(_inv, $"{track.FireCount} fires");
        }

        if (isBool)
        {
            return track.FinalActive ? "on" : "off";
        }

        if (track.FinalDisplay is { Length: > 0 } display)
        {
            return display;
        }

        return track.FinalNumeric is { } numeric ? numeric.ToString("0.##", _inv) : "-";
    }

    private static string Value(ValuePoint point, NodeTrack track, bool isBool)
    {
        if (track.IsDefine)
        {
            return string.Create(_inv, $"{point.Numeric ?? 0:0} fires");
        }

        if (isBool)
        {
            return point.Active ? "on" : "off";
        }

        if (point.Display is { Length: > 0 } display)
        {
            return display;
        }

        return point.Numeric is { } numeric ? numeric.ToString("0.##", _inv) : "-";
    }
}
