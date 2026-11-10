#region

using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;

/// <summary>
///     One layer's share of a card: the layer, which of its sections and reads the card shows, and whether its
///     strip chart draws here. A layer the board splits (Economy into its pistol card and the economy card) is two
///     parts on two cards.
/// </summary>
/// <param name="LayerId">The layer id.</param>
/// <param name="Sections">Keeps a section by its raw title; every section when null.</param>
/// <param name="Reads">Keeps a read; every read when null.</param>
/// <param name="Chart">Draws the layer's strip chart on this card.</param>
public sealed record ScoutingDashboardPart(
    string LayerId,
    Func<string, bool>? Sections = null,
    Func<ScoutingRead, bool>? Reads = null,
    bool Chart = true)
{
    /// <summary>Keeps no read: the card leads with another part's, or says it has none.</summary>
    public static Func<ScoutingRead, bool> NoReads { get; } = _ => false;
}

/// <summary>
///     One Dashboard card: a title, the finding it leads with (starrable), its numbers as the Layers tab's
///     inspector rows (each opening its rounds in Review), the layer's strip chart, and for a map layer a picture
///     of its marks on the radar. Everything is read from the layers the tab's report built for the scope in view;
///     the card owns no analysis.
/// </summary>
public sealed partial class ScoutingDashboardCardViewModel : ObservableObject
{
    private readonly Func<ScoutingSide, string, string> _titleFor;

