#region

using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;

/// <summary>
///     Draws the map stage for the export: the map's radar with the marks of the starred layers, through
///     <see cref="MapAssets.RenderPng" /> and the stage's own <see cref="ScoutingMarksLayer" />, so the picture is the
///     one the Scouting Report shows. The one-pager is printed, so the picture is on paper: a white underlay
///     under the radar, and the renderer's dark background left around the panes is turned white too.
///     <para>
///         Each floor is framed on where the art draws (<see cref="MapContentBounds" />), not on the radar image's
///         square, so the picture has no empty margin. A map with several floors lays them side by side when the
///         row stays within <see cref="MaxWidth" />, and stacks them otherwise.
///     </para>
///     <para>
///         One instance serves one export: one hold per map is taken and given back at <see cref="Dispose" />.
///         Not thread-safe.
///     </para>
/// </summary>
public sealed class ScoutingStageRenderer : IDisposable
{
    /// <summary>The box one floor is fitted into: wide enough to read a mark on paper.</summary>
    public static readonly SKSizeI Size = new(640, 640);

    /// <summary>The widest picture: floors sit side by side only while the row fits.</summary>
    public const int MaxWidth = 960;

    /// <summary>White paper around each floor, in pixels.</summary>
    public const int Padding = 8;

    // The share of the art's extent left around it inside a floor's frame.
    private const double Margin = 0.03;

    // The renderer outlines each band; this much of the band's edge is left out so the outline is too. The
    // margin above keeps the art well inside it.
    private const int Inset = 4;

    private readonly Dictionary<string, IMapAssetLease?> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, IMapAssetLease?> _loadMapAsset;
    private bool _disposed;

