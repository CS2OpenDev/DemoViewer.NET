#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>What an inspector row asks of the tab: its star, its rounds in Review, its moment in 2D.</summary>
public interface IScoutingRowHost
{
    bool IsStarred(string key);

    void SetStarred(string key, bool starred);

    void Open(ScoutingInspectorRowViewModel row);

    void Play(ScoutingInspectorRowViewModel row);
}

/// <summary>
///     One starrable inspector line: a layer's read sentence, or a row with its number. The number is a button
///     that opens its rounds in Review; the star files the line under its scope key.
/// </summary>
public sealed partial class ScoutingInspectorRowViewModel : ObservableObject
{
    /// <summary>The widest a bar draws, in pixels.</summary>
    public const double BarMaxWidth = 200;

    private readonly IScoutingRowHost _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StarLabel))]
    private bool _isStarred;

    /// <summary>A read sentence.</summary>
    /// <param name="host">The tab.</param>
    /// <param name="read">The sentence.</param>
    /// <param name="key">Its scope key.</param>
    /// <param name="where">The layer and scope it sits in, for the star's accessible name.</param>
    public ScoutingInspectorRowViewModel(IScoutingRowHost host, ScoutingRead read, string key, string where = "")
    {
        ArgumentNullException.ThrowIfNull(read);
        _host = host;
        Key = key;
        Where = where;
        Label = read.Text;
        IsRead = true;
        Read = read;
        _isStarred = host.IsStarred(key);
    }

    /// <summary>A row with its number.</summary>
    /// <param name="host">The tab.</param>
    /// <param name="row">The row.</param>
    /// <param name="key">Its scope key.</param>
    /// <param name="where">The layer, section and scope it sits in, for the star's accessible name.</param>
    public ScoutingInspectorRowViewModel(IScoutingRowHost host, ScoutingInspectorRow row, string key, string where = "")
    {
        ArgumentNullException.ThrowIfNull(row);
        _host = host;
        Key = key;
        Where = where;
        Row = row;
        Label = row.Label;
        _isStarred = host.IsStarred(key);
    }

    /// <summary>The scope key the star files.</summary>
    public string Key { get; }

    /// <summary>"Plants and post-plant · de_nuke T", the scope a screen reader hears with the star.</summary>
    public string Where { get; }

    /// <summary>The row, or null for a read.</summary>
    public ScoutingInspectorRow? Row { get; }

    /// <summary>The read, or null for a row.</summary>
    public ScoutingRead? Read { get; }

    public bool IsRead { get; }

    public string Label { get; }

    public string Value => Row?.Value ?? "";

    public bool HasValue => Value.Length > 0;

    /// <summary>A number with rounds behind it: drawn as a button.</summary>
    public bool CanOpen => Row?.Rounds.Count > 0;

    /// <summary>A number with nothing to open: drawn as text.</summary>
    public bool ShowsPlainValue => HasValue && !CanOpen;

    public string OpenToolTip => Row is { Rounds.Count: var n } && n > 0
        ? $"Open {ScoutingLayerBase.Plural(n, "round")} in Review"
        : "";

    public bool HasBar => Row?.BarPercent is not null;

    public double BarWidth => Math.Max(2, BarMaxWidth * Math.Clamp(Row?.BarPercent ?? 0, 0, 100) / 100);

    /// <summary>The bar's and the swatch's colour; the accent when the row has none.</summary>
    public ScoutingColor Color => Row?.Swatch ?? ScoutingColor.Neutral;

    public bool HasSwatch => Row?.Swatch is not null;

    public string Badge => Row?.Badge ?? "";

    public bool HasBadge => Badge.Length > 0;

    public string Note => Row?.Note ?? "";

    public bool HasNote => Note.Length > 0;

    public bool IsPlaceholder => Row?.IsPlaceholder == true;

    public bool CanPlay => Row?.Play is not null;

    /// <summary>
    ///     The star's accessible name: the line and the scope it is filed under, so no two stars on screen share
    ///     one ("Star "plant A" · Plants and post-plant · de_nuke T for the one-pager").
    /// </summary>
    public string StarLabel
    {
        get
        {
            string what = Where.Length > 0 ? $"\"{Label}\" · {Where}" : $"\"{Label}\"";
            return IsStarred ? $"Unstar {what} from the one-pager" : $"Star {what} for the one-pager";
        }
    }

    /// <summary>Re-reads the star from the store; the one-pager changed somewhere else.</summary>
    public void RefreshStar() => IsStarred = _host.IsStarred(Key);

    [RelayCommand]
    private void ToggleStar()
    {
        _host.SetStarred(Key, !IsStarred);
        RefreshStar();
    }

    [RelayCommand]
    private void Open() => _host.Open(this);

    [RelayCommand]
    private void Play() => _host.Play(this);
}

