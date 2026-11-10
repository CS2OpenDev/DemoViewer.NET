#region

using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;

/// <summary>
///     The Scouting Report as a dashboard ("v2 · A · Dashboard (tabs)"): the header with its stat tiles and the
///     sample by date, the roster, the map tabs and the side, the one-pager, the map record, then one card per
///     finding for the side in view, the AWP card, and the map-wide cards.
///     <para>
///         <b>It composes, it does not fork.</b> Every number comes from <see cref="Report" />, a Scouting Report of
///         the tab's own over the same services as the Layers tab: its loads, its layer registry (which builds every
///         layer of the side in view), its players and its editor. A card is a layer's read and inspector rows (or a
///         share of them), so a number here is the number there and opens the same rounds.
///     </para>
///     <para>
///         <b>Shared and not.</b> The team, map, side and buy are this tab's own. Stars, edits, notes, ranks and
///         vetoes live in the stores both tabs write, so a line starred in one shows starred in the other.
///     </para>
/// </summary>
public sealed partial class ScoutingDashboardTabViewModel : ExtensionViewModel, IWorkspaceTabViewModel, IDisposable
{
    /// <summary>The window the Form tile compares: the last ten demos against the ten before.</summary>
    public const int FormWindow = 10;

    private readonly Func<byte[], Bitmap?> _decode;
    private readonly Action<Action> _post;
    private readonly Func<string, IReadOnlyList<ScoutingMapMark>, byte[]?> _render;
    private readonly List<Task> _renders = [];
    private bool _disposed;
    private int _factsGeneration;

    [ObservableProperty]
    private string _pistolsLine = "—";

    /// <param name="report">The tab's own Scouting Report, built like the Layers tab's.</param>
    /// <param name="post">UI-thread marshal for a map picture drawn off it; the dispatcher when null.</param>
    /// <param name="renderMap">Draws (map, marks) as a PNG on the dark stage, or null; the app's renderer when null.</param>
    /// <param name="decode">Decodes a PNG for the view; Avalonia's bitmap when null.</param>
    public ScoutingDashboardTabViewModel(ScoutingReportTabViewModel report, Action<Action>? post = null,
        Func<string, IReadOnlyList<ScoutingMapMark>, byte[]?>? renderMap = null, Func<byte[], Bitmap?>? decode = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _render = renderMap ?? RenderOnStage;
        _decode = decode ?? Decode;
        Report.WindowSize = FormWindow;

        CtCards = ScoutingDashboardCards.Ct();
        TCards = ScoutingDashboardCards.T();
        AwpCard = ScoutingDashboardCards.Awp();
        MapWideCards = ScoutingDashboardCards.MapWide();

        foreach (ScoutingLayerChipViewModel chip in Report.Layers.All)
        {
            chip.PropertyChanged += OnChipChanged;
        }

        Report.PropertyChanged += OnReportChanged;
        Report.Editor.StarsChanged += OnStarsChanged;
        Report.Vetoes.CollectionChanged += OnVetoesChanged;
        RefreshAll();
        WatchFacts();
    }

    /// <summary>The tab's own report: the header, the players, the map tabs, the layers and the editor.</summary>
    public ScoutingReportTabViewModel Report { get; }

    public IReadOnlyList<ScoutingDashboardCardViewModel> CtCards { get; }

    public IReadOnlyList<ScoutingDashboardCardViewModel> TCards { get; }

    /// <summary>The cards of the side in view.</summary>
    public IReadOnlyList<ScoutingDashboardCardViewModel> SideCards => Report.IsCt ? CtCards : TCards;

    /// <summary>The AWP card: both sides, its own full-width card under the side's.</summary>
    public ScoutingDashboardCardViewModel AwpCard { get; }

    /// <summary>Economy, opening impact, by score and timeouts.</summary>
    public IReadOnlyList<ScoutingDashboardCardViewModel> MapWideCards { get; }

    /// <summary>Every card of the side in view, the AWP card and the map-wide ones.</summary>
    public IEnumerable<ScoutingDashboardCardViewModel> VisibleCards => [.. SideCards, AwpCard, .. MapWideCards];

    /// <summary>Every map picture asked for so far; tests await it, then drain the post.</summary>
    internal Task RendersIdle
    {
        get
        {
            _renders.RemoveAll(t => t.IsCompleted);
            return Task.WhenAll(_renders);
        }
    }

