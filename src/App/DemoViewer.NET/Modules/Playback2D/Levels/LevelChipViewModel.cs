#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Playback2D.Levels;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Levels;

/// <summary>
///     One button in the level strip: a floor's short name, whether the map baked a radar picture for it,
///     and whether it is the one being shown. The Z band is in the tooltip only, for debugging the split.
/// </summary>
public sealed partial class LevelChipViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isActive;

    /// <summary>Creates a chip for one level.</summary>
    /// <param name="level">The level it selects.</param>
    /// <param name="isActive">Whether it is the level currently shown.</param>
    public LevelChipViewModel(MapLevel level, bool isActive)
    {
        ArgumentNullException.ThrowIfNull(level);

        Id = level.Id;
        Name = level.Name;
        Label = Shorten(level.Name);
        HasRadar = level.HasRadar;

        ZRange = string.Create(CultureInfo.InvariantCulture, $"z[{level.ZMin:F0}..{level.ZMax:F0}]");
        _isActive = isActive;
    }

    /// <summary>The level's stable identity.</summary>
    public MapLevelId Id { get; }

    /// <summary>The level's full name, as the on-canvas floor label shows it, e.g. <c>Upper floor</c>.</summary>
    public string Name { get; }

    /// <summary>The name cut to fit the chip, e.g. <c>Upper</c> or <c>L3</c>.</summary>
    public string Label { get; }

    /// <summary>The band, e.g. <c>z[-416..-111]</c>.</summary>
    public string ZRange { get; }

    /// <summary>Whether the map bundle bound a radar image to this level.</summary>
    public bool HasRadar { get; }

    /// <summary>True when there is no baked radar. Drives the glyph's visibility.</summary>
    public bool HasNoRadar => !HasRadar;

    /// <summary>
    ///     Tooltip. The no-radar case says so explicitly: the canvas falls back to the grid, and a user
    ///     who is not told will read that as a broken map rather than a missing asset.
    /// </summary>
    public string Tooltip => HasRadar
        ? $"{Name}  {ZRange}"
        : $"{Name}  {ZRange} — no baked radar for this level (the grid shows through)";

    // The chip is 52 px of 11 pt monospace: "Upper floor" becomes "Upper", "Level 3" becomes "L3".
    internal static string Shorten(string name)
    {
        if (name.EndsWith(" floor", StringComparison.Ordinal))
        {
            return name[..^" floor".Length];
        }

        return name.StartsWith("Level ", StringComparison.Ordinal) ? "L" + name["Level ".Length..] : name;
    }
}
