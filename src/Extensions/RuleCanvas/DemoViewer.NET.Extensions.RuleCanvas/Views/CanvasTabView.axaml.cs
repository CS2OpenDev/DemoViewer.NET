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
using Nodify.Avalonia;
using Nodify.Avalonia.Events;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The Canvas section view. Bindings, plus what a binding cannot do: fit the viewport to the graph after
///     the view model replaces it, feed the source pane its text, move its caret to a selected node's line,
///     swap the editor's gestures with editability, turn keys and menu clicks into view-model commands, and
///     drive the viewport from the zoom cluster and the minimap.
/// </summary>
public partial class CanvasTabView : UserControl
{
    private static readonly EditorGestures _editingGestures = CanvasGestures.Editing();
    private static readonly EditorGestures _readOnlyGestures = CanvasGestures.ReadOnly();

    private CanvasTabViewModel? _viewModel;

    // The item being dragged from the gallery. The platform payload only has to exist; the drop reads this.
    private GalleryItem? _dragging;
    private Point? _rightPressAt;

    // Where the last right press landed in graph space: what the canvas menu's Add items place at.
    private Point _menuAt;

    // The cutting line matches connection types exactly, so the subclass has to be named once.
    static CanvasTabView()
    {
        NodifyEditor.CuttingConnectionTypes.Add(typeof(LabelledConnection));
        NodifyEditor.EnableCuttingLinePreview = true;
    }

