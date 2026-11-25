#region

using System.Text.Json;
using DemoViewer.NET.Playback2D.Zones;

#endregion

namespace DemoViewer.NET.Playback2D.Assets;

/// <summary>
///     Parses a user overlay (<c>&lt;map&gt;.zones.json</c>) into a <see cref="ZoneOverlayDocument" />.
///     Structure only: a polygon that is not simple or a floor key that does not exist is the
///     applier's diagnostic, not this reader's exception, so one bad entry never costs the rest of the
///     file. Unknown fields are ignored, as the format promises.
/// </summary>
internal static class ZoneOverlayReader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses a file's bytes.</summary>
    /// <param name="json">The UTF-8 document.</param>
    /// <exception cref="JsonException">The document is not JSON of the overlay's shape.</exception>
    public static ZoneOverlayDocument Read(ReadOnlySpan<byte> json)
    {
        OverlayDto dto = JsonSerializer.Deserialize<OverlayDto>(json, _options)
            ?? throw new JsonException("the overlay is empty.");

        if (dto.SchemaVersion < 1)
        {
            throw new JsonException($"overlay schemaVersion {dto.SchemaVersion} is not a version this build reads.");
        }

        // A JSON null deserializes to a null entry, and a hand-edited file ends a list with ", null" easily
        // enough. It names nothing an applier diagnostic could point at, so it is refused here, as the
        // malformed overlay it is, rather than dereferenced.
        List<ZoneOverlayZone> zones = new(dto.Zones?.Count ?? 0);
        foreach (ZoneDto? zone in dto.Zones ?? [])
        {
            if (zone is null)
            {
                throw new JsonException($"zones[{zones.Count}] is null.");
            }

            zones.Add(new ZoneOverlayZone(zone.Name ?? "", zone.Floor ?? double.NaN, zone.Polygon ?? [],
                zone.MinZ, zone.MaxZ, zone.Replaces));
        }

        List<ZoneOverlayMerge> merges = new(dto.Merges?.Count ?? 0);
        foreach (MergeDto? merge in dto.Merges ?? [])
        {
            if (merge is null)
            {
                throw new JsonException($"merges[{merges.Count}] is null.");
            }

            RequireNames(merge.From, $"merges[{merges.Count}].from");
            merges.Add(new ZoneOverlayMerge(merge.Into ?? "", merge.From ?? []));
        }

        RequireNames(dto.Hidden, "hidden");
        return new ZoneOverlayDocument(dto.SchemaVersion, dto.MapName, dto.BasedOn, zones, merges,
            dto.Hidden ?? []);
    }

    private static void RequireNames(List<string>? names, string field)
    {
        for (int i = 0; i < (names?.Count ?? 0); i++)
        {
            if (names![i] is null)
            {
                throw new JsonException($"{field}[{i}] is null.");
            }
        }
    }

    private sealed class OverlayDto
    {
        public int SchemaVersion { get; init; }
        public string? MapName { get; init; }
        public string? BasedOn { get; init; }
        public List<ZoneDto>? Zones { get; init; }
        public List<MergeDto>? Merges { get; init; }
        public List<string>? Hidden { get; init; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class ZoneDto
    {
        public string? Name { get; set; }
        public double? Floor { get; set; }
        public double[]? Polygon { get; set; }
        public double? MinZ { get; set; }
        public double? MaxZ { get; set; }
        public bool Replaces { get; set; }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class MergeDto
    {
        public string? Into { get; set; }
        public List<string>? From { get; set; }
    }
}
