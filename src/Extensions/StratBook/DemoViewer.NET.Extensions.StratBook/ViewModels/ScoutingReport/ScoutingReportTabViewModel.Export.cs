#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     The export's identity block and roster line, read off the header and the players table at export time, and
///     the one-pager's buy pictures.
/// </summary>
public sealed partial class ScoutingReportTabViewModel
{
    /// <summary>The layers the one-pager draws together, bottom to top: clouds under the spots and routes.</summary>
    internal static readonly IReadOnlyList<string> OnePagerLayers = ["util", "setups", "awp"];

    /// <summary>The buys the one-pager draws apart, left to right.</summary>
    internal static readonly IReadOnlyList<(ScoutingBuy Buy, string Label)> OnePagerBuys =
        [(ScoutingBuy.Full, "Full buy"), (ScoutingBuy.Force, "Force"), (ScoutingBuy.Eco, "Eco")];

    /// <summary>
    ///     The one-pager's pictures: for the map in view and each map in <paramref name="maps" />, each side's
    ///     utility, setups and AWP routes on one radar, once per buy. A buy with nothing drawn is left out.
    /// </summary>
    /// <param name="maps">The maps the starred lines name.</param>
    internal async Task<IReadOnlyList<ScoutingReportBuyStage>> BuyStagesAsync(IReadOnlyList<string> maps)
    {
        if (_data is not { } data)
        {
            return [];
        }

        List<string> order = [];
        if (SelectedMap?.Map is { Length: > 0 } inView)
        {
            order.Add(inView);
        }

        order.AddRange(maps.Where(m => !order.Contains(m, StringComparer.Ordinal)));
        // Each layer with the window it has now: a window picked mid-export does not change the pictures.
        List<(IScoutingLayer Layer, string Window)> layers =
        [
            .. OnePagerLayers.Select(id => Layers.All.FirstOrDefault(c => c.Id == id)?.Layer).OfType<IScoutingLayer>()
                .Select(l => (l, l is IScoutingWindowedLayer windowed ? windowed.Window : ""))
        ];

        // Every build starts at once; the stages are put together in order once all have landed.
        List<(string Map, ScoutingSide Side, string Label, List<(IScoutingLayer Layer, Task<ScoutingLayerResult> Build)> Builds)> pending = [];
        foreach (string map in order)
        {
            foreach (ScoutingSide side in (ScoutingSide[])[ScoutingSide.Ct, ScoutingSide.T])
            {
                foreach ((ScoutingBuy buy, string label) in OnePagerBuys)
                {
                    pending.Add((map, side, label,
                    [
                        .. layers.Where(l => l.Layer.AppliesTo(side)).Select(l => (l.Layer,
                            ScoutingLayerRegistry.Build(l.Layer, new ScoutingLayerContext(data, map, side, buy, l.Window),
                                CancellationToken.None)))
                    ]));
                }
            }
        }

        await Task.WhenAll(pending.SelectMany(p => p.Builds.Select(b => b.Build))).ConfigureAwait(true);
        List<ScoutingReportBuyStage> stages = [];
        foreach ((string map, ScoutingSide side, string label, List<(IScoutingLayer Layer, Task<ScoutingLayerResult> Build)> builds) in pending)
        {
            List<ScoutingMapMark> marks = [];
            List<string> drawn = [];
            foreach ((IScoutingLayer layer, Task<ScoutingLayerResult> build) in builds)
            {
                ScoutingLayerResult result = build.Result;
                if (result.Status.State == ScoutingLayerState.Ready && result.Marks.Count > 0)
                {
                    marks.AddRange(result.Marks);
                    drawn.Add(layer.Id == "awp" ? "AWP routes" : layer.NameFor(side).ToLowerInvariant());
                }
            }

            if (marks.Count > 0)
            {
                stages.Add(new ScoutingReportBuyStage(map, side.Label(),
                    $"{map} {side.Label()} · {label} · {string.Join(", ", drawn)}", marks));
            }
        }

        return stages;
    }

    /// <summary>
    ///     The identity block (record, ranks with the date each was set, the sample's dates, the opponent mix, head
    ///     to head) and the roster with role and rating, as the header and the players table show them now.
    /// </summary>
    internal ScoutingReportHeader ExportHeader()
    {
        if (SelectedTeam is not { } team)
        {
            return new ScoutingReportHeader();
        }

        List<string> ranks = [];
        foreach ((RankSystem system, string label) in (ReadOnlySpan<(RankSystem, string)>)
                 [(RankSystem.Vrs, "VRS"), (RankSystem.Hltv, "HLTV")])
        {
            if (_ranks.Current(team.Id, system) is { } rank)
            {
                ranks.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{label} {rank.Rank} (set {rank.AsOf:yyyy-MM-dd})"));
            }
        }

        string dates = "";
        if (_data is { Demos.Count: > 0 } data)
        {
            DateTime first = new(data.Demos.Min(d => d.Ticks));
            DateTime last = new(data.Demos.Max(d => d.Ticks));
            dates = $"{first.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} to {last.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";
        }

        return new ScoutingReportHeader
        {
            RecordLine = Wins + Losses == 0 ? RecordLine : $"{RecordLine} ({Wins}-{Losses})",
            Ranks = ranks,
            DateRangeLine = dates,
            // The header's how-to line is for the screen; the page says only that no opponent is ranked.
            OpponentMixLine = OpponentMixLine.StartsWith("no opponent ranks", StringComparison.Ordinal)
                ? "no opponent ranked yet"
                : OpponentMixLine,
            HeadToHeadLine = HeadToHeadLine,
            Roster =
            [
                .. Players.Select(p => new ScoutingReportRosterEntry(p.Name, p.Role, p.Rating.Label,
                    ScoutingMarksLayer.PlayerHex(p.Color)))
            ],
            RosterScope = PlayersAllMaps ? "every map" : SelectedMap?.Map ?? ""
        };
    }
}
