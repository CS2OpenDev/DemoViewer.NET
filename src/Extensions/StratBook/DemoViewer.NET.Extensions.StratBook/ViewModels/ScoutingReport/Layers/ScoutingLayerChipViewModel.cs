#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     One layer as the stage shows it: a map chip (checkbox draws, name inspects, count) or a strip mini (its
///     summary rows and Inspect). Holds the layer's last build for the scope in view and the marks it draws.
/// </summary>
public sealed partial class ScoutingLayerChipViewModel : ObservableObject
{
    private readonly ScoutingLayerRegistry _registry;
    private bool _applying;

    [ObservableProperty]
    private string _countLabel = "";

    [ObservableProperty]
    private bool _isFocused;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _title = "";

    /// <param name="registry">The registry that owns it.</param>
    /// <param name="layer">The layer.</param>
    public ScoutingLayerChipViewModel(ScoutingLayerRegistry registry, IScoutingLayer layer)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(layer);
        _registry = registry;
        Layer = layer;
        _name = layer.NameFor(ScoutingSide.Ct);
        _title = layer.TitleFor(ScoutingSide.Ct);
    }

    public IScoutingLayer Layer { get; }

    public string Id => Layer.Id;

    public bool IsMap => Layer.Placement == ScoutingLayerPlacement.Map;

    public ScoutingSwatch Swatch => Layer.Swatch;

    /// <summary>What the stage draws for this layer.</summary>
    public ScoutingMarksDocument Marks { get; } = new();

    /// <summary>The last build for the scope in view; a Building status until it lands.</summary>
    public ScoutingLayerResult Result { get; private set; } = new() { Status = ScoutingLayerStatus.Building };

    /// <summary>The scope the result was built for.</summary>
    public ScoutingLayerContext? Context { get; private set; }

    public ScoutingLayerStatus Status => Result.Status;

    /// <summary>Still building for the scope in view: the strip mini draws skeleton bars in place of its lines.</summary>
    public bool IsBuilding => Result.Status.State == ScoutingLayerState.Building;

    /// <summary>A strip mini that shows its text lines: built, and with no chart.</summary>
    public bool ShowsMiniLines => !IsBuilding && !HasMiniChart;

    /// <summary>The strip mini's lines: its summary rows, or what it waits on.</summary>
    public IReadOnlyList<string> MiniLines
    {
        get
        {
            if (Result.Status.State != ScoutingLayerState.Ready)
            {
                return [Result.Status.Display];
            }

            IEnumerable<ScoutingInspectorRow> rows = Result.Mini.Count > 0
                ? Result.Mini
                : Result.Sections.Count > 0 ? Result.Sections[0].Rows.Take(3) : [];
            List<string> lines = [.. rows.Select(r => r.Value.Length > 0 ? $"{r.Label}: {r.Value}" : r.Label)];
            return lines.Count > 0 ? lines : [Result.Reads.Count > 0 ? Result.Reads[0].Text : "nothing counted yet"];
        }
    }

    /// <summary>The strip mini's chart when the build is ready and drew one; null shows <see cref="MiniLines" />.</summary>
    public ScoutingMiniChart? MiniChart => Result.Status.State == ScoutingLayerState.Ready ? Result.MiniChart : null;

    public bool HasMiniChart => MiniChart is not null;

    /// <summary>
    ///     Always false: no strip mini has a second button any more (Timeouts is its own mini). Kept, with
    ///     <see cref="CompanionLabel" /> and <see cref="InspectCompanionCommand" />, only until the views drop their
    ///     bindings to them.
    /// </summary>
    public bool HasCompanion { get; }

    /// <summary>The checkbox's accessible name.</summary>
    public string DrawLabel => $"Draw {Name} on the map";

    /// <summary>The name button's accessible name.</summary>
    public string InspectLabel => $"Inspect {Name}";

    /// <summary>Always empty; see <see cref="HasCompanion" />.</summary>
    public string CompanionLabel { get; } = "";

    /// <summary>Does nothing; see <see cref="HasCompanion" />.</summary>
    public IRelayCommand InspectCompanionCommand { get; } = new RelayCommand(static () => { });

    partial void OnIsOnChanged(bool value)
    {
        if (!_applying)
        {
            _registry.Toggle(this, value);
        }
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DrawLabel));
        OnPropertyChanged(nameof(InspectLabel));
    }

    /// <summary>Focuses the layer in the inspector, turning it on.</summary>
    [RelayCommand]
    private void Inspect() => _registry.Focus(this);

    /// <summary>Sets the checkbox without asking the registry again.</summary>
    /// <param name="on">Drawn.</param>
    internal void SetOn(bool on)
    {
        _applying = true;
        IsOn = on;
        _applying = false;
    }

    /// <summary>Names the chip for a side.</summary>
    /// <param name="side">The side.</param>
    internal void SetSide(ScoutingSide side)
    {
        Name = Layer.NameFor(side);
        Title = Layer.TitleFor(side);
    }

    /// <summary>Takes a build.</summary>
    /// <param name="result">The result.</param>
    /// <param name="context">Its scope.</param>
    internal void Apply(ScoutingLayerResult result, ScoutingLayerContext? context)
    {
        Result = result;
        Context = context;
        CountLabel = result.Status.State switch
        {
            ScoutingLayerState.Building => "…",
            ScoutingLayerState.Placeholder => "soon",
            ScoutingLayerState.Empty => "none",
            _ => result.CountLabel
        };
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsBuilding));
        OnPropertyChanged(nameof(ShowsMiniLines));
        OnPropertyChanged(nameof(MiniLines));
        OnPropertyChanged(nameof(MiniChart));
        OnPropertyChanged(nameof(HasMiniChart));
    }
}
