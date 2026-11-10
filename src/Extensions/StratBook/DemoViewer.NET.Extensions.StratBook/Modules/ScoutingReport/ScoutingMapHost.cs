#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Playback2D;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;

/// <summary>
///     The Scouting Report's map stage: a <see cref="MapView" /> of the map in view with one
///     <see cref="ScoutingMarksLayer" /> per map layer over the radar. Each layer draws its own
///     <see cref="ScoutingMarksDocument" />, which already says whether it shows and at what fade, so the host only
///     wires them once and redraws when one changes. Where the host has no art for the map (a test, an unknown
///     map) a line says so instead of an empty box.
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

    private ScoutingReportTabViewModel? _vm;

    /// <summary>Creates the stage.</summary>
    public ScoutingMapHost()
    {
        ClipToBounds = true;
        _note.Bind(TextBlock.ForegroundProperty, _note.GetResourceObservable("TextMid"));
        Child = new Grid { Children = { Map, _note } };
        Map.MapBound += (_, _) =>
        {
            UpdateNote();
            InvalidateMeasure();
        };
    }

    /// <summary>The map view the stage is built on.</summary>
    public MapView Map { get; } = new();

    /// <summary>The height a stage takes with no map bound: room for the line that says why.</summary>
    public const double EmptyHeight = 360;

    /// <summary>
    ///     The height of one floor's band for a stage <paramref name="width" /> wide. The map view stacks a
    ///     multi-floor map's floors as bands of equal height, and a radar is square, so a floor draws as large
    ///     as its band is tall: the stage grows by a band per floor instead of squeezing two floors into one
    ///     map's height. A single floor stays a bit shorter than the stage is wide, two floors a bit shorter
    ///     still so the page does not turn into one long map.
    /// </summary>
    /// <param name="width">The stage's width.</param>
    /// <param name="floors">The map's floors.</param>
    public static double BandHeight(double width, int floors) =>
        Math.Clamp(width * 0.66, 420, floors > 1 ? 500 : 620);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        _measuredFloors = Floors();
        double height = _measuredFloors == 0 ? EmptyHeight : _measuredFloors * BandHeight(width, _measuredFloors);
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

    private static int FloorsOf(string? map)
    {
        if (string.IsNullOrEmpty(map))
        {
            return 0;
        }

        try
        {
            using IMapAssetLease? lease = MapAssets.Acquire(map);
            return lease?.Floors?.Count ?? 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

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

        if (_vm is not null)
        {
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

        vm.PropertyChanged += OnVmChanged;
        _assetFloors = FloorsOf(vm.MapName);
        Map.MapName = vm.MapName;
        UpdateNote();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScoutingReportTabViewModel.MapName) && _vm is { } vm)
        {
            _assetFloors = FloorsOf(vm.MapName);
            Map.MapName = vm.MapName;
            UpdateNote();
            InvalidateMeasure();
        }
    }

    private void UpdateNote()
    {
        string? map = _vm?.MapName;
        _note.Text = map is null ? "select a team with demos" : $"no map art for {map} on this host";
        _note.IsVisible = !Map.HasMap;
    }
}
