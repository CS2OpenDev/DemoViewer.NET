#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     A hand-placed excerpt of <c>rules/player_round.rules.yaml</c> for settling the canvas style: the
///     per-player, per-round counts and the KAST flag that reads two of them. Five nodes, two wires, and
///     two nodes with no wire at all, which is the normal case in this language. Replaced by the
///     document-derived model once the style is settled.
/// </summary>
public static class SampleGraph
{
    public static (IReadOnlyList<CanvasNodeViewModel> Nodes, IReadOnlyList<CanvasConnectionViewModel> Connections) Build()
    {
        CanvasNodeViewModel kills = Count("round_kills", "kill", new Point(40, 60), null, "per round",
            new CanvasFacetViewModel("enemy", "true"));
        CanvasNodeViewModel kastAssists = Count("round_kast_assists", "assist", new Point(40, 230),
            "where enrich.kill.was_enemy_kill == true", "per round");
        kills.Outputs.Add(new CanvasConnectorViewModel { Title = "value" });
        kastAssists.Outputs.Add(new CanvasConnectorViewModel { Title = "value" });

        CanvasNodeViewModel kast = new()
        {
            Id = "round_kast",
            Kind = "flag",
            Chips = ["per round"],
            Expression = "when round_kills > 0 or round_kast_assists > 0 or player.survived or player.traded",
            Location = new Point(460, 130)
        };
        kast.Inputs.Add(new CanvasConnectorViewModel { Title = "round_kills" });
        kast.Inputs.Add(new CanvasConnectorViewModel { Title = "round_kast_assists" });

        CanvasNodeViewModel deaths = Count("round_deaths", "death", new Point(40, 430), null, "while match.live", "per round");
        CanvasNodeViewModel survived = Count("round_survived", "round_ended", new Point(40, 580), null,
            "while player.alive", "per round");

        List<CanvasConnectionViewModel> wires =
        [
            new(kills.Outputs[0], kast.Inputs[0]),
            new(kastAssists.Outputs[0], kast.Inputs[1])
        ];
        return ([kills, kastAssists, kast, deaths, survived], wires);
    }

    private static CanvasNodeViewModel Count(string id, string view, Point at, string? predicate, string gateOrScope,
        params CanvasFacetViewModel[] facets) =>
        Count(id, view, at, predicate, [gateOrScope], facets);

    private static CanvasNodeViewModel Count(string id, string view, Point at, string? predicate, string gate, string scope) =>
        Count(id, view, at, predicate, [gate, scope], []);

    private static CanvasNodeViewModel Count(string id, string view, Point at, string? predicate, string[] chips,
        CanvasFacetViewModel[] facets) =>
        new()
        {
            Id = id,
            Kind = "count",
            Chips = ["on " + view, .. chips],
            Facets = facets,
            Expression = predicate,
            Location = at
        };
}
