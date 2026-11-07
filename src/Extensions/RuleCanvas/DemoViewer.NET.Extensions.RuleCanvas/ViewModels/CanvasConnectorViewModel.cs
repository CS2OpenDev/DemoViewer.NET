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
/// <param name="source">The output.</param>
/// <param name="target">The input.</param>
/// <param name="text">What the wire shows: the event that fires it, what it does, or the logic test.</param>
/// <param name="condition">The lowered condition, shown on hover, or null.</param>
/// <param name="isRead">A value read rather than a write; drawn dashed.</param>
/// <param name="isGate">A gate holding the target open while it is active; drawn as a long dash.</param>
/// <param name="isAnd">A logic input the target conjoins; drawn in the and colour.</param>
/// <param name="isOr">A logic input the target disjoins; drawn in the or colour.</param>
public sealed class CanvasConnectionViewModel(CanvasConnectorViewModel source, CanvasConnectorViewModel target,
    string text = "", string? condition = null, bool isRead = false, bool isGate = false, bool isAnd = false, bool isOr = false)
{
    /// <summary>A gate holding the target open while it is active; drawn dashed, labelled while.</summary>
    public bool IsGate { get; } = isGate;

    /// <summary>A logic input the target conjoins; the wire template sets the and class from it.</summary>
    public bool IsAnd { get; } = isAnd;

    /// <summary>A logic input the target disjoins; the wire template sets the or class from it.</summary>
    public bool IsOr { get; } = isOr;

    public CanvasConnectorViewModel Source { get; } = source;

    public CanvasConnectorViewModel Target { get; } = target;

    public string Text { get; } = text;

    public string? Condition { get; } = condition;

    public bool IsRead { get; } = isRead;

    public bool HasText => Text.Length > 0;
}
