#region

using System.Collections.Concurrent;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;

/// <summary>
///     Where a map's radar art actually draws: the world rectangle of its opaque pixels, over every floor's image.
///     A radar image is a square much larger than the playable map (de_nuke covers about half of its own), so
///     fitting a view to <see cref="IMapAsset.RadarBounds" /> leaves wide empty margins. One rectangle over all
///     floors keeps the floors aligned with each other. Read once per map and cached.
/// </summary>
public static class MapContentBounds
{
    // Below this alpha a pixel is the art's soft edge or a faint grid tick, not floor.
    private const byte OpaqueAlpha = 128;

    // A row or column needs this share of its pixels opaque to count, so a stray tick does not widen the frame.
    private const double MinFill = 0.006;

    private static readonly ConcurrentDictionary<string, WorldBounds?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The rectangle the map's art covers, or null when the asset has no decoded radar images (a test stub).
    /// </summary>
    /// <param name="map">The map's name, the cache key.</param>
    /// <param name="asset">The map's art.</param>
    public static WorldBounds? Of(string map, IMapAsset? asset)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (asset is null)
        {
            return null;
        }

        if (_cache.TryGetValue(map, out WorldBounds? known))
        {
            return known;
        }

        WorldBounds? found = Measure(asset);
        if (found is not null)
        {
            _cache[map] = found;
        }

        return found;
    }

    /// <summary>The height over the width of the rectangle, or 1 for none.</summary>
    /// <param name="bounds">The rectangle.</param>
    public static double Aspect(WorldBounds? bounds) =>
        bounds is { } b && b.MaxX - b.MinX > 0 ? (b.MaxY - b.MinY) / (b.MaxX - b.MinX) : 1;

    private static WorldBounds? Measure(IMapAsset asset)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (MapRadarImage radar in asset.DescribeRadars())
        {
            if (radar.Image is not { } image || OpaqueRect(image) is not { } px)
            {
                continue;
            }

            WorldBounds b = radar.Bounds;
            double sx = (b.MaxX - b.MinX) / image.Width;
            double sy = (b.MaxY - b.MinY) / image.Height;
            minX = Math.Min(minX, b.MinX + px.Left * sx);
            maxX = Math.Max(maxX, b.MinX + px.Right * sx);
            // Image rows run from the top, world Y from the bottom.
            maxY = Math.Max(maxY, b.MaxY - px.Top * sy);
            minY = Math.Min(minY, b.MaxY - px.Bottom * sy);
        }

        return minX < maxX && minY < maxY ? new WorldBounds(minX, minY, maxX, maxY) : null;
    }

    // The pixel rectangle of the image's opaque rows and columns, or null for an empty image.
    private static SKRectI? OpaqueRect(SKImage image)
    {
        int w = image.Width, h = image.Height;
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        using SKBitmap bitmap = new(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
        {
            return null;
        }

        int[] rows = new int[h];
        int[] cols = new int[w];
        ReadOnlySpan<byte> bytes = bitmap.GetPixelSpan();
        int stride = bitmap.RowBytes;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> row = bytes.Slice(y * stride, w * 4);
            for (int x = 0; x < w; x++)
            {
                if (row[x * 4 + 3] >= OpaqueAlpha)
                {
                    rows[y]++;
                    cols[x]++;
                }
            }
        }

        int rowMin = Math.Max(1, (int)(w * MinFill)), colMin = Math.Max(1, (int)(h * MinFill));
        int top = Array.FindIndex(rows, c => c >= rowMin);
        int bottom = Array.FindLastIndex(rows, c => c >= rowMin);
        int left = Array.FindIndex(cols, c => c >= colMin);
        int right = Array.FindLastIndex(cols, c => c >= colMin);
        return top < 0 || left < 0 ? null : new SKRectI(left, top, right + 1, bottom + 1);
    }
}
