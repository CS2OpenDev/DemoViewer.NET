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
///         One instance serves one export: one hold per map is taken and given back at <see cref="Dispose" />.
///         Not thread-safe.
///     </para>
/// </summary>
public sealed class ScoutingStageRenderer : IDisposable
{
    /// <summary>The picture size: square, because a radar is, and wide enough to read a mark on paper.</summary>
    public static readonly SKSizeI Size = new(640, 640);

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
    /// <exception cref="InvalidOperationException">No app is installed to render.</exception>
    public byte[]? Render(string map, IReadOnlyList<ScoutingMapMark> marks)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(marks);
        if (marks.Count == 0)
        {
            return null;
        }

        IMapAsset? asset = AssetFor(map);
        Scene2DFrame frame = BuildFrame(map, marks, asset);
        WorldBounds bounds = frame.Map.NetworkedBounds ?? frame.Map.ObservedBounds;
        ScoutingMarksDocument document = new();
        document.Set(marks, true, 1f);
        byte[] png = MapAssets.RenderPng(frame, asset, [SceneLayerIds.Radar],
            ViewportTransform.Fit(Size.Width, Size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY), Size,
            [() => new PaperLayer(), () => new ScoutingMarksLayer("scouting.export", document)]);
        return OnPaper(png);
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
