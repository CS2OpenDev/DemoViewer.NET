#region

using System.ComponentModel;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace DemoViewer.NET.Models;

/// <summary>A named category of entities shown in the entity tree panel.</summary>
public class EntityGroup : INotifyPropertyChanged
{
    /// <summary>Delta count.</summary>
    public int DeltaCount { get; init; }

    /// <summary>Entities.</summary>
    public List<EntityState> Entities { get; init; } = [];

    /// <summary>Is expanded.</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    /// <summary>Name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Property changed.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}