    [ObservableProperty]
    private string _buildCode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChart))]
    private ScoutingMiniChart? _chart;

    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMapImage))]
    private Bitmap? _mapImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMapNote))]
    private string _mapNote = "";

    [ObservableProperty]
    private string _mapTag = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoFinding))]
    private string _noFindingLine = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _title = "";

    /// <param name="key">"ct.setups": unique on the page, for tests and the view.</param>
    /// <param name="title">The title for a side and a map label.</param>
    /// <param name="parts">The layers it reads, in order; the first one's read leads.</param>
    /// <param name="mapLayerId">The layer whose marks the card draws on the radar; none when null.</param>
    /// <param name="isWide">Spans every column of the grid.</param>
    public ScoutingDashboardCardViewModel(string key, Func<ScoutingSide, string, string> title,
        IReadOnlyList<ScoutingDashboardPart> parts, string? mapLayerId = null, bool isWide = false)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(parts);
        Key = key;
        _titleFor = title;
        Parts = parts;
        MapLayerId = mapLayerId;
        IsWide = isWide;
    }

    public string Key { get; }

    public IReadOnlyList<ScoutingDashboardPart> Parts { get; }

    /// <summary>The layer whose marks the card draws, or null.</summary>
    public string? MapLayerId { get; }

    public bool ShowsMap => MapLayerId is not null;

    public bool IsWide { get; }

    /// <summary>The read sentences the card leads with.</summary>
    public ObservableCollection<ScoutingInspectorRowViewModel> Reads { get; } = [];

    public ObservableCollection<ScoutingInspectorSectionViewModel> Sections { get; } = [];

    /// <summary>The AWP card's trail windows; empty on every other card.</summary>
    public ObservableCollection<ScoutingWindowPillViewModel> Windows { get; } = [];

    public bool HasWindows => Windows.Count > 0;

    public bool HasChart => Chart is not null;

    public bool HasMapImage => MapImage is not null;

    public bool HasMapNote => MapNote.Length > 0;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    public bool HasNoFinding => NoFindingLine.Length > 0;

    /// <summary>The marks the picture was last asked for, so an unchanged build is not drawn again.</summary>
    internal IReadOnlyList<ScoutingMapMark>? RenderedMarks { get; set; }

    internal int RenderGeneration { get; set; }

    /// <summary>Every starrable line shown, reads first.</summary>
    public IEnumerable<ScoutingInspectorRowViewModel> AllRows => Reads.Concat(Sections.SelectMany(s => s.Rows));

    /// <summary>Whether the card reads a layer.</summary>
    /// <param name="layerId">The layer id.</param>
    public bool UsesLayer(string layerId) =>
        Parts.Any(p => p.LayerId == layerId) || string.Equals(MapLayerId, layerId, StringComparison.Ordinal);

    /// <summary>Re-reads the card from the layers' current builds.</summary>
    /// <param name="registry">The report's layers, built for the scope in view.</param>
    /// <param name="host">Stars, Review and 2D for the rows.</param>
    /// <param name="side">The side in view.</param>
    /// <param name="mapLabel">"Nuke", for the title.</param>
    internal void Refresh(ScoutingLayerRegistry registry, IScoutingRowHost host, ScoutingSide side, string mapLabel)
    {
        Title = _titleFor(side, mapLabel);
        List<ScoutingInspectorRowViewModel> reads = [];
        List<ScoutingInspectorSectionViewModel> sections = [];
        ScoutingMiniChart? chart = null;
        bool building = false, ready = false;
        string status = "";
        List<string> codes = [];
        foreach (ScoutingDashboardPart part in Parts)
        {
            if (registry.Find(part.LayerId) is not { } chip || !chip.Layer.AppliesTo(side))
            {
                continue;
            }

            codes.Add(chip.Status is { State: ScoutingLayerState.Placeholder, GapId: { } gap }
                ? $"{chip.Layer.BuildCode} · {gap}"
                : chip.Layer.BuildCode);
            switch (chip.Status.State)
            {
                case ScoutingLayerState.Building:
                    building = true;
                    continue;
                case ScoutingLayerState.Ready when chip.Context is { } context:
                    ready = true;
                    ScoutingLayerResult shown = chip.Result with
                    {
                        Reads = [.. chip.Result.Reads.Where(r => part.Reads?.Invoke(r) ?? true)],
                        Sections = [.. chip.Result.Sections.Where(s => part.Sections?.Invoke(s.Title) ?? true)]
                    };
                    (List<ScoutingInspectorRowViewModel> r, List<ScoutingInspectorSectionViewModel> s) =
                        ScoutingLayerRegistry.Lines(host, chip, context, shown);
                    reads.AddRange(r);
                    sections.AddRange(s.Where(x => x.Rows.Count > 0));
                    if (part.Chart && chart is null)
                    {
                        chart = chip.Result.MiniChart;
                    }

                    break;
                default:
                    if (status.Length == 0)
                    {
                        status = ScoutingNames.Humanise(chip.Status.Display);
                    }

                    break;
            }
        }

        BuildCode = string.Join(" + ", codes.Distinct(StringComparer.Ordinal));
        IsBuilding = building && !ready;
        bool any = reads.Count > 0 || sections.Count > 0 || chart is not null;
        StatusMessage = IsBuilding || any
            ? ""
            : status.Length > 0
                ? status
                : ready
                    ? "Nothing counted here for this map and side."
                    : "";
        NoFindingLine = any && reads.Count == 0
            ? "No one-line finding for this card yet; the numbers below are the read."
            : "";
        Chart = chart;
        Replace(Reads, reads);
        Replace(Sections, sections);
        ShowWindows(registry);
    }

    private void ShowWindows(ScoutingLayerRegistry registry)
    {
        Windows.Clear();
        foreach (ScoutingDashboardPart part in Parts)
        {
            if (registry.Find(part.LayerId) is { Layer: IScoutingWindowedLayer windowed } chip)
            {
                foreach (ScoutingWindow window in windowed.Windows)
                {
                    Windows.Add(new ScoutingWindowPillViewModel(window, window.Key == windowed.Window,
                        key => registry.SelectWindow(chip, key)));
                }
            }
        }

        OnPropertyChanged(nameof(HasWindows));
    }

    /// <summary>Re-reads every star shown; the one-pager changed somewhere.</summary>
    internal void RefreshStars()
    {
        foreach (ScoutingInspectorRowViewModel row in AllRows)
        {
            row.RefreshStar();
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, List<T> items)
    {
        target.Clear();
        foreach (T item in items)
        {
            target.Add(item);
        }
    }
}
