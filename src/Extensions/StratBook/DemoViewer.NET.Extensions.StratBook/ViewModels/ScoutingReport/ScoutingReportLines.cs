#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     The sentences the Scouting Report says about a whole load: how much each service read, or why it has
///     nothing. The layers show them as their empty reason; the tests pin the wording.
/// </summary>
public static class ScoutingReportLines
{
    private const string NoFacts = "no rounds yet: no demo of this team has round facts";
    private const string NoSide = "no rounds: Team Identity put this team on a side in no round with facts";

    /// <summary>"12 heatmaps over 80 CT rounds", or why there are none.</summary>
    /// <param name="set">The build.</param>
    public static string HeatmapLineFor(SetupHeatmapSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Heatmaps.Count == 0)
        {
            return set.DemosRead + set.DemosWithoutPositions == 0
                ? "no CT rounds yet: no demo of this team has round facts"
                : "no CT rounds: Team Identity put this team on CT in no round with facts";
        }

        int rounds = set.Heatmaps.Sum(h => h.Rounds.Count);
        string line = $"{Plural(set.Heatmaps.Count, "heatmap")} over {Plural(rounds, "CT round")}";
        return set.DemosWithoutPositions == 0
            ? line
            : $"{line} · {Plural(set.DemosWithoutPositions, "demo")} without positions; rebuild the index";
    }

    /// <summary>"40 rounds over 2 maps", the demos it could not fully read, or why there are none.</summary>
    /// <param name="set">The build.</param>
    public static string OpeningsLineFor(OpeningTendenciesSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Blocks.Count == 0)
        {
            return set.DemosRead == 0 ? NoFacts : NoSide;
        }

        int rounds = set.Blocks.Sum(b => b.Rounds.Count);
        int maps = set.Blocks.Select(b => b.Map).Distinct(StringComparer.Ordinal).Count();
        List<string> parts = [$"{Plural(rounds, "round")} over {Plural(maps, "map")}"];
        if (set.DemosWithoutGrenades > 0)
        {
            parts.Add($"{Plural(set.DemosWithoutGrenades, "demo")} without grenades; index grenades");
        }

        if (set.DemosWithoutPositions > 0)
        {
            parts.Add($"{Plural(set.DemosWithoutPositions, "demo")} without positions; rebuild the index");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"6 plants over 2 maps", the demos without positions, or why there are none.</summary>
    /// <param name="set">The build.</param>
    public static string PostPlantLineFor(PostPlantSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Blocks.Count == 0)
        {
            return set.DemosRead == 0 ? NoFacts : NoSide;
        }

        int plants = set.Blocks.Sum(b => b.Planted.Count);
        int maps = set.Blocks.Select(b => b.Map).Distinct(StringComparer.Ordinal).Count();
        List<string> parts = [$"{Plural(plants, "plant")} over {Plural(maps, "map")}"];
        if (set.DemosWithoutPositions > 0)
        {
            parts.Add($"{Plural(set.DemosWithoutPositions, "demo")} without positions; rebuild the index");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"4 pistols over 2 maps", or why there are none.</summary>
    /// <param name="set">The build.</param>
    public static string SituationalLineFor(SituationalBehaviourSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Blocks.Count == 0)
        {
            return set.DemosRead == 0 ? NoFacts : NoSide;
        }

        int pistols = set.Blocks.Sum(b => b.PistolRounds.Count);
        int maps = set.Blocks.Select(b => b.Map).Distinct(StringComparer.Ordinal).Count();
        return $"{Plural(pistols, "pistol")} over {Plural(maps, "map")}";
    }

    /// <summary>The Period Diff's summary line.</summary>
    /// <param name="set">The build.</param>
    public static string PeriodDiffNoteFor(PeriodDiffSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (!set.HasBothPeriods)
        {
            return set.TotalDemos == 0
                ? "no demos yet: this team has no side assigned in any indexed demo"
                : $"only {Plural(set.TotalDemos, "demo")} so far: not enough for two periods of {set.WindowSize}";
        }

        if (!set.RosterChanged)
        {
            return set.Recent.DominantRoster is null
                ? "no roster resolved in either period"
                : "same roster across both periods";
        }

        return $"roster changed: {set.Previous.DominantRoster!.Label} then {set.Recent.DominantRoster!.Label}";
    }

    /// <summary>The label for a buy class, lower case the way the fact vocabulary spells it.</summary>
    /// <param name="buy">The buy.</param>
    public static string BuyLabelFor(BuyType buy) => buy switch
    {
        BuyType.Pistol => "pistol",
        BuyType.Eco => "eco",
        BuyType.Semi => "semi buy",
        BuyType.Force => "force buy",
        BuyType.Full => "full buy",
        _ => "unknown buy"
    };

    private static string Plural(int count, string noun) => ScoutingLayerBase.Plural(count, noun);
}
