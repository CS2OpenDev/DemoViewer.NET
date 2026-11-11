#region

using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Layers;

/// <summary>
///     The per-band caption (<c>Upper</c>, <c>Lower floor</c>, <c>Level 3</c>) in the pane's top-left
///     corner: the level's <see cref="MapLevel.Name" />, from <see cref="LevelNames" />. Never the Z band.
///     <para>
///         Renders only on a multi-level layout: the pre-v2 single-floor path passed a null label, and a
///         lone band naming the only floor over the whole map is noise.
///     </para>
/// </summary>
public sealed class FloorLabelLayer : ISceneLayer
{
    private const float MarginX = 8f;
    private const float MarginY = 6f;

    private readonly bool _ownsText;
    private readonly SKPaint _paint;
    private readonly TextBlobCache _text;

    /// <summary>Creates the layer.</summary>
    /// <param name="text">The shared blob cache. A private one when null, disposed with the layer.</param>
    public FloorLabelLayer(TextBlobCache? text = null)
    {
        _ownsText = text is null;
        _text = text ?? new TextBlobCache();
        _paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true };
    }

    /// <inheritdoc />
    public string Id => SceneLayerIds.FloorLabel;

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Hud;

    /// <inheritdoc />
    public int Order => 60;

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.Dynamic;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => 0;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (ctx.IsSingleLevel || ctx.Pane.Level is not { } level)
        {
            return;
        }

        string caption = CaptionFor(ctx, level);
        if (_text.Get(caption, SceneDefaults.FloorLabelSize) is not { } shaped)
        {
            return;
        }

        // Line-box top-left, which is what the pre-v2 `DrawText(text, new Point(8, 6))` also meant:
        // the caption's inset must not move with which glyphs the caption happens to start with.
        _paint.Color = ctx.Palette.Label;
        (float x, float y) = shaped.OriginForTopLeft(MarginX, MarginY);
        canvas.DrawText(shaped.Blob, x, y, _paint);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _paint.Dispose();
        if (_ownsText)
        {
            _text.Dispose();
        }
    }

    // A level built outside MapSpace may carry no name (a strat's bundle floors do); it still gets
    // the name the space would have given it rather than a blank caption.
    private static string CaptionFor(SceneRenderContext ctx, MapLevel level) =>
        string.IsNullOrWhiteSpace(level.Name)
            ? LevelNames.For(ctx.Frame.Map.MapName, ctx.LevelIndex, ctx.Levels?.Levels.Count ?? 0)
            : level.Name;
}
