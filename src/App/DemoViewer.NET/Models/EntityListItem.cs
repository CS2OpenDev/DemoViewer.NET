#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>Entity list item.</summary>
public class EntityListItem
{
    /// <summary>Delta count.</summary>
    public int DeltaCount { get; init; }

    /// <summary>Entity.</summary>
    public EntityState? Entity { get; init; }

    /// <summary>Is header.</summary>
    public bool IsHeader { get; init; }

    /// <summary>Entity slot index (carried so the entity grid avoids an O(N²) lookup). -1 for headers.</summary>
    public int SlotIndex { get; init; } = -1;
}
