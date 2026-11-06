#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     An endpoint on a node face. The connector control writes <see cref="Anchor" /> in graph coordinates
///     as the node moves; a connection binds to the anchors of its two endpoints, never to copies of them.
/// </summary>
public sealed partial class CanvasConnectorViewModel : ObservableObject
{
    /// <summary>The label beside the endpoint: the sibling id read, or <c>value</c> for an output.</summary>
    public required string Title { get; init; }

    /// <summary>Written by the control as the node moves.</summary>
    [ObservableProperty]
    private Point _anchor;

    /// <summary>Must stay true for the anchor to track the node; every sample connector has a wire.</summary>
    [ObservableProperty]
    private bool _isConnected = true;
}

/// <summary>A value reference drawn as a wire, from the producing node's output to the reader's input.</summary>
/// <param name="Source">The output read.</param>
/// <param name="Target">The input that reads it.</param>
public sealed record CanvasConnectionViewModel(CanvasConnectorViewModel Source, CanvasConnectorViewModel Target);
