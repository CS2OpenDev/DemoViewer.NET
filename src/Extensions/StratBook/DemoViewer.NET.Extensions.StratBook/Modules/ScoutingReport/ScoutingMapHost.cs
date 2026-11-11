#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Levels;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;

/// <summary>
///     The Scouting Report's map stage: a <see cref="MapView" /> of the map in view with one
///     <see cref="ScoutingMarksLayer" /> per map layer over the radar. Each layer draws its own
///     <see cref="ScoutingMarksDocument" />, which already says whether it shows and at what fade, so the host only
///     wires them once and redraws when one changes. Where the host has no art for the map (a test, an unknown
///     map) a line says so instead of an empty box.
///     <para>
///         Each floor's pane is framed on where the radar art actually draws (<see cref="MapContentBounds" />),
///         not on the radar image's square, when the map binds, when the side changes and when a pane is resized
///         while the user has not panned it. The Fit button and a double-click frame it again.
///     </para>
///     <para>
///         A mark that carries <see cref="ScoutingMarkInfo" /> answers the pointer: hovering it shows a card by
///         the pointer, and clicking it (a press and release that did not pan) pins it through
///         <see cref="ScoutingLayerRegistry.Select" />, which draws its detail over every layer. Clicking the
///         map away from a mark, or Escape, unpins it.
///     </para>
/// </summary>
public sealed class ScoutingMapHost : Decorator
{
    private readonly List<IDisposable> _layers = [];
    private readonly TextBlock _note = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        FontSize = 12,
        Margin = new Thickness(16)
    };

    private readonly Button _fit = new()
    {
        Content = "Fit",
        Classes = { "ghost" },
        FontSize = 11,
        Padding = new Thickness(8, 3),
        Margin = new Thickness(10),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom
    };

    // How far past a mark's edge the pointer still hits it, and how far a press may move and still be a click.
    private const double HitSlop = 5;
    private const double ClickSlop = 4;

    private readonly Canvas _hoverLayer = new() { IsHitTestVisible = false };
    private readonly Border _hover = new()
    {
        Padding = new Thickness(10, 7),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        MaxWidth = 300,
        IsVisible = false
    };

    private readonly StackPanel _hoverLines = new() { Spacing = 2 };
    private ScoutingMapMark? _hovered;
    private Point? _pressedAt;

    // One hand cursor for every stage, made on first hover; the cursor it replaced comes back when hover ends.
    private static Cursor? _hand;
    private bool _handShown;
    private Cursor? _cursorBeforeHover;

    // The last hit test's inputs and answer, so a pointer move that changes none of them reuses it.
    private (Point Point, LevelPane? Pane, ViewportTransform Camera, SkiaSharp.SKRect Rect, MapSpace? Space, int Panes, long Stamp)?
        _hitKey;
    private (ScoutingLayerChipViewModel Chip, ScoutingMapMark Mark)? _hitResult;

    // The world rectangle the bound map's art covers, or null with no art.
    private WorldBounds? _content;

    // The map and pane rectangles the panes were last framed for, so a repaint does not undo a pan.
    private string _fittedKey = "";
    private bool _fitQueued;

    private ScoutingReportTabViewModel? _vm;

    /// <summary>Creates the stage.</summary>
    public ScoutingMapHost()
    {
        ClipToBounds = true;
        _note.Bind(TextBlock.ForegroundProperty, _note.GetResourceObservable("TextBright"));
        Avalonia.Automation.AutomationProperties.SetName(_fit, "Fit the map to the stage");
        ToolTip.SetTip(_fit, "Frame the map again (or double-click it)");
        _fit.Click += (_, _) => FitToContent(true);
        _hover.Child = _hoverLines;
        _hover.Bind(Border.BackgroundProperty, _hover.GetResourceObservable("CardBg"));
        _hover.Bind(Border.BorderBrushProperty, _hover.GetResourceObservable("BorderSubtle"));
        _hoverLayer.Children.Add(_hover);
        Child = new Grid { Children = { Map, _note, _fit, _hoverLayer } };
        AddHandler(PointerMovedEvent, OnPointerMovedOver, RoutingStrategies.Tunnel, true);
        AddHandler(PointerPressedEvent, OnPointerPressedOver, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, OnPointerReleasedOver, RoutingStrategies.Tunnel, true);
        PointerExited += (_, _) => Hover(null, default);
        Map.EscapePressed += (_, _) => _vm?.Layers.ClearSelection();
        Map.MapBound += (_, _) =>
        {
            UpdateNote();
            UpdateFitButton();
            InvalidateMeasure();
        };
        Map.DoubleTapped += (_, _) => FitToContent(true);
        // The panes are built while the map view draws, so a layer that draws nothing tells the stage a frame
        // went by and the panes can be framed.
        Map.AddLayer("scouting.fit-probe", () => new FitProbe(QueueFit));
        UpdateFitButton();
    }

    /// <summary>The map view the stage is built on.</summary>
    public MapView Map { get; } = new();

    /// <summary>The height a stage takes with no map bound: room for the line that says why.</summary>
    public const double EmptyHeight = 360;

    /// <summary>
    ///     The height of one floor's band for a stage <paramref name="width" /> wide. The map view stacks a
    ///     multi-floor map's floors as bands of equal height, and each pane is framed on the art, so a band as
    ///     tall as the art is for this width fills the pane: a wide map (de_nuke) gets shorter bands, a square
    ///     one taller. The stage grows by a band per floor instead of squeezing two floors into one map's
    ///     height, capped so the page does not turn into one long map.
    /// </summary>
    /// <param name="width">The stage's width.</param>
    /// <param name="floors">The map's floors.</param>
    /// <param name="aspect">The art's height over its width; 1 when unknown.</param>
    public static double BandHeight(double width, int floors, double aspect = 1) =>
        Math.Clamp(width * aspect * 1.06, 400, floors > 1 ? 520 : 640);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        _measuredFloors = Floors();
        double height = _measuredFloors == 0
            ? EmptyHeight
            : _measuredFloors * BandHeight(width, _measuredFloors, MapContentBounds.Aspect(_content));
        Size size = new(width, height);
        Child?.Measure(size);
        return size;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        // The floors are known only once the art has loaded, which can land after the bind: measure again then.
        if (Floors() != _measuredFloors)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateMeasure);
        }

        return base.ArrangeOverride(finalSize);
    }

    private int _measuredFloors = -1;

    // The map's floors from its shipped art: the view's own level set fills in only once it first draws.
    private int _assetFloors;

    private int Floors() => Map.HasMap ? Math.Max(Math.Max(1, _assetFloors), Map.Space?.Levels.Count ?? 1) : 0;

    // The map's floors and the rectangle its art covers, from its shipped bundle.
    private static (int Floors, WorldBounds? Content) Describe(string? map)
    {
        if (string.IsNullOrEmpty(map))
        {
            return (0, null);
        }

        try
        {
            using IMapAssetLease? lease = MapAssets.Acquire(map);
            return (lease?.Floors?.Count ?? 0, lease is null ? null : MapContentBounds.Of(map, lease));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (0, null);
        }
    }

    private void QueueFit()
    {
        if (_fitQueued)
        {
            return;
        }

        _fitQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _fitQueued = false;
            FitToContent(false);
        });
    }

    /// <summary>
    ///     Frames every floor's pane on the map's art. Unforced, it only frames panes the stage has not framed at
    ///     their current size, and leaves a pane the user panned or zoomed alone unless the map changed.
    /// </summary>
    /// <param name="force">Frame every pane now, undoing the user's pan and zoom.</param>
    public void FitToContent(bool force)
    {
        IReadOnlyList<LevelPane> panes = Map.Panes;
        string map = _vm?.MapName ?? "";
        if (_content is not { } c || panes.Count == 0 || map.Length == 0)
        {
            return;
        }

        string key = map + "|" + string.Join(";", panes.Select(p =>
            $"{p.LevelId}:{p.ViewportRect.Width:0}x{p.ViewportRect.Height:0}"));
        if (!force && key == _fittedKey)
        {
            return;
        }

        bool sameMap = _fittedKey.StartsWith(map + "|", StringComparison.Ordinal);
        foreach (LevelPane pane in panes)
        {
            if (!force && sameMap && pane.Camera.ManualOverride)
            {
                continue;
            }

            pane.Camera.Current = ViewportTransform.Fit(pane.ViewportRect.Width, pane.ViewportRect.Height,
                c.MinX, c.MinY, c.MaxX, c.MaxY, 0.04);
            pane.Camera.ManualOverride = false;
            pane.SyncCameraEpoch();
        }

        _fittedKey = key;
        Map.Invalidate();
    }

    private void UpdateFitButton() => _fit.IsVisible = Map.HasMap && _content is not null;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null)
        {
            Attach(DataContext as ScoutingReportTabViewModel);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as ScoutingReportTabViewModel);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    private void Attach(ScoutingReportTabViewModel? vm)
    {
        if (ReferenceEquals(_vm, vm))
        {
            return;
        }

        Hover(null, default);
        _hitKey = null;
        if (_vm is not null)
        {
            _vm.Layers.Selection.Changed -= Map.Invalidate;
            _vm.PropertyChanged -= OnVmChanged;
            foreach (ScoutingLayerChipViewModel chip in _vm.Layers.All.Where(c => c.IsMap))
            {
                chip.Marks.Changed -= Map.Invalidate;
            }
        }

        foreach (IDisposable layer in _layers)
        {
            layer.Dispose();
        }

        _layers.Clear();
        _vm = vm;
        if (vm is null)
        {
            Map.MapName = null;
            return;
        }

        foreach (ScoutingLayerChipViewModel chip in vm.Layers.All.Where(c => c.IsMap))
        {
            string id = "scouting." + chip.Id;
            ScoutingMarksDocument marks = chip.Marks;
            _layers.Add(Map.AddLayer(id, () => new ScoutingMarksLayer(id, marks)));
            marks.Changed += Map.Invalidate;
        }

        _layers.Add(Map.AddLayer("scouting.selection",
            () => new ScoutingMarksLayer("scouting.selection", vm.Layers.Selection, 140)));
        vm.Layers.Selection.Changed += Map.Invalidate;
        vm.PropertyChanged += OnVmChanged;
        (_assetFloors, _content) = Describe(vm.MapName);
        _fittedKey = "";
        Map.MapName = vm.MapName;
        UpdateNote();
        UpdateFitButton();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScoutingReportTabViewModel.MapName) && _vm is { } vm)
        {
            (_assetFloors, _content) = Describe(vm.MapName);
            _fittedKey = "";
            Map.MapName = vm.MapName;
            UpdateNote();
            UpdateFitButton();
            InvalidateMeasure();
        }
        else if (e.PropertyName == nameof(ScoutingReportTabViewModel.Side))
        {
            FitToContent(true);
        }
    }

    /// <summary>
    ///     The mark under a point of the stage that says something about itself, and its layer: the nearest
    ///     within its radius plus <see cref="HitSlop" />, on the floor pane under the point, the inspected layer's
    ///     before a faded one's.
    /// </summary>
    /// <param name="point">The point, in the map view's coordinates.</param>
    public (ScoutingLayerChipViewModel Chip, ScoutingMapMark Mark)? MarkAt(Point point)
    {
        if (_vm is not { } vm || Map.PaneAt(point) is not { } pane)
        {
            return null;
        }

        MapSpace? space = Map.Space;
        ViewportTransform camera = pane.Camera.Current;
        SkiaSharp.SKRect rect = pane.ViewportRect;
        // Versions only grow, so their sum changes whenever any document does.
        long stamp = 0;
        foreach (ScoutingLayerChipViewModel chip in vm.Layers.All)
        {
            stamp += chip.Marks.Version;
        }

        (Point, LevelPane?, ViewportTransform, SkiaSharp.SKRect, MapSpace?, int, long) key =
            (point, pane, camera, rect, space, Map.Panes.Count, stamp);
        if (_hitKey is { } last && last.Equals(key))
        {
            return _hitResult;
        }

        bool filter = pane.LevelIndex >= 0 && Map.Panes.Count > 1 && space is not null;
        double localX = point.X - rect.Left;
        double localY = point.Y - rect.Top;
        double scale = camera.EffectiveScale;
        (double worldX, double worldY) = camera.ScreenToWorld(localX, localY);
        (ScoutingLayerChipViewModel, ScoutingMapMark)? best = null;
        // A faded layer's mark loses to any of the inspected layer's, then the nearer mark wins.
        bool bestFaded = true;
        double bestSquared = double.MaxValue;
        foreach (ScoutingLayerChipViewModel chip in vm.Layers.All.Where(c => c.IsMap && c.Marks.Visible))
        {
            bool faded = chip.Marks.Fade < 1f;
            if (faded && !bestFaded)
            {
                continue;
            }

            foreach (ScoutingMapMark mark in chip.Marks.Marks)
            {
                if (mark.Info is null || mark.Shape == ScoutingMarkShape.Link)
                {
                    continue;
                }

                double reach = mark.Size + HitSlop;
                // A world-space box around the pointer rules most marks out before projecting them.
                if (scale > 0 && (Math.Abs(mark.X - worldX) * scale > reach + 1
                                  || Math.Abs(mark.Y - worldY) * scale > reach + 1))
                {
                    continue;
                }

                if (filter && space!.LevelIndexFor(mark.Z) != pane.LevelIndex)
                {
                    continue;
                }

                (double x, double y) = camera.WorldToScreen(mark.X, mark.Y);
                double dx = x - localX;
                double dy = y - localY;
                double squared = dx * dx + dy * dy;
                if (squared <= reach * reach
                    && (faded == bestFaded ? squared < bestSquared : !faded))
                {
                    bestFaded = faded;
                    bestSquared = squared;
                    best = (chip, mark);
                }
            }
        }

        _hitKey = key;
        _hitResult = best;
        return best;
    }

    private void OnPointerMovedOver(object? sender, PointerEventArgs e)
    {
        Point at = e.GetPosition(Map);
        if (_pressedAt is { } from && Distance(from, at) > ClickSlop)
        {
            // A drag pans the map; the card would only trail behind it.
            _pressedAt = null;
        }

        Hover(_pressedAt is null && !e.GetCurrentPoint(Map).Properties.IsLeftButtonPressed ? MarkAt(at)?.Mark : null,
            e.GetPosition(_hoverLayer));
    }

    private void OnPointerPressedOver(object? sender, PointerPressedEventArgs e) =>
        _pressedAt = e.GetCurrentPoint(Map).Properties.IsLeftButtonPressed ? e.GetPosition(Map) : null;

    private void OnPointerReleasedOver(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedAt is not { } from || _vm is not { } vm)
        {
            return;
        }

        _pressedAt = null;
        Point at = e.GetPosition(Map);
        if (Distance(from, at) > ClickSlop)
        {
            return;
        }

        Click(at);
    }

    /// <summary>Clicks the stage at a point: pins the mark there, or unpins with no mark there.</summary>
    /// <param name="point">The point, in the map view's coordinates.</param>
    public void Click(Point point)
    {
        if (_vm is not { } vm)
        {
            return;
        }

        if (MarkAt(point) is { } hit)
        {
            vm.Layers.Select(hit.Chip, hit.Mark);
        }
        else
        {
            vm.Layers.ClearSelection();
        }
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    // Shows the hover card for a mark by the pointer, kept inside the stage; null hides it.
    private void Hover(ScoutingMapMark? mark, Point at)
    {
        SetHandCursor(mark is not null);
        if (mark?.Info is not { } info)
        {
            _hovered = null;
            _hover.IsVisible = false;
            return;
        }

        if (!ReferenceEquals(mark, _hovered))
        {
            _hovered = mark;
            _hoverLines.Children.Clear();
            _hoverLines.Children.Add(Line(info.Title, 12, FontWeight.SemiBold, "TextBright"));
            foreach (ScoutingInspectorRow row in info.Rows.Take(ScoutingMarkInfo.HoverRows))
            {
                string text = row.Value.Length > 0 ? $"{row.Label}: {row.Value}"
                    : row.Note is { Length: > 0 } note ? $"{row.Label}: {note}"
                    : row.Label;
                _hoverLines.Children.Add(Line(ScoutingNames.Humanise(text), 11, FontWeight.Normal, "TextValue"));
            }

            string hint = info.Hint.Length > 0 ? "Click to " + info.Hint : "Click to pin";
            _hoverLines.Children.Add(Line(hint, 10, FontWeight.Normal, "TextMid"));
        }

        _hover.IsVisible = true;
        _hover.Measure(Size.Infinity);
        Size size = _hover.DesiredSize;
        double x = at.X + 16 + size.Width > Bounds.Width ? at.X - 12 - size.Width : at.X + 16;
        double y = Math.Clamp(at.Y + 14, 4, Math.Max(4, Bounds.Height - size.Height - 4));
        Canvas.SetLeft(_hover, Math.Max(4, x));
        Canvas.SetTop(_hover, y);
    }

    // Shows the hand over a mark and puts back the cursor that was there, only when hovering starts or stops,
    // and leaves alone a cursor the map set meanwhile (a pan's).
    private void SetHandCursor(bool show)
    {
        if (show == _handShown)
        {
            return;
        }

        _handShown = show;
        if (show)
        {
            _cursorBeforeHover = Map.Cursor;
            Map.Cursor = _hand ??= new Cursor(StandardCursorType.Hand);
        }
        else if (ReferenceEquals(Map.Cursor, _hand))
        {
            Map.Cursor = _cursorBeforeHover;
        }
    }

    private static TextBlock Line(string text, double size, FontWeight weight, string brush)
    {
        TextBlock line = new() { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable(brush));
        return line;
    }

    private void UpdateNote()
    {
        string? map = _vm?.MapName;
        _note.Text = map is null ? "select a team with demos" : $"no map art for {ScoutingNames.Map(map)} on this host";
        _note.IsVisible = !Map.HasMap;
    }

    /// <summary>Draws nothing; tells the stage a frame was composed, once the panes exist to be framed.</summary>
    private sealed class FitProbe(Action composed) : ISceneLayer
    {
        public string Id => "scouting.fit-probe";

        public LayerSlot Slot => LayerSlot.Underlay;

        public int Order => -1000;

        public LayerCacheHint Cache => LayerCacheHint.Static;

        public bool IsEnabled { get; set; } = true;

        public int ContentVersion => 0;

        public bool Advance(in SceneTime time, Scene2DFrame frame)
        {
            composed();
            return false;
        }

        public void Render(SKCanvas canvas, SceneRenderContext ctx)
        {
        }

        public void Dispose()
        {
        }
    }
}