/// <summary>A titled group of inspector rows.</summary>
/// <param name="title">The heading, or empty.</param>
/// <param name="rows">The rows.</param>
public sealed class ScoutingInspectorSectionViewModel(string title, IReadOnlyList<ScoutingInspectorRowViewModel> rows)
{
    public string Title { get; } = title;

    public bool HasTitle => Title.Length > 0;

    public IReadOnlyList<ScoutingInspectorRowViewModel> Rows { get; } = rows;
}

/// <summary>
///     The inspector: the focused layer's title, scope, build status, read sentences and sections, or the
///     sentence that says why it has none.
/// </summary>
public sealed partial class ScoutingInspectorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _buildCode = "";

    /// <summary>The focused layer is still building: the view draws skeleton rows in place of its lines.</summary>
    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    private string _reviewLine = "";

    [ObservableProperty]
    private string _scopeLine = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _title = "";

    public ObservableCollection<ScoutingInspectorRowViewModel> Reads { get; } = [];

    public ObservableCollection<ScoutingInspectorSectionViewModel> Sections { get; } = [];

    /// <summary>The focused layer's window pills (the AWP trail); empty for every other layer.</summary>
    public ObservableCollection<ScoutingWindowPillViewModel> Windows { get; } = [];

    [ObservableProperty]
    private bool _hasWindows;

    public bool HasStatusMessage => StatusMessage.Length > 0;

    /// <summary>Shows the focused layer's window pills, or none.</summary>
    /// <param name="pills">The pills.</param>
    public void ShowWindows(IEnumerable<ScoutingWindowPillViewModel> pills)
    {
        Windows.Clear();
        foreach (ScoutingWindowPillViewModel pill in pills)
        {
            Windows.Add(pill);
        }

        HasWindows = Windows.Count > 0;
    }

    /// <summary>Every starrable line shown, reads first.</summary>
    public IEnumerable<ScoutingInspectorRowViewModel> AllRows => Reads.Concat(Sections.SelectMany(s => s.Rows));

    /// <summary>Shows a layer's build.</summary>
    /// <param name="title">The inspector title.</param>
    /// <param name="scope">The scope line.</param>
    /// <param name="buildCode">Plan §2's code.</param>
    /// <param name="status">The build's state.</param>
    /// <param name="reads">The read sentences.</param>
    /// <param name="sections">The sections.</param>
    public void Show(string title, string scope, string buildCode, ScoutingLayerStatus status,
        IEnumerable<ScoutingInspectorRowViewModel> reads, IEnumerable<ScoutingInspectorSectionViewModel> sections)
    {
        Title = title;
        ScopeLine = scope;
        // A placeholder's gap id lives here, on the Build status chip, and nowhere in the copy.
        BuildCode = status is { State: ScoutingLayerState.Placeholder, GapId: { } gap }
            ? string.Join(" · ", new[] { buildCode, gap }.Where(p => p.Length > 0))
            : buildCode;
        IsBuilding = status.State == ScoutingLayerState.Building;
        StatusMessage = status.State is ScoutingLayerState.Ready or ScoutingLayerState.Building ? "" : status.Display;
        Reads.Clear();
        foreach (ScoutingInspectorRowViewModel read in reads)
        {
            Reads.Add(read);
        }

        Sections.Clear();
        foreach (ScoutingInspectorSectionViewModel section in sections)
        {
            Sections.Add(section);
        }
    }
}
