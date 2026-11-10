#region

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

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
        Map.MapBound += (_, _) => UpdateNote();
    }

    /// <summary>The map view the stage is built on.</summary>
    public MapView Map { get; } = new();

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
        Map.MapName = vm.MapName;
        UpdateNote();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScoutingReportTabViewModel.MapName) && _vm is { } vm)
        {
            Map.MapName = vm.MapName;
            UpdateNote();
        }
    }

    private void UpdateNote()
    {
        string? map = _vm?.MapName;
        _note.Text = map is null ? "select a team with demos" : $"no map art for {map} on this host";
        _note.IsVisible = !Map.HasMap;
    }
}
