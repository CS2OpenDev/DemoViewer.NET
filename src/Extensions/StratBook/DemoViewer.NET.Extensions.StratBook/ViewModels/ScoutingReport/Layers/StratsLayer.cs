namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Detected strats (T): the mined T patterns of this team, most run first, with what they lost to and a 2D
///     play. A placeholder until Phase 2 (P2.7) filters the library-wide mining to the team.
/// </summary>
public sealed class StratsLayer() : ScoutingLayerBase("strats", "Detected strats", ScoutingLayerPlacement.Map,
    ScoutingSides.T, new ScoutingSwatch(ScoutingMarkShape.Badge, ScoutingColor.T), "E/D")
{
    /// <inheritdoc />
    public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScoutingLayerResult.Placeholder("P2.7",
            "Detected strats filtered to this team come with Phase 2 (P2.7)."));
}
