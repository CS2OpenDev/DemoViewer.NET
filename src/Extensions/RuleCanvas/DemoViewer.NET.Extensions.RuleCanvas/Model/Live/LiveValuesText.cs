#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>The words the canvas puts on a track: the header badge, the badge's hover text, the wire's line.</summary>
public static class LiveValuesText
{
    /// <summary>The badge for a node the run has no engine node for.</summary>
    public const string NoData = "no data";

    /// <summary>Said under a tip when the run predates the text drawn.</summary>
    public const string StaleLine = "stale: from the text before your edit";

    /// <summary>The audit's budget for an always-visible badge.</summary>
    public const int BadgeLength = 12;

    /// <summary>The final value, short: on/off for a bool, the display or numeric value otherwise, cut to <see cref="BadgeLength" />.</summary>
    public static string Badge(NodeTrack track, bool isBool)
    {
        ArgumentNullException.ThrowIfNull(track);
        string value = Final(track, isBool);
        return value.Length <= BadgeLength ? value : value[..(BadgeLength - 1)] + "…";
    }

    /// <summary>The badge's hover text for <paramref name="instance" />, with the stale line when <paramref name="stale" />.</summary>
    public static string Tip(NodeTrack track, LiveInstance instance, bool isBool, bool stale)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(instance);
        string fires = track.FireCount == 0 || track.FirstTick is null
            ? string.Create(CultureInfo.InvariantCulture, $"fired {track.FireCount} times")
            : string.Create(CultureInfo.InvariantCulture, $"fired {track.FireCount} times, first tick {track.FirstTick}, last tick {track.LastTick}");
        string line = string.Create(CultureInfo.InvariantCulture, $"{Name(instance)}: {fires}, final {Final(track, isBool)}");
        return stale ? line + "\n" + StaleLine : line;
    }

    /// <summary>The wire's hover line from its target's track.</summary>
    public static string WireTip(NodeTrack track, bool stale)
    {
        ArgumentNullException.ThrowIfNull(track);
        string line = track.LastTick is { } last
            ? string.Create(CultureInfo.InvariantCulture, $"fired {track.FireCount} times, last tick {last}")
            : string.Create(CultureInfo.InvariantCulture, $"fired {track.FireCount} times");
        return stale ? line + "\n" + StaleLine : line;
    }

    /// <summary>The instance picker's label: the player's name, or <c>game</c>.</summary>
    public static string Name(LiveInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.Name ?? (instance.Slot is { } slot ? string.Create(CultureInfo.InvariantCulture, $"slot {slot}") : "game");
    }

    private static string Final(NodeTrack track, bool isBool)
    {
        if (isBool)
        {
            return track.FinalActive ? "on" : "off";
        }

        if (track.FinalDisplay is { Length: > 0 } display)
        {
            return display;
        }

        return track.FinalNumeric is { } numeric ? numeric.ToString("0.##", CultureInfo.InvariantCulture) : "-";
    }
}
