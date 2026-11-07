#region

using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Where nodes go: the focus on one node's slice, and the arrangement saved to and reset from the
///     sidecar. The decisions are the arranger's; this is the observable surface over them.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>
    ///     The node whose slice of the chain is in focus, by its layout key, or null for the whole chain. A
    ///     change redraws from the composition already in hand: the file did not change.
    /// </summary>
    public RulesetDocumentNodeKey? FocusKey
    {
        get => _arranger.FocusKey;
        set
        {
            if (_arranger.FocusKey != value)
            {
                _arranger.FocusKey = value;
                NotifyFocus();
                Redraw(SelectedRuleset, keepArrangement: false, recompose: false);
            }
        }
    }

    public bool HasFocus => _arranger.HasFocus;

    public string FocusLabel => FocusKey?.Id ?? "";

    /// <summary>
    ///     Where the selected ruleset's arrangement lives: beside a user ruleset, under the extension's own
    ///     folder for a shipped one, or null when there is nowhere to keep it.
    /// </summary>
    public string? SidecarPath => _arranger.SidecarFor(SelectedRuleset);

    /// <summary>Focuses the first selected node's slice: itself, everything upstream of it, and everything it feeds.</summary>
    [RelayCommand]
    private void FocusSelected()
    {
        if (SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault() is { } node)
        {
            FocusNode(node);
        }
    }

    [RelayCommand]
    private void ClearFocus() => FocusKey = null;

    /// <summary>Focuses <paramref name="node" />'s slice; the rest of the chain is drawn faint until the focus is cleared.</summary>
    public void FocusNode(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        FocusKey = node.Key;
    }

    /// <summary>Writes every node's position to the sidecar. Bound to the editor's drag-completed command.</summary>
    [RelayCommand]
    private void SaveLayout()
    {
        if (!HasFocus && Nodes.Count > 0)
        {
            HasSavedLayout = _arranger.SaveLayout(SidecarPath, CurrentArrangement());
        }
    }

    /// <summary>Drops the saved arrangement and places the ruleset again.</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        CanvasArranger.ResetLayout(SidecarPath);
        Redraw(SelectedRuleset, keepArrangement: false);
    }

    private RulesetNodeLayout CurrentArrangement() =>
        CanvasArranger.Capture(Nodes.Select(n => new KeyValuePair<RulesetDocumentNodeKey, Avalonia.Point>(n.Key, n.Location)));

    private void NotifyFocus()
    {
        OnPropertyChanged(nameof(FocusKey));
        OnPropertyChanged(nameof(HasFocus));
        OnPropertyChanged(nameof(FocusLabel));
    }
}