    private string MapLabel => Report.SelectedMap?.MapLabel ?? "";

    // ---- Header tiles ----

    public string DecidersValue => Report.HasDeciderData ? Report.DeciderLine.Replace("Deciders: ", "", StringComparison.Ordinal) : "—";

    public string FormTitle => $"Form, last {Report.WindowSize}";

    public string FormValue => Report.PeriodDiffRecent?.RecordLabel ?? "—";

    public string FormNote => Report.PeriodDiffPrevious is { } previous
        ? $"previous {Report.WindowSize}: {previous.RecordLabel}"
        : "";

    public string HeadToHeadValue => Report.HasHeadToHead ? Report.HeadToHeadLine : "no meeting with your team yet";

    // ---- The map in view ----

    /// <summary>"CT side · Nuke".</summary>
    public string SideHeading => MapLabel.Length == 0 ? "" : $"{Report.Side.Label()} side · {MapLabel}";

    /// <summary>"Map-wide · Nuke".</summary>
    public string MapWideHeading => MapLabel.Length == 0 ? "Map-wide" : $"Map-wide · {MapLabel}";

    /// <summary>"Nuke record".</summary>
    public string RecordTitle => MapLabel.Length == 0 ? "Record" : $"{MapLabel} record";

    /// <summary>"picked 3 · banned 0", from the veto history you enter by hand.</summary>
    public string VetoLine
    {
        get
        {
            if (Report.SelectedMap is not { } map)
            {
                return "";
            }

            List<VetoRowViewModel> rows =
            [
                .. Report.Vetoes.Where(v => SameMap(v.Map, map.Map) || SameMap(v.Map, map.MapLabel))
            ];
            return rows.Count == 0
                ? "none entered"
                : $"picked {rows.Count(r => r.ActionLabel == "Pick")} · banned {rows.Count(r => r.ActionLabel == "Ban")}";
        }
    }

    /// <summary>The roster's stand-in line: what is known, and that per-player stand-in rows are not derived.</summary>
    public string StandInNote
    {
        get
        {
            string known = Report.PeriodDiffRecent is { HasStandIn: true } recent
                ? $" In the last {Report.WindowSize} demos: {recent.StandInLabel}."
                : "";
            return "Stand-ins are not listed per player yet: Team Identity counts the demos played with one, not who."
                   + known;
        }
    }

    // ---- Lifecycle ----

    /// <inheritdoc />
    public void OnActivated(IModuleContext context) => Report.OnActivated(context);

