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

    /// <summary>The node the endpoint sits on, so a wire drawn between two endpoints names two nodes.</summary>
    public CanvasNodeViewModel? Owner { get; init; }

    /// <summary>Written by the control as the node moves.</summary>
    [ObservableProperty]
    private Point _anchor;

    /// <summary>Must stay true for the anchor to track the node; every sample connector has a wire.</summary>
    [ObservableProperty]
    private bool _isConnected = true;
}

/// <summary>A wire from one node's output to another's input.</summary>
/// <param name="Source">The output.</param>
/// <param name="Target">The input.</param>
/// <param name="Text">What the wire shows: the event that fires it, what it does, or the logic test.</param>
/// <param name="Condition">The lowered condition, shown on hover, or null.</param>
/// <param name="IsRead">A value read rather than a write; drawn dashed.</param>
public sealed record CanvasConnectionViewModel(CanvasConnectorViewModel Source, CanvasConnectorViewModel Target,
    string Text = "", string? Condition = null, bool IsRead = false)
{
    public bool HasText => Text.Length > 0;
}
