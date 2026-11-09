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
        Editor.AddHandler(PointerWheelChangedEvent, OnFaceWheel, RoutingStrategies.Tunnel);
        Editor.AddHandler(KeyDownEvent, OnFaceKeyDown, RoutingStrategies.Tunnel);
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
        _viewModel?.MarkCompatibleFor(item);
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
            _viewModel?.MarkCompatible(null);
        }
    }

    private void OnEditorDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = _dragging is null || _viewModel is not { IsEditable: true } ? DragDropEffects.None : DragDropEffects.Copy;

    // A drop on a node's container goes to that node; on a wire, to the wire; anywhere else on the canvas
    // places a new entry there.
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
        else if (WireAt(e.Source as Visual, e.GetPosition(Editor)) is { } wire)
        {
            _viewModel.DropOnWire(item, wire, Editor.GetLocationInsideEditor(e));
        }
        else
        {
            _viewModel.PlaceItem(item, Editor.GetLocationInsideEditor(e));
        }
    }

    // The wire under a point: the event's own source when it is one, else the editor's hit test. Nodify's
    // connections live in the editor's connections layer and hit-test by their stroke.
    private CanvasConnectionViewModel? WireAt(Visual? source, Point at)
    {
        LabelledConnection? connection = WireOf(source) ?? WireOf(Editor.InputHitTest(at) as Visual);
        return connection?.DataContext as CanvasConnectionViewModel;
    }

    private static LabelledConnection? WireOf(Visual? source) =>
        source?.GetSelfAndVisualAncestors().OfType<LabelledConnection>().FirstOrDefault();

    // The node container starts a drag on any press it sees and captures the pointer, after which a picker
    // inside the face never gets the release that opens it. A left press on an editor stops at the face; a
    // right press goes on, so the container selects the node before its menu opens.
    // The New flyout: Create empty or Start with a stat; a written file closes it, a refusal leaves it open.
    private void OnCreateRuleset(object? sender, RoutedEventArgs e)
    {
        bool starter = sender is Control { Tag: "starter" };
        if (_viewModel?.CreateNewRuleset(starter) == true)
        {
            NewRulesetButton.Flyout?.Hide();
        }
    }

    private void OnNewRulesetKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnCreateRuleset(null, e);
            e.Handled = true;
        }
    }

    private void OnFacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Visual face && CanvasGestures.FaceKeepsPress(e.Source as Visual, face, e.Properties))
        {
            e.Handled = true;
        }
    }

    // The menu opens on the release, by which time the pointer may be elsewhere; the press is the spot.
    // Tunnelled, so a press while the wire editor is open commits it and goes no further: the canvas
    // neither pans nor selects under an open editor. A double-click on a wire opens its editor.
    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is { HasWireEditor: true })
        {
            _viewModel.CommitWireEditorCommand.Execute(null);
            e.Handled = true;
            return;
        }

        PointerPointProperties properties = e.GetCurrentPoint(Editor).Properties;
        if (properties.IsRightButtonPressed)
        {
            _menuAt = Editor.GetLocationInsideEditor(e);
            _rightPressAt = e.GetPosition(this);
        }
        else if (properties.IsLeftButtonPressed && e.ClickCount == 2 && _viewModel is not null
                 && WireOf(e.Source as Visual)?.DataContext is CanvasConnectionViewModel wire)
        {
            _viewModel.BeginEditWire(wire);
            e.Handled = true;
        }
    }

    // The wire menu's Edit condition: the item's DataContext is the wire.
    private void OnWireEdit(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: CanvasConnectionViewModel wire })
        {
            _viewModel?.BeginEditWire(wire);
        }
    }

    // Enter writes once any list inside is closed; with one open, Enter picks from it. Escape closes without a write.
    private void OnWireEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.CancelWireEditorCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not AutoCompleteBox { IsDropDownOpen: true } and not ComboBox { IsDropDownOpen: true }:
                _viewModel.CommitWireEditorCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // Focus leaving the editor altogether writes, as a face editor does; focus moving between its own boxes does not.
    private void OnWireEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { HasWireEditor: true })
        {
            return;
        }

        Visual? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        if (focused is null || !focused.GetSelfAndVisualAncestors().Contains(WireEditorHost))
        {
            _viewModel.CommitWireEditorCommand.Execute(null);
        }
    }

    // The editor sits centred on the wire's label, or on the wire's midpoint before the label was drawn,
    // kept inside the canvas; the first box inside takes focus so Enter and Escape land in it. The layer
    // shares the editor's cell and has no bounds of its own yet, so the editor's are the frame.
    private void PlaceWireEditor(CanvasWireEditorViewModel editor)
    {
        WireEditorHost.Measure(Size.Infinity);
        Size size = WireEditorHost.DesiredSize;
        Point anchor = AnchorOf(editor.Wire);
        double left = Math.Clamp(anchor.X - size.Width / 2, 0, Math.Max(0, Editor.Bounds.Width - size.Width));
        double top = Math.Clamp(anchor.Y - size.Height / 2, 0, Math.Max(0, Editor.Bounds.Height - size.Height));
        Canvas.SetLeft(WireEditorHost, left);
        Canvas.SetTop(WireEditorHost, top);
        Dispatcher.UIThread.Post(FocusWireEditor, DispatcherPriority.Background);
    }

    private void FocusWireEditor()
    {
        if (_viewModel is not { HasWireEditor: true })
        {
            return;
        }

        List<Control> boxes = [.. WireEditorHost.GetVisualDescendants().OfType<Control>().Where(c => c is TextBox or AutoCompleteBox && c.IsEffectivelyVisible)];
        Control target = boxes.FirstOrDefault(b => b.Name == "WireLiteral") ?? boxes.FirstOrDefault() ?? WireEditorHost;
        target.Focus();
        if (target is TextBox box)
        {
            box.SelectAll();
        }
    }

    // The label's centre in the editor's coordinates when the wire's control is realised, else the midpoint
    // of the wire's anchors taken through the viewport.
    private Point AnchorOf(CanvasConnectionViewModel wire)
    {
        LabelledConnection? connection = Editor.GetVisualDescendants().OfType<LabelledConnection>().FirstOrDefault(c => ReferenceEquals(c.DataContext, wire));
        if (connection is not null && connection.TranslatePoint(connection.LabelAnchor, Editor) is { } placed)
        {
            return placed;
        }

        Point mid = new((wire.Source.Anchor.X + wire.Target.Anchor.X) / 2, (wire.Source.Anchor.Y + wire.Target.Anchor.Y) / 2);
        return new Point((mid.X - Editor.ViewportLocation.X) * Editor.ViewportZoom, (mid.Y - Editor.ViewportLocation.Y) * Editor.ViewportZoom);
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

    // Tunnelled: a bare Up or Down in a closed editable combo would step its selection, and the gate and
    // field combos write a step at once. Opening the list is what the key asked for.
    private static void OnFaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (CanvasGestures.ListToOpen(e.Key, e.KeyModifiers, e.Source as Visual) is { } box)
        {
            box.SetCurrentValue(ComboBox.IsDropDownOpenProperty, true);
            e.Handled = true;
        }
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
    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e) => ZoomAtPointer(e);

    // Tunnelled, so the wheel is taken before a focused picker steps its selection on it. Handled even with
    // no vertical delta: a sideways tick would still step the picker.
    private void OnFaceWheel(object? sender, PointerWheelEventArgs e)
    {
        if (CanvasGestures.WheelOverClosedPicker(e.Source as Visual))
        {
            ZoomAtPointer(e);
            e.Handled = true;
        }
    }

    private void ZoomAtPointer(PointerWheelEventArgs e)
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
            _viewModel.WireDropped -= OfferNodesForWire;
            _viewModel.WireEditorOpened -= PlaceWireEditor;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        ApplyGestures();
        if (_viewModel is not null)
        {
            _viewModel.GraphReplaced += FitLater;
            _viewModel.JumpRequested += JumpTo;
            _viewModel.WireDropped += OfferNodesForWire;
            _viewModel.WireEditorOpened += PlaceWireEditor;
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

    // A wire dropped on empty canvas: a menu at the pointer with the nodes that can start there; the pick is
    // placed where the wire ended. The editor's MouseLocation is that point in graph space.
    private void OfferNodesForWire(CanvasNodeViewModel source)
    {
        if (_viewModel is null)
        {
            return;
        }

        Point at = Editor.MouseLocation;
        MenuFlyout menu = new() { Placement = PlacementMode.Pointer };
        foreach (GalleryItem option in _viewModel.WireDropOptions(source))
        {
            MenuItem item = new() { Header = option.Title, Tag = option };
            item.Click += (_, _) => _viewModel.PlaceItem(option, at);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0)
        {
            menu.ShowAt(Editor, true);
        }
    }

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

    // A readable fit, never a fit-everything: the zoom stays between 0.6 and 1. When the whole chain does not
    // fit at 0.6 the viewport frames its width from the event column through the authored column, anchored
    // at the extent's top-left, so the chain reads source to stat; the rest is a pan down or the minimap away.
    private void FitNow()
    {
        if (_viewModel is null || _viewModel.Nodes.Count == 0 || Editor.Bounds.Width <= 0 || Editor.Bounds.Height <= 0)
        {
            return;
        }

        ReadableFit fit = ReadableFit.Of(_viewModel.Nodes.Select(n => (n.Location, n.EstimatedSize, n.IsAuthorable)),
            Editor.Bounds.Size, OverlayReserve());
        Editor.ViewportZoom = fit.Zoom;
        Editor.ViewportLocation = fit.Location;
    }

    // The zoom cluster and the minimap sit over the editor's bottom edge; the fit keeps the chain above them.
    private double OverlayReserve()
    {
        double cluster = ZoomCluster.Bounds.Height + ZoomCluster.Margin.Bottom;
        double minimap = Minimap.IsVisible ? Math.Max(Minimap.Height, Minimap.Bounds.Height) + Minimap.Margin.Bottom : 0;
        return Math.Max(cluster, minimap);
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

    // F: the selection, or the whole chain when nothing is selected, framed at the zoom that fits it, at most
    // 1.0. Set directly: the editor's BringIntoView animates the location and never touches the zoom.
    private void FrameSelection()
    {
        if (_viewModel is null || Editor.Bounds.Width <= 0 || Editor.Bounds.Height <= 0)
        {
            return;
        }

        List<CanvasNodeViewModel> selected = [.. _viewModel.SelectedNodes.OfType<CanvasNodeViewModel>()];
        if (BoundsOf(selected.Count > 0 ? selected : _viewModel.Nodes) is { } bounds)
        {
            ReadableFit fit = ReadableFit.OfSelection(bounds.Inflate(ReadableFit.Pad), Editor.Bounds.Size, Editor.MinViewportZoom);
            Editor.ViewportZoom = fit.Zoom;
            Editor.ViewportLocation = fit.Location;
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
