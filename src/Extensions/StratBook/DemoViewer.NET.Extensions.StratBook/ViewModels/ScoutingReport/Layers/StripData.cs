#region

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     The strip layers' shared reads: the team's rounds (once per team load, off the UI thread), the map's
///     setup areas, and the row shapes the economy, pace, impact and score inspectors share.
/// </summary>
public static class StripData
{
    // One build per team load, shared by every strip layer and scope; dropped with the load.
    private static readonly ConditionalWeakTable<ScoutingReportData, Task<TeamRoundSet?>> _rounds = [];

    private static readonly ConditionalWeakTable<ScoutingReportData, ConcurrentDictionary<string, Task<SetupAreaClassifier>>>
        _areas = [];

    /// <summary>The team's rounds for a load, or null when Round Facts is not built here or could not be read.</summary>
    /// <param name="data">The team's loads.</param>
    public static Task<TeamRoundSet?> RoundsFor(ScoutingReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _rounds.GetValue(data, d => d.Sources.RoundFacts is { } facts
            ? ScoutingLayerContext.Offload<TeamRoundSet?>(() =>
            {
                try
                {
                    return TeamRounds.Build(d.Sources.Teams, d.Sources.Library, facts, d.TeamId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or InvalidOperationException)
                {
                    return null;
                }
            })
            : Task.FromResult<TeamRoundSet?>(null));
    }

    /// <summary>
    ///     A map's A · mid · B classifier, learned from the team's CT setups on it and the map's site regions; with
    ///     neither only the bombsite places count as a site.
    /// </summary>
    /// <param name="data">The team's loads.</param>
    /// <param name="map">The map.</param>
    public static Task<SetupAreaClassifier> AreasFor(ScoutingReportData data, string map)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _areas.GetValue(data, _ => new ConcurrentDictionary<string, Task<SetupAreaClassifier>>(
                StringComparer.OrdinalIgnoreCase))
            .GetOrAdd(map, m => LearnAsync(data, m));
    }

    private static async Task<SetupAreaClassifier> LearnAsync(ScoutingReportData data, string map)
    {
        SetupHeatmapSet? set = await data.Heatmaps.ConfigureAwait(false);
        SetupSiteRegions? regions = null;
        try
        {
            regions = data.Sources.SiteRegions?.Invoke(map);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No regions: distance from the learned site centres decides.
        }

        return SetupAreaClassifier.Learn(
            set?.Heatmaps.Where(h => string.Equals(h.Map, map, StringComparison.OrdinalIgnoreCase))
                .SelectMany(h => h.Rounds) ?? [], regions);
    }

    /// <summary>Why a strip layer over the team's rounds has nothing for the scope.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="set">The rounds, or null when they could not be read.</param>
    public static ScoutingLayerResult Missing(ScoutingLayerContext context, TeamRoundSet? set)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Data.Sources.RoundFacts is null)
        {
            return ScoutingLayerResult.Empty(ScoutingLayerResult.NotOnThisHost);
        }

        return set is null
            ? ScoutingLayerResult.Unreadable("round facts", null)
            : ScoutingLayerResult.Empty(set.DemosRead == 0
                ? "no demo of the team has round facts yet"
                : $"no rounds on {context.Map}");
    }

    /// <summary>"de_nuke · {what}", the Review title of a number that covers both sides.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="what">What the number counts.</param>
    public static string MapTitle(ScoutingLayerContext context, string what)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{context.Map} · {what}";
    }

    /// <summary>
    ///     A row of rounds out of a sample: "4 of 13 rounds", the share as its note (after <paramref name="note" />),
    ///     a bar, and the rounds behind the count.
    /// </summary>
    /// <param name="label">The label shown.</param>
    /// <param name="rounds">The rounds counted.</param>
    /// <param name="of">The sample.</param>
    /// <param name="star">The star text.</param>
    /// <param name="title">The Review title.</param>
    /// <param name="color">The bar's colour.</param>
    /// <param name="note">A note before the share.</param>
    public static ScoutingInspectorRow Share(string label, IReadOnlyList<TendencyRound> rounds, int of, string star,
        string title, ScoutingColor? color = null, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        string share = of > 0 ? ScoutingLayerBase.Percent((double)rounds.Count / of) : "";
        return new ScoutingInspectorRow
        {
            Label = label,
            Value = of > 0 ? ScoutingLayerBase.CountText(rounds.Count, of) : "none",
            StarText = star,
            Note = note is null ? share : share.Length == 0 ? note : $"{share} · {note}",
            BarPercent = of > 0 ? 100.0 * rounds.Count / of : null,
            Swatch = color,
            Rounds = rounds,
            OpenTitle = title
        };
    }

    /// <summary>
    ///     Whether a map-wide layer's read starts starred in this scope. Stars carry the side, and the long form
    ///     builds every side, so a map-wide read suggested on both would land in the one-pager twice: it is
    ///     suggested on T only.
    /// </summary>
    /// <param name="context">The scope.</param>
    public static bool SuggestOn(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Side == ScoutingSide.T;
    }

    /// <summary>"CT" or "T" for a Round Facts side.</summary>
    /// <param name="side">2 or 3.</param>
    public static string SideLabel(int side) => side == 3 ? "CT" : "T";

    /// <summary>The side's colour.</summary>
    /// <param name="side">2 or 3.</param>
    public static ScoutingColor SideColor(int side) => side == 3 ? ScoutingColor.Ct : ScoutingColor.T;

    /// <summary>The Round Facts buy a filter value names, or null for All.</summary>
    /// <param name="buy">The filter.</param>
    public static BuyType? BuyOf(ScoutingBuy buy) => buy switch
    {
        ScoutingBuy.Eco => BuyType.Eco,
        ScoutingBuy.Semi => BuyType.Semi,
        ScoutingBuy.Force => BuyType.Force,
        ScoutingBuy.Full => BuyType.Full,
        _ => null
    };

    /// <summary>The rounds the buy filter keeps: every round for All.</summary>
    /// <param name="rounds">The rounds.</param>
    /// <param name="buy">The filter.</param>
    public static IReadOnlyList<TeamRound> Filter(IEnumerable<TeamRound> rounds, ScoutingBuy buy) =>
        BuyOf(buy) is { } b ? [.. rounds.Where(r => r.Buy == b)] : [.. rounds];

    /// <summary>"eco ", "force " or nothing, for an empty-scope sentence.</summary>
    /// <param name="buy">The filter.</param>
    public static string BuyWord(ScoutingBuy buy) =>
        buy == ScoutingBuy.All ? "" : buy.ToString().ToLowerInvariant() + " ";
}