    /// <summary>Builds the view.</summary>
    public CanvasTabView()
    {
        InitializeComponent();
        Editor.InputGestures = _readOnlyGestures;
        Editor.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver);
        Editor.AddHandler(DragDrop.DropEvent, OnEditorDrop);
        Editor.AddHandler(PointerPressedEvent, OnEditorPointerPressed, RoutingStrategies.Tunnel);
        Editor.AddHandler(PointerReleasedEvent, OnEditorPointerReleased, RoutingStrategies.Bubble, true);
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Bubble, true);
        ZoomCluster.AddHandler(PointerWheelChangedEvent, OnOverlayWheel);
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
            .OfType<ItemContainer>().FirstOrDefault()?.DataContext as CanvasNodeViewModel;
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

    // The menu opens on the release, by which time the pointer may be elsewhere; the press is the spot.
    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed)
        {
            _menuAt = Editor.GetLocationInsideEditor(e);
            _rightPressAt = e.GetPosition(this);
        }
    }

    // Nodify's editor and its containers take the release without calling the base handler, so Avalonia
    // never raises the context request that would open a ContextMenu. A right click that did not pan opens
    // the menu of the control under the pointer here: a node's, a wire's, or the canvas's.
    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || _rightPressAt is not { } pressed)
        {
            return;
        }

        _rightPressAt = null;
        Point released = e.GetPosition(this);
        if (e.Handled || new Vector(released.X - pressed.X, released.Y - pressed.Y).Length > NodifyEditor.MouseActionSuppressionThreshold)
        {
            return;
        }

        Visual? hit = this.InputHitTest(released) as Visual ?? e.Source as Visual;
        Control? owner = hit?.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.ContextMenu is not null);
        if (owner?.ContextMenu is not { } menu)
        {
            return;
        }

        if (owner is ItemContainer { DataContext: CanvasNodeViewModel node } && _viewModel is not null && !_viewModel.SelectedNodes.Contains(node))
        {
            _viewModel.SelectedNodes.Clear();
            _viewModel.SelectedNodes.Add(node);
        }

        menu.Open(owner);
        e.Handled = true;
    }

    // Keys reach here after the editor's own set. A press inside a text editor on a node face is that
    // editor's: Delete and Ctrl+Z mean something else there.
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(v => v is TextBox or ComboBox))
        {
            return;
        }

        switch (CanvasKeyMap.Resolve(e.Key, e.KeyModifiers))
        {
            case CanvasKeyAction.DeleteSelection:
                _viewModel.DeleteSelectionCommand.Execute(null);
                break;
            case CanvasKeyAction.Undo:
                _viewModel.UndoCommand.Execute(null);
                break;
            case CanvasKeyAction.Redo:
                _viewModel.RedoCommand.Execute(null);
                break;
            case CanvasKeyAction.Duplicate:
                _viewModel.DuplicateCommand.Execute(null);
                break;
            case CanvasKeyAction.ClearFocusAndSelection:
                _viewModel.ClearFocusAndSelectionCommand.Execute(null);
                break;
            case CanvasKeyAction.FrameSelection:
                FrameSelection();
                break;
            case CanvasKeyAction.FitAll:
                FitAll();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // A typed literal is written when its editor loses focus; Enter is a key binding on the editor itself.
    private void OnFacetEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: CanvasFacetViewModel facet })
        {
            facet.Commit();
        }
    }

    // The YAML tab is detached while the Inspector is in front, so a jump made then lands when it comes back.
    // SelectionChanged bubbles from every picker inside the dock; only the tab strip's own counts.
    private void OnDockTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, Dock) && Dock.SelectedIndex == 1 && _viewModel is { SelectedLine: > 0 } vm)
        {
            Dispatcher.UIThread.Post(() => JumpTo(vm.SelectedLine), DispatcherPriority.Background);
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

    // The canvas menu.
    private void OnAddStatAt(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string view })
        {
            _viewModel?.AddStatAt(view, _menuAt);
        }
    }

    private void OnAddHighlightAt(object? sender, RoutedEventArgs e) => _viewModel?.AddHighlightAt(_menuAt);

    private void OnFit(object? sender, RoutedEventArgs e) => FitAll();

    // The node menu: the item's DataContext is the node under the pointer.
    private void OnNodeFocus(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
        {
            _viewModel?.FocusNode(node);
        }
    }

    private void OnNodeJump(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
        {
            JumpToNode(node);
        }
    }

    private void OnNodeDuplicate(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node)
        {
            _viewModel?.DuplicateNode(node);
        }
    }

    // Through the selection, so the delete reads the same as the Delete key and the strip button.
    private void OnNodeDelete(object? sender, RoutedEventArgs e)
    {
        if (NodeOf(sender) is { } node && _viewModel is not null)
        {
            Select(node);
            _viewModel.DeleteSelectedCommand.Execute(null);
        }
    }

    private void OnAlign(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string alignment } && EditorCommands.Align.CanExecute(alignment, Editor))
        {
            EditorCommands.Align.Execute(alignment, Editor);
        }
    }

    // The wire menu: the item's DataContext is the wire.
    private void OnWireRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: CanvasConnectionViewModel wire })
        {
            _viewModel?.DisconnectCommand.Execute(wire);
        }
    }

    private void OnWireJump(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: CanvasConnectionViewModel { Target.Owner: { } node } })
        {
            JumpToNode(node);
        }
    }

    // The zoom cluster and the minimap.
    private void OnZoomIn(object? sender, RoutedEventArgs e) => Editor.ZoomIn();

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Editor.ZoomOut();

    private void OnZoomReset(object? sender, RoutedEventArgs e) =>
        Editor.ZoomAtPosition(1 / Editor.ViewportZoom, ViewportCenter());

    private void OnMinimapZoom(object? sender, ZoomEventArgs e) => Editor.ZoomAtPosition(e.Zoom, e.Location);

    // A wheel over the cluster zooms the editor at that spot, as it would have without the overlay.
    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0)
        {
            Editor.ZoomAtPosition(Math.Pow(2.0, e.Delta.Y / 3.0), Editor.GetLocationInsideEditor(e.GetPosition(Editor), Editor));
            e.Handled = true;
        }
    }

    private static CanvasNodeViewModel? NodeOf(object? sender) => (sender as MenuItem)?.DataContext as CanvasNodeViewModel;

    private void Select(CanvasNodeViewModel node)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.SelectedNodes.Clear();
        _viewModel.SelectedNodes.Add(node);
    }

    // Selecting the node is what moves the caret; the pane is shown so the move is seen.
    private void JumpToNode(CanvasNodeViewModel node)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.ShowSource = true;
        Select(node);
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
        ApplyGestures();
        if (_viewModel is not null)
        {
            _viewModel.GraphReplaced += FitLater;
            _viewModel.JumpRequested += JumpTo;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            // The unsaved text when the pane is dirty: a tab switch rebuilds this view over the same view model.
            Source.Text = _viewModel.SourceText;
            FillAddStatMenu(_viewModel.Views);
        }
    }

    // The editor's gesture set follows editability: a shipped ruleset keeps selection, pan, zoom and the
    // keyboard, and loses every gesture that would write.
    private void ApplyGestures() =>
        Editor.InputGestures = _viewModel is { IsEditable: true } ? _editingGestures : _readOnlyGestures;

    // One item per view under Add stat on. Named rather than bound: the items need the press location,
    // which only the view holds.
    private void FillAddStatMenu(IReadOnlyList<string> views)
    {
        if (Editor.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(m => m.Name == "AddStatMenu") is not { } menu)
        {
            return;
        }

        List<MenuItem> items = [];
        foreach (string view in views)
        {
            MenuItem item = new() { Header = view, Tag = view };
            item.Click += OnAddStatAt;
            items.Add(item);
        }

        menu.ItemsSource = items;
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

        if (e.PropertyName == nameof(CanvasTabViewModel.IsEditable))
        {
            ApplyGestures();
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
        // Scrolling needs the editor's visual lines, which it has only while the YAML tab is in front.
        if (Source.IsAttachedToVisualTree())
        {
            Source.ScrollToLine(safeLine);
        }
    }

    // Background priority runs after the layout pass that gives the editor its bounds. The extent comes from
    // the view model's positions and estimated sizes rather than the containers, which may not be measured
    // yet; the editor's own FitToScreen refuses an empty extent.
    private void FitLater() => Dispatcher.UIThread.Post(FitNow, DispatcherPriority.Background);

    // A readable fit, never a fit-everything: the zoom stays between 0.8 and 1, so 11px text never renders
    // under 9px. When the whole chain does not fit at 0.8 the viewport frames the authored nodes instead and
    // anchors to the left-most column, where the chain starts; the rest is a pan or the minimap away.
    private void FitNow()
    {
        if (_viewModel is null || _viewModel.Nodes.Count == 0 || Editor.Bounds.Width <= 0 || Editor.Bounds.Height <= 0)
        {
            return;
        }

        ReadableFit fit = ReadableFit.Of(_viewModel.Nodes.Select(n => (n.Location, n.EstimatedSize, n.IsAuthorable)),
            Editor.Bounds.Size);
        Editor.ViewportZoom = fit.Zoom;
        Editor.ViewportLocation = fit.Location;
    }

    // Once the containers are measured the editor's own fit is the truer one; before that, the estimate.
    private void FitAll()
    {
        if (Editor.ItemsExtent.Width > 0 && Editor.ItemsExtent.Height > 0)
        {
            Editor.FitToScreen(null);
        }
        else
        {
            FitNow();
        }
    }

    // F: the selection, or the whole chain when nothing is selected, brought into view at a zoom that fits.
    private void FrameSelection()
    {
        if (_viewModel is null)
        {
            return;
        }

        List<CanvasNodeViewModel> selected = [.. _viewModel.SelectedNodes.OfType<CanvasNodeViewModel>()];
        if (BoundsOf(selected.Count > 0 ? selected : _viewModel.Nodes) is { } bounds)
        {
            _ = Editor.BringIntoView(bounds.Inflate(40));
        }
    }

    private Point ViewportCenter() => Editor.ViewportLocation + new Vector(Editor.ViewportSize.Width / 2, Editor.ViewportSize.Height / 2);

    // A measured container's size where there is one; the placement's estimate otherwise.
    private Rect? BoundsOf(IEnumerable<CanvasNodeViewModel> nodes)
    {
        Rect? union = null;
        foreach (CanvasNodeViewModel node in nodes)
        {
            Size size = Editor.ContainerFromItem(node) is ItemContainer { ActualSize: { Width: > 0, Height: > 0 } measured }
                ? measured
                : node.EstimatedSize;
            Rect rect = new(node.Location, size);
            union = union is { } u ? u.Union(rect) : rect;
        }

        return union;
    }
}
