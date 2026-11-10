#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>The export's identity block and roster line, read off the header and the players table at export time.</summary>
public sealed partial class ScoutingReportTabViewModel
{
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
            Roster = [.. Players.Select(p => new ScoutingReportRosterEntry(p.Name, p.Role, p.Rating.Label))],
            RosterScope = PlayersAllMaps ? "every map" : SelectedMap?.Map ?? ""
        };
    }
}