    /// <param name="loadMapAsset">Takes a hold on a map's baked bundle; the app's lease registry, a stub in a test.</param>
    public ScoutingStageRenderer(Func<string, IMapAssetLease?>? loadMapAsset = null) =>
        _loadMapAsset = loadMapAsset ?? MapAssets.Acquire;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IMapAssetLease? lease in _assets.Values)
        {
            lease?.Dispose();
        }

        _assets.Clear();
    }

    /// <summary>
    ///     The stage of one map with these marks, or null with no marks or no app to draw (a test host, a tool):
    ///     the export then prints the lines without the picture.
    /// </summary>
    /// <param name="map">The map.</param>
    /// <param name="marks">The marks, in drawing order.</param>
    public static byte[]? TryRender(string map, IReadOnlyList<ScoutingMapMark> marks)
    {
        using ScoutingStageRenderer renderer = new();
        try
        {
            return renderer.Render(map, marks);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The PNG of the marks on the map, or null with no marks.</summary>
    /// <param name="map">The map.</param>
    /// <param name="marks">The marks, in drawing order.</param>
    /// <param name="paper">
    ///     White paper around and under the radar, for print; false keeps the renderer's dark stage, for a picture
    ///     shown on screen in either theme.
    /// </param>
    /// <exception cref="InvalidOperationException">No app is installed to render.</exception>
    public byte[]? Render(string map, IReadOnlyList<ScoutingMapMark> marks, bool paper = true)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(marks);
        if (marks.Count == 0)
        {
            return null;
        }

        IMapAsset? asset = AssetFor(map);
        Scene2DFrame frame = BuildFrame(map, marks, asset);
        WorldBounds bounds = MapContentBounds.Of(map, asset) ?? frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;
        int floors = Math.Max(1, asset?.Floors.Count ?? 1);
        SKSizeI band = BandSize(MapContentBounds.Aspect(bounds));
        ScoutingMarksDocument document = new();
        document.Set(marks, true, 1f);
        // The renderer stacks the floors as equal bands, highest on top, and points every band's camera at this
        // one transform, so each band is exactly one floor's art.
        byte[] png = MapAssets.RenderPng(frame, asset, [SceneLayerIds.Radar],
            ViewportTransform.Fit(band.Width, band.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, Margin),
            new SKSizeI(band.Width, band.Height * floors),
            paper
                ? [() => new PaperLayer(), () => new ScoutingMarksLayer("scouting.export", document)]
                : [() => new ScoutingMarksLayer("scouting.export", document)]);
        return paper
            ? Lay(OnPaper(png), floors, band)
            : Lay(png, floors, band, ScenePalette.Dark.Background);
    }

    /// <summary>One floor's band for art of this height over width, fitted into <see cref="Size" />.</summary>
    /// <param name="aspect">The art's height over its width.</param>
    public static SKSizeI BandSize(double aspect)
    {
        double a = double.IsFinite(aspect) && aspect > 0 ? aspect : 1;
        return a <= 1
            ? new SKSizeI(Size.Width, Math.Max(1, (int)Math.Round(Size.Width * a)))
            : new SKSizeI(Math.Max(1, (int)Math.Round(Size.Height / a)), Size.Height);
    }

    /// <summary>
    ///     The floors of a stacked picture laid out on paper: side by side, highest floor first, when the row fits
    ///     in <see cref="MaxWidth" />, else stacked; each floor with <see cref="Padding" /> of white around it.
    /// </summary>
    /// <param name="png">The rendered stack, highest floor on top.</param>
    /// <param name="floors">How many bands it holds.</param>
    /// <param name="band">One band's size.</param>
    /// <param name="background">The fill around the floors; white paper when null.</param>
    public static byte[] Lay(byte[] png, int floors, SKSizeI band, SKColor? background = null)
    {
        ArgumentNullException.ThrowIfNull(png);
        using SKBitmap? stack = SKBitmap.Decode(png);
        if (stack is null)
        {
            return png;
        }

        int cellW = band.Width - 2 * Inset + 2 * Padding, cellH = band.Height - 2 * Inset + 2 * Padding;
        bool row = floors > 1 && floors * cellW <= MaxWidth;
        int width = row ? floors * cellW : cellW;
        int height = row ? cellH : floors * cellH;
        using SKBitmap paper = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (SKCanvas canvas = new(paper))
        {
            canvas.Clear(background ?? SKColors.White);
            using SKPaint seam = new() { Color = new SKColor(0xCC, 0xCC, 0xCC), StrokeWidth = 1 };
            for (int i = 0; i < floors; i++)
            {
                SKRectI source = new(Inset, i * band.Height + Inset, band.Width - Inset,
                    Math.Min(stack.Height, (i + 1) * band.Height) - Inset);
                float x = row ? i * cellW + Padding : Padding;
                float y = row ? Padding : i * cellH + Padding;
                canvas.DrawBitmap(stack, source, new SKRect(x, y, x + source.Width, y + source.Height));
                if (i > 0)
                {
                    // A hairline between floors, so two floors read as two pictures.
                    if (row)
                    {
                        canvas.DrawLine(i * cellW, Padding, i * cellW, height - Padding, seam);
                    }
                    else
                    {
                        canvas.DrawLine(Padding, i * cellH, width - Padding, i * cellH, seam);
                    }
                }
            }
        }

        using SKData data = paper.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    ///     The picture with the renderer's background colour (the margin and the seams between floor panes) turned
    ///     white; the fill is solid, so an exact match finds it and nothing the radar or a mark drew.
    /// </summary>
    /// <param name="png">The rendered picture.</param>
    public static byte[] OnPaper(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        using SKBitmap? bitmap = SKBitmap.Decode(png);
        if (bitmap is null)
        {
            return png;
        }

        SKColor background = ScenePalette.Dark.Background;
        SKColor[] pixels = bitmap.Pixels;
        bool changed = false;
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == background)
            {
                pixels[i] = SKColors.White;
                changed = true;
            }
        }

        if (!changed)
        {
            return png;
        }

        bitmap.Pixels = pixels;
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // The radar's art and bounds when this host has the bundle, else the marks' own extent, padded.
    private static Scene2DFrame BuildFrame(string mapName, IReadOnlyList<ScoutingMapMark> marks, IMapAsset? asset)
    {
        SceneMapInfo map;
        if (asset is not null)
        {
            WorldBounds bounds = asset.RadarBounds;
            map = new SceneMapInfo
            {
                MapName = mapName,
                NetworkedBounds = bounds,
                ObservedBounds = bounds,
                Radars = asset.DescribeRadars()
            };
        }
        else
        {
            map = new SceneMapInfo
            {
                MapName = mapName,
                ObservedBounds = new WorldBounds(
                    marks.Min(m => m.X) - 512, marks.Min(m => m.Y) - 512,
                    marks.Max(m => m.X) + 512, marks.Max(m => m.Y) + 512)
            };
        }

        return new Scene2DFrame { Time = new SceneTime(0, 0, 0, 1.0 / 64, true), Markers = [], Map = map };
    }

    private IMapAssetLease? AssetFor(string map)
    {
        if (!_assets.TryGetValue(map, out IMapAssetLease? asset))
        {
            asset = _loadMapAsset(map);
            _assets[map] = asset;
        }

        return asset;
    }

    /// <summary>A white fill under the radar, so the art's transparent edges blend onto paper, not the dark theme.</summary>
    private sealed class PaperLayer : ISceneLayer
    {
        public string Id => "scouting.export.paper";

        public LayerSlot Slot => LayerSlot.Underlay;

        public int Order => -100;

        public LayerCacheHint Cache => LayerCacheHint.Static;

        public bool IsEnabled { get; set; } = true;

        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            canvas.DrawColor(SKColors.White);
        }

        public void Dispose()
        {
        }
    }
}
