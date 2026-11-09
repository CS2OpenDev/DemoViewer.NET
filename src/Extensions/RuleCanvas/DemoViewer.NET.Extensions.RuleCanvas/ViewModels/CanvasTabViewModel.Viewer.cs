#region

using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Pinned viewers: the node menu pins a panel under one node, the overflow menu under the selection. The
///     pin is layout, kept in the sidecar beside the fold; the panel's content is rebuilt from the run on
///     every live pass and never computed at rest.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>Pins a viewer under <paramref name="node" />, or takes it away, and writes the sidecar. The node menu.</summary>
    public void ToggleViewer(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        SetViewers([node], !node.IsViewerPinned);
    }

    /// <summary>Pins a viewer under every selected node. The overflow menu.</summary>
    [RelayCommand]
    private void PinSelectedViewers() => SetViewers([.. SelectedNodes.OfType<CanvasNodeViewModel>()], true);

    /// <summary>Takes the viewer away from every selected node.</summary>
    [RelayCommand]
    private void UnpinSelectedViewers() => SetViewers([.. SelectedNodes.OfType<CanvasNodeViewModel>()], false);

    private void SetViewers(IReadOnlyList<CanvasNodeViewModel> nodes, bool pinned)
    {
        bool changed = false;
        foreach (CanvasNodeViewModel node in nodes)
        {
            if (node.IsViewerPinned != pinned)
            {
                node.IsViewerPinned = pinned;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        ApplyLive(Nodes, Connections);
        SaveLayout();
    }

    // The panel for one pinned node: the track's, or what the run says about having none.
    private static CanvasLiveViewerViewModel? ViewerFor(CanvasNodeViewModel node, LiveValuesRun? run, LiveInstance? instance, NodeTrack? track, bool stale, int? tick)
    {
        if (!node.IsViewerPinned)
        {
            return null;
        }

        if (run is null || instance is null)
        {
            return CanvasLiveViewerViewModel.Said(LiveValuesText.NoRun, false);
        }

        return track is null
            ? CanvasLiveViewerViewModel.Said(LiveValuesText.NoData, stale)
            : CanvasLiveViewerViewModel.Of(run, node.Key, track, instance, node.ValueType == "bool", stale, tick);
    }
}
