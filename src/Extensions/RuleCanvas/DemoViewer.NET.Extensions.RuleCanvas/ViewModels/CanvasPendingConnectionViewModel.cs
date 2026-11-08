#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The wire being dragged. The editor writes the endpoints as the drag goes; the preview text is the
///     sentence the drop would write, or the refusal, asked of the semantics without applying anything,
///     so the author reads the outcome before letting go.
/// </summary>
public sealed partial class CanvasPendingConnectionViewModel : ObservableObject
{
    /// <summary>What the preview says while the wire ends over nothing it can land on.</summary>
    public const string NoTargetText = "drop on a stat, a define or a highlight";

    /// <summary>The connector the drag started from, written by the editor.</summary>
    [ObservableProperty]
    private object? _source;

    /// <summary>The connector or node the wire was dropped on, written by the editor on completion.</summary>
    [ObservableProperty]
    private object? _target;

    /// <summary>The connector or node under the wire's end while dragging, written by the editor.</summary>
    [ObservableProperty]
    private object? _previewTarget;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _previewText = "";

    public bool HasPreviewText => PreviewText.Length > 0;

    partial void OnSourceChanged(object? value) => Refresh();

    partial void OnPreviewTargetChanged(object? value) => Refresh();

    partial void OnPreviewTextChanged(string value) => OnPropertyChanged(nameof(HasPreviewText));

    private void Refresh()
    {
        CanvasNodeViewModel? source = NodeOf(Source);
        CanvasNodeViewModel? target = NodeOf(PreviewTarget);
        PreviewText = source is null ? ""
            : target is null || ReferenceEquals(source, target) ? NoTargetText
            : CanvasEditSemantics.ConnectPreview(source, target);
    }

    private static CanvasNodeViewModel? NodeOf(object? endpoint) => endpoint switch
    {
        CanvasConnectorViewModel connector => connector.Owner,
        CanvasNodeViewModel node => node,
        _ => null
    };
}
