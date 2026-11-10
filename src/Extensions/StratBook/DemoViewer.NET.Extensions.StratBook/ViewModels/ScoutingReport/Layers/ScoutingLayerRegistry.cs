#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     The map stage's layers for the scope in view: which draw, which one the inspector shows, and every
///     layer's build. The tab VM sets the scope; the view binds the chips, the strip and the inspector.
///     <para>
///         <b>Focus and fade.</b> Inspecting a map layer (its name) turns it on and fades every other drawn
///         layer to 30 %. Turning a layer on focuses it; turning the focused one off moves focus to the first
///         layer still drawn, or to the Economy strip when none is. Inspecting a strip layer draws every map
///         layer at full strength. Each side remembers its own drawn set for the session.
///     </para>
///     <para>
///         <b>Builds.</b> <see cref="SetScope" /> bumps a generation and cancels the last scope's builds; each
///         layer builds on its own (awaiting the team's shared loads) and lands through the UI-thread post, and
///         one that lands behind the generation is dropped. A build that finishes synchronously (every load
///         already in) lands at once.
///     </para>
/// </summary>
public sealed partial class ScoutingLayerRegistry : ObservableObject, IScoutingRowHost, IDisposable
{
    private readonly List<ScoutingLayerChipViewModel> _all;
    private readonly Dictionary<(ScoutingSide, string), bool> _on = [];
    private readonly Dictionary<string, int> _rebuilds = new(StringComparer.Ordinal);
    private readonly Action<Action> _post;
    private readonly IScoutingRowHost _rows;
    private CancellationTokenSource _cancel = new();
    private ScoutingReportData? _data;
    private int _generation;

    [ObservableProperty]
    private ScoutingLayerChipViewModel? _focused;

