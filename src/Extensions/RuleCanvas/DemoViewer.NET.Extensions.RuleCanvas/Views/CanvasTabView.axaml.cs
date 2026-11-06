#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The Canvas section view. Bindings, plus one thing a binding cannot do: fit the viewport to the graph
///     after the view model replaces it, once the containers have been laid out.
/// </summary>
public partial class CanvasTabView : UserControl
{
    private CanvasTabViewModel? _viewModel;

    /// <summary>Builds the view.</summary>
    public CanvasTabView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as CanvasTabViewModel);
        Loaded += (_, _) => FitLater();
    }

    private void Attach(CanvasTabViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.GraphReplaced -= FitLater;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.GraphReplaced += FitLater;
        }
    }

    // Background priority runs after the layout pass that gives the editor its bounds. The extent comes from
    // the view model's positions and estimated sizes rather than the containers, which may not be measured
    // yet; the editor's own FitToScreen refuses an empty extent.
    private void FitLater() => Dispatcher.UIThread.Post(FitNow, DispatcherPriority.Background);

    private void FitNow()
    {
        if (_viewModel is null || _viewModel.Nodes.Count == 0 || Editor.Bounds.Width <= 0 || Editor.Bounds.Height <= 0)
        {
            return;
        }

        const double Pad = 40;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (CanvasNodeViewModel node in _viewModel.Nodes)
        {
            minX = Math.Min(minX, node.Location.X);
            minY = Math.Min(minY, node.Location.Y);
            maxX = Math.Max(maxX, node.Location.X + node.EstimatedSize.Width);
            maxY = Math.Max(maxY, node.Location.Y + node.EstimatedSize.Height);
        }

        double width = maxX - minX + 2 * Pad;
        double height = maxY - minY + 2 * Pad;
        double zoom = Math.Min(1, Math.Min(Editor.Bounds.Width / width, Editor.Bounds.Height / height));
        Editor.ViewportZoom = zoom;
        Editor.ViewportLocation = new Point(minX - Pad, minY - Pad);
    }
}
