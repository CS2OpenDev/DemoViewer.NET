#region

using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.Testing;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Layers;
using DemoViewer.NET.Playback2D.Levels;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using DemoViewer.NET.Playback2D.Zones;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report's marks on the real radar art of the maps whose floors are light grey (de_nuke) and
///     the darker ones, in both scene palettes: every map layer's mark style, drawn at the places of the map's
///     own zones so they land on the floor and not the void. The PNGs land in
///     <see cref="HeadlessSession.ArtifactDir" /> as <c>p2-marks-&lt;map&gt;-&lt;theme&gt;.png</c> for a person
///     to look at; the test itself only checks that the map's art and places load and the picture renders.
/// </summary>
[NotInParallel(TestLanes.HeadlessUi)]
[Category("Integration")]
public class ScoutingMarksRenderTests
{
    private static readonly SKSizeI _size = new(1100, 1100);

    // The mark styles the map layers draw (sizes, colours and opacities as each layer file writes them).
    private static IEnumerable<(string Layer, IReadOnlyList<ScoutingMapMark> Marks)> Styles(float x, float y, float z,
        float px)
    {
        ScoutingMapMark M(float dx, float dy, ScoutingMarkShape shape, ScoutingColor color, float size,
            string? text = null, float opacity = 1f) =>
            new(x + dx * px, y - dy * px, z, shape, color, size, text, opacity);

        yield return ("setups",
        [
            M(-14, 0, ScoutingMarkShape.Dot, ScoutingColor.Player1, 4.5f, opacity: 0.8f),
            M(-4, 8, ScoutingMarkShape.Dot, ScoutingColor.Player2, 4.5f, opacity: 0.8f),
            M(6, -8, ScoutingMarkShape.Dot, ScoutingColor.Player3, 4.5f, opacity: 0.8f),
            M(16, 4, ScoutingMarkShape.Dot, ScoutingColor.Player4, 4.5f, opacity: 0.8f),
            M(0, -20, ScoutingMarkShape.Dot, ScoutingColor.Neutral, 10f, opacity: 0.95f),
            M(0, -20, ScoutingMarkShape.Dot, ScoutingColor.Player5, 8f)
        ]);
        yield return ("opening",
        [
            M(-10, 0, ScoutingMarkShape.Dot, ScoutingColor.Neutral, OpeningLayer.DotSize, opacity: 0.95f),
            M(10, 0, ScoutingMarkShape.Dot, ScoutingColor.Bad, OpeningLayer.RingSize, opacity: 0.3f),
            M(10, 0, ScoutingMarkShape.Ring, ScoutingColor.Bad, OpeningLayer.RingSize, opacity: 0.95f)
        ]);
        yield return ("retakes",
        [
            M(-8, 0, ScoutingMarkShape.Dot, ScoutingColor.Ct, 4f, opacity: 0.85f),
            M(4, 6, ScoutingMarkShape.Dot, ScoutingColor.Ct, 4f, opacity: 0.85f),
            M(10, -6, ScoutingMarkShape.Ring, ScoutingColor.Neutral, 5f)
        ]);
        yield return ("plants",
        [
            M(-12, 0, ScoutingMarkShape.Dot, ScoutingColor.T, 7f, opacity: 0.9f),
            M(4, 0, ScoutingMarkShape.Ring, ScoutingColor.T, 8f),
            M(-4, -20, ScoutingMarkShape.Badge, ScoutingColor.T, 11f, "1")
        ]);
        yield return ("lurks",
        [
            M(-6, 0, ScoutingMarkShape.Dot, ScoutingColor.Lurk, 5f, opacity: 0.85f),
            M(8, 4, ScoutingMarkShape.Dot, ScoutingColor.Lurk, 5f, opacity: 0.85f)
        ]);
        yield return ("utility",
        [
            M(-10, 0, ScoutingMarkShape.Cloud, ScoutingColor.T, 14f, "S", 0.9f),
            M(16, 10, ScoutingMarkShape.Cloud, ScoutingColor.Ct, 10f, "M", 0.9f)
        ]);
        yield return ("strats",
        [
            .. Enumerable.Range(0, 8).Select(i =>
                M(-24 + i * 5, i * 2, ScoutingMarkShape.Dot, ScoutingColor.T, 2f, opacity: 0.25f + 0.55f * (i + 1) / 8)),
            M(18, 16, ScoutingMarkShape.Badge, ScoutingColor.T, 9f, "A")
        ]);
    }

    public static IEnumerable<(string Map, string Theme)> Cases() =>
        from map in (string[])["de_nuke", "de_mirage", "de_inferno", "de_ancient"]
        from theme in (string[])["light", "dark"]
        select (map, theme);

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task EveryLayersMarks_ReadOnTheRadar(string map, string theme)
    {
        HostedMapAssets.Install();
        using IMapAssetLease? lease = MapAssets.Acquire(map);
        await Assert.That(lease).IsNotNull().Because($"the repository ships {map}'s radar");
        IMapAsset asset = lease!;
        WorldBounds bounds = asset.RadarBounds;
        ViewportTransform camera =
            ViewportTransform.Fit(_size.Width, _size.Height, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        float px = (float)((bounds.MaxX - bounds.MinX) / _size.Width);

        // One style per place, cycling, at the place's largest area: on the floor, on every floor.
        List<ZoneArea> spots =
        [
            .. (asset.Zones?.Zones.Areas ?? [])
                .GroupBy(a => a.PlaceId).Select(g => g.MaxBy(a => a.Area)!)
                .OrderBy(a => a.CentroidX).ThenBy(a => a.CentroidY)
        ];
        await Assert.That(spots.Count).IsGreaterThan(10);
        Dictionary<string, List<ScoutingMapMark>> byLayer = new(StringComparer.Ordinal);
        for (int i = 0; i < spots.Count; i++)
        {
            ZoneArea spot = spots[i];
            (string layer, IReadOnlyList<ScoutingMapMark> marks) = Styles((float)spot.CentroidX, (float)spot.CentroidY,
                (float)spot.Z, px).ElementAt(i % 7);
            if (!byLayer.TryGetValue(layer, out List<ScoutingMapMark>? list))
            {
                byLayer[layer] = list = [];
            }

            list.AddRange(marks);
        }

        Scene2DFrame frame = new()
        {
            Time = new SceneTime(0, 0, 0, 1.0 / 64, true),
            Markers = [],
            Map = new SceneMapInfo
            {
                MapName = map, NetworkedBounds = bounds, ObservedBounds = bounds, Radars = asset.DescribeRadars()
            }
        };

        using CpuSurfaceProvider provider = new();
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack([SceneLayerIds.Radar]);
        foreach ((string layer, List<ScoutingMapMark> marks) in byLayer)
        {
            ScoutingMarksDocument document = new();
            document.Set(marks, true, 1f);
            compositor.Add(new ScoutingMarksLayer("scouting." + layer, document));
        }

        using HeadlessSceneRenderer renderer = new(provider, compositor);
        renderer.Palette = theme == "light" ? ScenePalette.Light : ScenePalette.Dark;
        renderer.Camera = camera;
        renderer.Levels.SetAuthoritativeFloors(asset.Floors);
        renderer.Levels.RadarBinder = asset.CreateRadarBinder();
        SceneTime time = frame.Time;
        byte[] png = renderer.RenderPng(frame, in time, _size);

        string path = Path.Combine(HeadlessSession.ArtifactDir, $"p2-marks-{map}-{theme}.png");
        await File.WriteAllBytesAsync(path, png);
        Console.WriteLine($"[marks-render] {path}");
        await Assert.That(png.Length).IsGreaterThan(10_000);
    }
}
