namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     By score (strip): rounds won when leading, level or trailing. A placeholder until the strip build (P3.5);
///     on demos that start mid-match the score before a round is wrong until the app captures it (R2).
/// </summary>
public sealed class ScoreLayer() : ScoutingLayerBase("score", "By score", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D, R2")
{
    /// <inheritdoc />
    public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScoutingLayerResult.Placeholder("R2",
            "Rounds by score come with the strip build (P3.5). Demos that start mid-match need the score captured at freeze end (R2)."));
}
