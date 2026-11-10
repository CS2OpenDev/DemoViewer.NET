namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Strats run against them (CT): the opponents' T patterns against this team's CT, best first. A
///     placeholder until Phase 2 (P2.7) picks the patterns whose opponent is this team.
/// </summary>
public sealed class AgainstLayer() : ScoutingLayerBase("against", "Strats run against them",
    ScoutingLayerPlacement.Map, ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Badge, ScoutingColor.T), "D")
{
    /// <inheritdoc />
    public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScoutingLayerResult.Placeholder("P2.7",
            "The opponents' strats against this CT come with Phase 2 (P2.7)."));
}
