#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     The block of a shared load a layer reads for its scope, and the empty result that says why there is
///     none: the service is not built on this host, the load failed, or the team has no round on that map and side.
/// </summary>
public static class LayerData
{
    /// <summary>The Opening Tendencies block of the scope, or null.</summary>
    /// <param name="context">The scope.</param>
    public static Task<OpeningTendencies?> Openings(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Data.OpeningsFor(context.Map, context.Side);
    }

    /// <summary>The Post-Plant block of the scope, or null.</summary>
    /// <param name="context">The scope.</param>
    public static Task<PostPlantBlock?> PostPlant(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Data.PostPlantFor(context.Map, context.Side);
    }

    /// <summary>The Situational Behaviour block of the scope, or null.</summary>
    /// <param name="context">The scope.</param>
    public static Task<SituationalBehaviourBlock?> Situational(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Data.SituationalFor(context.Map, context.Side);
    }

    /// <summary>Why the scope has no Opening Tendencies block.</summary>
    /// <param name="context">The scope.</param>
    public static async Task<ScoutingLayerResult> OpeningsMissing(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        OpeningTendenciesSet? set = await context.Data.Openings.ConfigureAwait(false);
        return Missing(context, context.Data.Sources.Openings is not null, "openings",
            set is null ? null : ScoutingReportLines.OpeningsLineFor(set), set?.Blocks.Count > 0);
    }

    /// <summary>Why the scope has no Post-Plant block.</summary>
    /// <param name="context">The scope.</param>
    public static async Task<ScoutingLayerResult> PostPlantMissing(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PostPlantSet? set = await context.Data.PostPlant.ConfigureAwait(false);
        return Missing(context, context.Data.Sources.PostPlant is not null, "post-plant",
            set is null ? null : ScoutingReportLines.PostPlantLineFor(set), set?.Blocks.Count > 0);
    }

    /// <summary>Why the scope has no Situational Behaviour block.</summary>
    /// <param name="context">The scope.</param>
    public static async Task<ScoutingLayerResult> SituationalMissing(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SituationalBehaviourSet? set = await context.Data.Situational.ConfigureAwait(false);
        return Missing(context, context.Data.Sources.Situational is not null, "situational",
            set is null ? null : ScoutingReportLines.SituationalLineFor(set), set?.Blocks.Count > 0);
    }

    private static ScoutingLayerResult Missing(ScoutingLayerContext context, bool hasService, string load,
        string? line, bool? anyBlock)
    {
        if (!hasService)
        {
            return ScoutingLayerResult.Empty("not built on this host");
        }

        if (line is null)
        {
            return ScoutingLayerResult.Empty(context.Data.Problems.TryGetValue(load, out string? why)
                ? $"unavailable: {why}"
                : "unavailable");
        }

        return ScoutingLayerResult.Empty(anyBlock == true
            ? $"no {context.Side.Label()} rounds on {context.Map}"
            : line);
    }
}