    /// <param name="layers">The layers, in chip order.</param>
    /// <param name="post">UI-thread marshal for a build that lands off it.</param>
    /// <param name="rows">The tab: stars, Review and 2D for the inspector's rows.</param>
    public ScoutingLayerRegistry(IEnumerable<IScoutingLayer> layers, Action<Action> post, IScoutingRowHost rows)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(rows);
        _post = post;
        _rows = rows;
        _all = [.. layers.Select(l => new ScoutingLayerChipViewModel(this, l))];
        if (Find("score") is { } score)
        {
            score.Companion = Find("timeouts");
        }
    }

    /// <summary>Every layer, both sides.</summary>
    public IReadOnlyList<ScoutingLayerChipViewModel> All => _all;

    /// <summary>The map layers offered on the side in view.</summary>
    public ObservableCollection<ScoutingLayerChipViewModel> MapChips { get; } = [];

    /// <summary>The strip minis under the map: Economy, Pace (T) or Rotations (CT), Opening impact, By score.</summary>
    public ObservableCollection<ScoutingLayerChipViewModel> StripMinis { get; } = [];

    /// <summary>The legend lines of the drawn layers.</summary>
    public ObservableCollection<ScoutingLegendEntry> Legend { get; } = [];

    public ScoutingInspectorViewModel Inspector { get; } = new();

    public string Map { get; private set; } = "";

    public ScoutingSide Side { get; private set; } = ScoutingSide.Ct;

    public ScoutingBuy Buy { get; private set; } = ScoutingBuy.All;

    /// <summary>"Mirage · CT · Full buy · 6 maps", appended by the tab.</summary>
    public string ScopeSuffix { get; set; } = "";

    /// <summary>Every build of the current scope, landed or not; tests await it, then drain the post.</summary>
    internal Task Idle { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        _cancel.Cancel();
        _cancel.Dispose();
    }

    /// <summary>The chip of a layer id, or null.</summary>
    /// <param name="id">The id.</param>
    public ScoutingLayerChipViewModel? Find(string id) => _all.FirstOrDefault(c => c.Id == id);

    /// <summary>Whether a layer draws on a side.</summary>
    /// <param name="side">The side.</param>
    /// <param name="chip">The layer.</param>
    public bool IsOn(ScoutingSide side, ScoutingLayerChipViewModel chip) =>
        _on.TryGetValue((side, chip.Id), out bool on) ? on : chip.Layer.OnByDefault(side);

    /// <summary>Forgets the drawn sets, for a newly opened team.</summary>
    public void ResetToggles() => _on.Clear();

    /// <summary>Sets the scope and rebuilds every layer that applies to its side; null data clears the stage.</summary>
    /// <param name="data">The team's loads, or null with no team.</param>
    /// <param name="map">The map.</param>
    /// <param name="side">The side.</param>
    /// <param name="buy">The buy filter.</param>
    public void SetScope(ScoutingReportData? data, string map, ScoutingSide side, ScoutingBuy buy)
    {
        _data = data;
        Map = map;
        Side = side;
        Buy = buy;
        int generation = Interlocked.Increment(ref _generation);
        _cancel.Cancel();
        _cancel.Dispose();
        _cancel = new CancellationTokenSource();
        CancellationToken token = _cancel.Token;
        _rebuilds.Clear();

        List<ScoutingLayerChipViewModel> offered = [.. _all.Where(c => c.Layer.AppliesTo(side))];
        foreach (ScoutingLayerChipViewModel chip in _all)
        {
            chip.SetSide(side);
            chip.SetOn(chip.Layer.AppliesTo(side) && IsOn(side, chip));
            chip.Apply(new ScoutingLayerResult { Status = ScoutingLayerStatus.Building }, null);
        }

        Replace(MapChips, offered.Where(c => c.IsMap));
        Replace(StripMinis, ((string[])["econ", side == ScoutingSide.T ? "pace" : "rotations", "impact", "score"])
            .Select(Find).OfType<ScoutingLayerChipViewModel>());
        if (Focused is null || !Focused.Layer.AppliesTo(side) || (Focused.IsMap && !Focused.IsOn))
        {
            SetFocus(DefaultFocus());
        }

        if (data is null || map.Length == 0)
        {
            foreach (ScoutingLayerChipViewModel chip in _all)
            {
                chip.Apply(ScoutingLayerResult.Empty("select a team"), null);
            }

            Idle = Task.CompletedTask;
            Redraw();
            return;
        }

        List<Task> builds = [];
        foreach (ScoutingLayerChipViewModel chip in offered)
        {
            ScoutingLayerContext context = ContextFor(chip.Layer, data);
            Task<ScoutingLayerResult> build = Build(chip.Layer, context, token);
            if (build.IsCompleted)
            {
                chip.Apply(Landed(build), context);
                continue;
            }

            builds.Add(build.ContinueWith(t => _post(() =>
            {
                if (generation == Volatile.Read(ref _generation))
                {
                    chip.Apply(Landed(t), context);
                    Redraw();
                }
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }

        Idle = Task.WhenAll(builds);
        Redraw();
    }

    /// <summary>
    ///     Rebuilds some of the scope's layers in place, the rest untouched: a source only they read changed (Strat
    ///     Mining published). Each lands under the scope's generation, so a scope change drops it, and under its
    ///     own rebuild stamp, so an older rebuild of the same layer that lands late is dropped too.
    /// </summary>
    /// <param name="ids">The layer ids; those not offered on the side in view are skipped.</param>
    public void Rebuild(params string[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_data is not { } data || Map.Length == 0)
        {
            return;
        }

        int generation = Volatile.Read(ref _generation);
        CancellationToken token = _cancel.Token;
        List<Task> builds = [Idle];
        foreach (ScoutingLayerChipViewModel chip in _all.Where(c => ids.Contains(c.Id, StringComparer.Ordinal)
                                                                   && c.Layer.AppliesTo(Side)))
        {
            ScoutingLayerContext context = ContextFor(chip.Layer, data);
            int stamp = _rebuilds[chip.Id] = _rebuilds.GetValueOrDefault(chip.Id) + 1;
            Task<ScoutingLayerResult> build = Build(chip.Layer, context, token);
            if (build.IsCompleted)
            {
                chip.Apply(Landed(build), context);
                continue;
            }

            builds.Add(build.ContinueWith(t => _post(() =>
            {
                if (generation == Volatile.Read(ref _generation) && _rebuilds.GetValueOrDefault(chip.Id) == stamp)
                {
                    chip.Apply(Landed(t), context);
                    Redraw();
                }
            }), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        }

        Idle = Task.WhenAll(builds);
        Redraw();
    }

    // The scope for one layer: a windowed layer's context carries its window, so its stars file under it.
    private ScoutingLayerContext ContextFor(IScoutingLayer layer, ScoutingReportData data) =>
        new(data, Map, Side, Buy, layer is IScoutingWindowedLayer windowed ? windowed.Window : "");

    /// <summary>Sets a windowed layer's window and rebuilds that layer alone; another window or layer is ignored.</summary>
    /// <param name="chip">The layer.</param>
    /// <param name="window">One of its windows' keys.</param>
    public void SelectWindow(ScoutingLayerChipViewModel chip, string window)
    {
        ArgumentNullException.ThrowIfNull(chip);
        if (chip.Layer is not IScoutingWindowedLayer windowed || windowed.Window == window
            || windowed.Windows.All(w => w.Key != window))
        {
            return;
        }

        windowed.Window = window;
        if (_data is null || Map.Length == 0)
        {
            Redraw();
            return;
        }

        Rebuild(chip.Id);
    }

    /// <summary>Builds one layer for a scope, catching what a layer throws as an empty result.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="context">The scope.</param>
    /// <param name="token">Cancelled with the scope.</param>
    public static Task<ScoutingLayerResult> Build(IScoutingLayer layer, ScoutingLayerContext context,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(layer);
        try
        {
            return layer.BuildAsync(context, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(ScoutingLayerResult.Empty($"could not build: {ex.Message}"));
        }
    }

    private static ScoutingLayerResult Landed(Task<ScoutingLayerResult> build) =>
        build.IsCompletedSuccessfully
            ? build.Result
            : build.IsCanceled
                ? new ScoutingLayerResult { Status = ScoutingLayerStatus.Building }
                : ScoutingLayerResult.Empty(
                    $"could not build: {build.Exception?.GetBaseException().Message ?? "unknown error"}");

    /// <summary>Draws a layer or stops drawing it, moving the focus by the stage's rules.</summary>
    /// <param name="chip">The layer.</param>
    /// <param name="on">Drawn.</param>
    public void Toggle(ScoutingLayerChipViewModel chip, bool on)
    {
        ArgumentNullException.ThrowIfNull(chip);
        _on[(Side, chip.Id)] = on;
        chip.SetOn(on);
        if (on)
        {
            SetFocus(chip);
        }
        else if (ReferenceEquals(Focused, chip))
        {
            SetFocus(DefaultFocus());
        }

        Redraw();
    }

    /// <summary>Shows a layer in the inspector; a map layer is turned on to be seen.</summary>
    /// <param name="chip">The layer.</param>
    public void Focus(ScoutingLayerChipViewModel chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        if (chip.IsMap && !chip.IsOn)
        {
            _on[(Side, chip.Id)] = true;
            chip.SetOn(true);
        }

        SetFocus(chip);
        Redraw();
    }

    // The first map layer drawn on this side, else the Economy strip.
    private ScoutingLayerChipViewModel? DefaultFocus() =>
        _all.FirstOrDefault(c => c.IsMap && c.Layer.AppliesTo(Side) && c.IsOn
                                 && c.Status.State != ScoutingLayerState.Placeholder)
        ?? _all.FirstOrDefault(c => c.IsMap && c.Layer.AppliesTo(Side) && c.IsOn)
        ?? Find("econ");

    private void SetFocus(ScoutingLayerChipViewModel? chip)
    {
        foreach (ScoutingLayerChipViewModel c in _all)
        {
            c.IsFocused = ReferenceEquals(c, chip);
        }

        Focused = chip;
    }

    /// <summary>
    ///     Refreshes everything that follows from the chips: the marks and their fade, the legend, and the
    ///     inspector for the focused layer.
    /// </summary>
    private void Redraw()
    {
        bool mapFocus = Focused is { IsMap: true };
        Legend.Clear();
        foreach (ScoutingLayerChipViewModel chip in _all)
        {
            bool visible = chip.IsMap && chip.IsOn && chip.Layer.AppliesTo(Side);
            float fade = mapFocus && !ReferenceEquals(chip, Focused) ? ScoutingMarksDocument.FadedOpacity : 1f;
            chip.Marks.Set(chip.Result.Marks, visible, fade);
            if (visible)
            {
                foreach (ScoutingLegendEntry entry in chip.Result.Legend)
                {
                    Legend.Add(entry);
                }
            }
        }

        ShowInspector();
    }

    private void ShowInspector()
    {
        if (Focused is not { } chip)
        {
            Inspector.Show("", "", "", ScoutingLayerResult.Empty("").Status, [], []);
            Inspector.ShowWindows([]);
            return;
        }

        ScoutingLayerResult result = chip.Result;
        ScoutingLayerContext? context = chip.Context;
        List<ScoutingInspectorRowViewModel> reads = [];
        List<ScoutingInspectorSectionViewModel> sections = [];
        if (context is not null)
        {
            reads.AddRange(result.Reads.Select(r =>
                new ScoutingInspectorRowViewModel(this, r,
                    ScoutingReportStarKeys.Key(context.Scope(chip.Layer, r.Buy), r.StarText))));
            sections.AddRange(result.Sections.Select(s => new ScoutingInspectorSectionViewModel(s.Title,
            [
                .. s.Rows.Select(row => new ScoutingInspectorRowViewModel(this, row,
                    ScoutingReportStarKeys.Key(context.Scope(chip.Layer, row.Buy), row.KeyText)))
            ])));
        }

        string buy = !chip.Layer.UsesBuy ? "all buys" : Buy == ScoutingBuy.All ? "all buys" : $"{Buy} buy";
        string scope = string.Join(" · ", new[] { Map, Side.Label(), buy, ScopeSuffix }.Where(p => p.Length > 0));
        Inspector.Show(chip.Title, scope, chip.Layer.BuildCode, result.Status, reads, sections);
        Inspector.ShowWindows(chip.Layer is IScoutingWindowedLayer windowed
            ? windowed.Windows.Select(w => new ScoutingWindowPillViewModel(w, w.Key == windowed.Window,
                key => SelectWindow(chip, key)))
            : []);
    }

    /// <summary>Re-reads every shown star; the one-pager changed somewhere else.</summary>
    public void RefreshStars()
    {
        foreach (ScoutingInspectorRowViewModel row in Inspector.AllRows)
        {
            row.RefreshStar();
        }
    }

    /// <inheritdoc />
    bool IScoutingRowHost.IsStarred(string key) => _rows.IsStarred(key);

    /// <inheritdoc />
    void IScoutingRowHost.SetStarred(string key, bool starred) => _rows.SetStarred(key, starred);

    /// <inheritdoc />
    void IScoutingRowHost.Open(ScoutingInspectorRowViewModel row) => _rows.Open(row);

    /// <inheritdoc />
    void IScoutingRowHost.Play(ScoutingInspectorRowViewModel row) => _rows.Play(row);

    /// <summary>The loads the current scope reads; null with no team.</summary>
    public ScoutingReportData? Data => _data;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        List<T> list = [.. items];
        if (list.SequenceEqual(target))
        {
            return;
        }

        target.Clear();
        foreach (T item in list)
        {
            target.Add(item);
        }
    }
}