    /// <inheritdoc />
    public void OnDeactivated() => Report.OnDeactivated();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ScoutingLayerChipViewModel chip in Report.Layers.All)
        {
            chip.PropertyChanged -= OnChipChanged;
        }

        Report.PropertyChanged -= OnReportChanged;
        Report.Editor.StarsChanged -= OnStarsChanged;
        Report.Vetoes.CollectionChanged -= OnVetoesChanged;
        Report.Dispose();
    }

    private static bool SameMap(string entered, string map) =>
        string.Equals(entered.Trim(), map, StringComparison.OrdinalIgnoreCase)
        || string.Equals("de_" + entered.Trim(), map, StringComparison.OrdinalIgnoreCase);

    // A layer landed (or went back to building) for the scope in view: the cards that read it follow.
    private void OnChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(ScoutingLayerChipViewModel.Status)
                      || sender is not ScoutingLayerChipViewModel chip)
        {
            return;
        }

        foreach (ScoutingDashboardCardViewModel card in VisibleCards.Where(c => c.UsesLayer(chip.Id)))
        {
            Refresh(card);
        }
    }

    private void OnReportChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(ScoutingReportTabViewModel.Side):
                OnPropertyChanged(nameof(SideCards));
                OnPropertyChanged(nameof(SideHeading));
                RefreshAll();
                break;
            case nameof(ScoutingReportTabViewModel.SelectedMap):
                OnPropertyChanged(nameof(SideHeading));
                OnPropertyChanged(nameof(MapWideHeading));
                OnPropertyChanged(nameof(RecordTitle));
                OnPropertyChanged(nameof(VetoLine));
                break;
            case nameof(ScoutingReportTabViewModel.RecordLine):
                // Raised with the header on every projection of the team: its loads may be new.
                OnPropertyChanged(nameof(DecidersValue));
                OnPropertyChanged(nameof(HeadToHeadValue));
                WatchFacts();
                break;
            case nameof(ScoutingReportTabViewModel.PeriodDiffRecent):
            case nameof(ScoutingReportTabViewModel.PeriodDiffPrevious):
            case nameof(ScoutingReportTabViewModel.WindowSize):
                OnPropertyChanged(nameof(FormTitle));
                OnPropertyChanged(nameof(FormValue));
                OnPropertyChanged(nameof(FormNote));
                OnPropertyChanged(nameof(StandInNote));
                break;
        }
    }

    private void OnVetoesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(VetoLine));

    private void OnStarsChanged()
    {
        foreach (ScoutingDashboardCardViewModel card in VisibleCards)
        {
            card.RefreshStars();
        }
    }

    private void RefreshAll()
    {
        foreach (ScoutingDashboardCardViewModel card in VisibleCards)
        {
            Refresh(card);
        }
    }

    private void Refresh(ScoutingDashboardCardViewModel card)
    {
        card.Refresh(Report.Layers, Report, Report.Side, MapLabel);
        UpdateMap(card);
    }

    // The card's picture: the radar with its layer's marks, drawn off the UI thread once per build.
    private void UpdateMap(ScoutingDashboardCardViewModel card)
    {
        if (card.MapLayerId is not { } id)
        {
            return;
        }

        string map = Report.MapName ?? "";
        if (Report.Layers.Find(id) is not { } chip || !chip.Layer.AppliesTo(Report.Side) || map.Length == 0)
        {
            ClearMap(card, "");
            return;
        }

        card.MapTag = $"{MapLabel} · {chip.Name}";
        if (chip.IsBuilding)
        {
            ClearMap(card, "Reading the rounds…");
            return;
        }

        IReadOnlyList<ScoutingMapMark> marks = chip.Result.Marks;
        if (chip.Status.State != ScoutingLayerState.Ready || marks.Count == 0)
        {
            ClearMap(card, chip.Status.State == ScoutingLayerState.Ready ? "Nothing to draw on the map for this scope." : "");
            return;
        }

        if (ReferenceEquals(card.RenderedMarks, marks))
        {
            return;
        }

        card.RenderedMarks = marks;
        card.MapImage = null;
        card.MapNote = "Drawing the map…";
        int generation = ++card.RenderGeneration;
        Task render = Task.Run(() =>
        {
            byte[]? png = _render(map, marks);
            return png is null ? null : _decode(png);
        }).ContinueWith(t => _post(() =>
        {
            Bitmap? bitmap = t.IsCompletedSuccessfully ? t.Result : null;
            if (_disposed || generation != card.RenderGeneration)
            {
                bitmap?.Dispose();
                return;
            }

            card.MapImage = bitmap;
            card.MapNote = bitmap is null ? "No map picture on this host: the radar art ships with the desktop app." : "";
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _renders.Add(render);
    }

    private static void ClearMap(ScoutingDashboardCardViewModel card, string note)
    {
        card.RenderGeneration++;
        card.RenderedMarks = null;
        card.MapImage = null;
        card.MapNote = note;
    }

    // The header's Pistols tile: every map's pistols, once the team's Round Facts are counted.
    private void WatchFacts()
    {
        int generation = ++_factsGeneration;
        PistolsLine = "—";
        if (Report.Data is not { } data)
        {
            return;
        }

        data.Facts.ContinueWith(t => _post(() =>
        {
            if (_disposed || generation != _factsGeneration)
            {
                return;
            }

            TeamMapFactsSet? facts = t.IsCompletedSuccessfully ? t.Result : null;
            int played = facts?.Maps.Sum(m => m.PistolsPlayed) ?? 0;
            int won = facts?.Maps.Sum(m => m.PistolsWon) ?? 0;
            PistolsLine = played == 0 ? "—" : $"{won} of {played}";
        }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static byte[]? RenderOnStage(string map, IReadOnlyList<ScoutingMapMark> marks)
    {
        using ScoutingStageRenderer renderer = new();
        try
        {
            return renderer.Render(map, marks, paper: false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Bitmap? Decode(byte[] png)
    {
        try
        {
            using MemoryStream stream = new(png);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}
