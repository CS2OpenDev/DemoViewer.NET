#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The Canvas section view. Bindings, plus what a binding cannot do: fit the viewport to the graph after
///     the view model replaces it, feed the source pane its text, and move its caret to a selected node's line.
/// </summary>
public partial class CanvasTabView : UserControl
{
    private CanvasTabViewModel? _viewModel;

    // The item being dragged from the gallery. The platform payload only has to exist; the drop reads this.
    private GalleryItem? _dragging;

    /// <summary>Builds the view.</summary>
    public CanvasTabView()
    {
        InitializeComponent();
        Editor.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver);
        Editor.AddHandler(DragDrop.DropEvent, OnEditorDrop);
        DataContextChanged += (_, _) => Attach(DataContext as CanvasTabViewModel);
        Loaded += (_, _) => FitLater();
        Source.TextChanged += (_, _) => _viewModel?.SourceEdited(Source.Text);
    }

    private async void OnGalleryItemPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: GalleryItem item } || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = item;
        DataTransfer payload = new();
        payload.Add(DataTransferItem.CreateText(item.Name));
        try
        {
            await DragDrop.DoDragDropAsync(e, payload, DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            // A platform that refuses the drag leaves the gallery as it was.
        }
        finally
        {
            _dragging = null;
        }
    }

    private void OnEditorDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = _dragging is null || _viewModel is not { IsEditable: true } ? DragDropEffects.None : DragDropEffects.Copy;

    // A drop on a node's container goes to that node; anywhere else on the canvas places a new entry there.
    private void OnEditorDrop(object? sender, DragEventArgs e)
    {
        if (_dragging is not { } item || _viewModel is null)
        {
            return;
        }

        e.Handled = true;
        CanvasNodeViewModel? node = (e.Source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<Nodify.Avalonia.ItemContainer>().FirstOrDefault()?.DataContext as CanvasNodeViewModel;
        if (node is not null)
        {
            _viewModel.DropOnNode(item, node);
        }
        else
        {
            _viewModel.PlaceItem(item, Editor.GetLocationInsideEditor(e));
        }
    }

    // The node container starts a drag on any press it sees and captures the pointer, after which a picker
    // inside the face never gets the release that opens it. A press on an editor stops at the face.
    private void OnFacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().TakeWhile(v => !ReferenceEquals(v, sender))
                .Any(v => v is ComboBox or TextBox or AutoCompleteBox or CheckBox or Button))
        {
            e.Handled = true;
        }
    }

    // A typed literal is written when its editor loses focus; Enter is a key binding on the editor itself.
    private void OnFacetEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: CanvasFacetViewModel facet })
        {
            facet.Commit();
        }
    }

    // The picker is a command surface: the chosen view adds a stat, and the picker clears for the next.
    private void OnAddStatViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string view } picker && _viewModel is not null)
        {
            _viewModel.AddStatCommand.Execute(view);
            picker.SelectedItem = null;
        }
    }

    private void Attach(CanvasTabViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.GraphReplaced -= FitLater;
            _viewModel.JumpRequested -= JumpTo;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.GraphReplaced += FitLater;
            _viewModel.JumpRequested += JumpTo;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            Source.Text = _viewModel.DocumentText;
        }
    }

    // The pane shows the file's text after every load and write, and again on Revert. Setting Text raises
    // TextChanged, which reports the same text back and reads as clean.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasTabViewModel.DocumentText) && _viewModel is not null
            && Source.Text != _viewModel.DocumentText)
        {
            Source.Text = _viewModel.DocumentText;
        }
    }

    // The caret lands on the line and the whole line is selected, so the entry reads as picked rather than
    // merely scrolled into view. A line past the document's end, which a stale read could produce, is clamped.
    private void JumpTo(int line)
    {
        int safeLine = Math.Clamp(line, 1, Math.Max(1, Source.Document.LineCount));
        AvaloniaEdit.Document.DocumentLine documentLine = Source.Document.GetLineByNumber(safeLine);
        Source.TextArea.Caret.Line = safeLine;
        Source.TextArea.Caret.Column = 1;
        Source.Select(documentLine.Offset, documentLine.Length);
        Source.ScrollToLine(safeLine);
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
