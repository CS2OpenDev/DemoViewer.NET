namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Rotations (strip, CT): the first rotator's time to the hit site after T contact, and rotations off a site
///     on a fake. A placeholder until the strip build (P3.5) reads positions against first contact.
/// </summary>
public sealed class RotationsLayer() : ScoutingLayerBase("rotations", "Rotations", ScoutingLayerPlacement.Strip,
    ScoutingSides.Ct, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Ct), "D")
{
    /// <inheritdoc />
    public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScoutingLayerResult.Placeholder("P3.5",
            "First-rotator timing reads positions against first contact; it comes with the strip build (P3.5)."));
}
