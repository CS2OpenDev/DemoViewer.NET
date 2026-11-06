#region

using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     A hand-placed excerpt of <c>rules/aim_rating.rules.yaml</c> for choosing the canvas style: three
///     counts feeding two computes, a counter-strafe pair feeding a third compute, and one highlight. Seven
///     nodes, five wires. Replaced by the document-derived model once the style is settled.
/// </summary>
public static class SampleGraph
{
    public static (IReadOnlyList<CanvasNodeViewModel> Nodes, IReadOnlyList<CanvasConnectionViewModel> Connections) Build()
    {
        CanvasNodeViewModel bulletShots = Count("bullet_shots", "Shots", "shot", new Point(40, 60),
            new CanvasFacetViewModel("bullet", "true"));
        CanvasNodeViewModel enemyHits = Count("enemy_hits", "Hits", "shot_landed", new Point(40, 230),
            new CanvasFacetViewModel("enemy", "true"), new CanvasFacetViewModel("bullet", "true"));
        CanvasNodeViewModel accuracy = Compute("accuracy_pct", "Accuracy", "enemy_hits / bullet_shots * 100",
            new Point(420, 130), "enemy_hits", "bullet_shots");

        CanvasNodeViewModel csAttempts = Count("cs_attempts", "CS attempts", "shot", new Point(40, 410),
            new CanvasFacetViewModel("bullet", "true"), new CanvasFacetViewModel("counter_strafe_admitted", "true"));
        CanvasNodeViewModel csClean = Count("cs_clean", "CS clean", "shot", new Point(40, 590),
            new CanvasFacetViewModel("bullet", "true"), new CanvasFacetViewModel("counter_strafe_admitted", "true"),
            new CanvasFacetViewModel("counter_strafe_good", "true"));
        CanvasNodeViewModel counterStrafe = Compute("counter_strafe_pct", "CS%", "cs_clean / cs_attempts * 100",
            new Point(420, 450), "cs_clean", "cs_attempts");

        CanvasNodeViewModel highlight = new()
        {
            Id = "clean_strafe_round",
            Kind = "highlight",
            Label = "Clean strafing round",
            Chips = ["per round"],
            Expression = "cs_clean >= 5",
            Location = new Point(420, 640)
        };
        highlight.Inputs.Add(new CanvasConnectorViewModel { Title = "cs_clean" });

        List<CanvasConnectionViewModel> wires =
        [
            Wire(enemyHits, accuracy, 0),
            Wire(bulletShots, accuracy, 1),
            Wire(csClean, counterStrafe, 0),
            Wire(csAttempts, counterStrafe, 1),
            Wire(csClean, highlight, 0)
        ];
        return ([bulletShots, enemyHits, accuracy, csAttempts, csClean, counterStrafe, highlight], wires);
    }

    private static CanvasNodeViewModel Count(string id, string label, string view, Point at, params CanvasFacetViewModel[] facets)
    {
        CanvasNodeViewModel node = new()
        {
            Id = id,
            Kind = "count",
            Label = label,
            Chips = ["on " + view, "while round.active"],
            Facets = facets,
            Location = at
        };
        node.Outputs.Add(new CanvasConnectorViewModel { Title = "value" });
        return node;
    }

    private static CanvasNodeViewModel Compute(string id, string label, string expression, Point at, params string[] reads)
    {
        CanvasNodeViewModel node = new()
        {
            Id = id,
            Kind = "compute",
            Label = label,
            Chips = ["per match"],
            Expression = expression,
            Location = at
        };
        foreach (string read in reads)
        {
            node.Inputs.Add(new CanvasConnectorViewModel { Title = read });
        }

        return node;
    }

    // A count node's single output feeds every reader; a reader's inputs are in declaration order.
    private static CanvasConnectionViewModel Wire(CanvasNodeViewModel from, CanvasNodeViewModel to, int input) =>
        new(from.Outputs[0], to.Inputs[input]);
}
