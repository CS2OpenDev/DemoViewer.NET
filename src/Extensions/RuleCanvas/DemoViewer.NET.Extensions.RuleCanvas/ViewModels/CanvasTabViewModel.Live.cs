#region

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Live values: the drawn ruleset evaluated over the open demo, kept as one <see cref="LiveValuesRun" />
///     and shown as a badge on every node and a line on every wire for the selected instance. A write marks
///     the run stale and queues a replacement after a pause; a ruleset or demo switch drops it. The run is
///     re-applied after every redraw, since a redraw rebuilds the node view models. While the tab is active
///     the badges follow the shell's playhead, and the menus seek it to a fire.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>The host's Library tab, where a demo is opened from.</summary>
    public const string LibraryTabId = "builtin.library";

    private LiveValuesService _liveService = null!;
    private Func<string?> _demoPath = null!;
    private Action<Action> _post = null!;
    private Func<int>? _currentTick;
    private Func<Action, IDisposable>? _watchPlayhead;
    private Action<int>? _seekToTick;
    private IDisposable? _playheadWatch;
    private IModuleContext? _moduleContext;
    private string? _liveRulesetId;
    private string _liveFingerprint = "";
    private int _liveGeneration;
    private CancellationTokenSource? _liveDelay;
    private CancellationTokenSource? _liveRunCts;

    // What the playhead update walks: each badged node with its track, and the index of the point it
    // shows, so a tick that moves nothing costs a binary search and no string.
    private readonly List<LiveNodeBinding> _liveNodes = [];
    private readonly List<LiveWireBinding> _liveWires = [];

    /// <summary>The last run accepted, or null. One per canvas; the next replaces it.</summary>
    [ObservableProperty]
    private LiveValuesRun? _liveRun;

    /// <summary>The instance the badges show: the game, or one player.</summary>
    [ObservableProperty]
    private LiveInstance? _selectedInstance;

    /// <summary>True when the text drawn differs from the text the run evaluated. The badges stay, greyed.</summary>
    [ObservableProperty]
    private bool _isLiveStale;

    /// <summary>True while a run is queued or evaluating.</summary>
    [ObservableProperty]
    private bool _isLiveRunning;

    /// <summary>The shell's playhead tick while this tab is active and the host has a clock; null otherwise.</summary>
    [ObservableProperty]
    private int? _playheadTick;

    /// <summary>The run's instances, the game first, for the picker.</summary>
    public ObservableCollection<LiveInstance> Instances { get; } = [];

    /// <summary>How long after a write the replacement run is queued. Another write restarts the wait.</summary>
    public TimeSpan LiveAutoRunDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The auto-run a write scheduled, through its evaluation; null when none is pending. For tests.</summary>
    public Task? PendingLiveRun { get; private set; }

    public bool HasDemo => _demoPath() is not null;

    public bool HasLiveRun => LiveRun is not null;

    /// <summary>True while the tab listens to the shell's playhead.</summary>
    public bool IsWatchingPlayhead => _playheadWatch is not null;

    /// <summary>The open demo's name, cut to fit the strip; the full name is the chip's tooltip.</summary>
    public string DemoChipText => _demoPath() is { } path ? ShortDemoName(Path.GetFileNameWithoutExtension(path)) : "no demo open";

    /// <summary>The open demo's full file name, or null.</summary>
    public string? DemoFileName => _demoPath() is { } path ? Path.GetFileName(path) : null;

    // Demo files are named by long ids; the strip keeps the start and the end so two look different.
    public static string ShortDemoName(string stem) =>
        stem.Length <= 18 ? stem : string.Concat(stem.AsSpan(0, 9), "…", stem.AsSpan(stem.Length - 6));

    /// <summary>The strip's live state, in words; with a clock, where the values are read from.</summary>
    public string LiveState
    {
        get
        {
            if (!HasDemo)
            {
                return "no demo open";
            }

            if (IsLiveRunning)
            {
                return "evaluating";
            }

            if (LiveRun is not { } run)
            {
                return "no values yet";
            }

            string state = IsLiveStale ? "stale: edited since the run" : "values ready";
            return PlayheadTick is null ? state : state + ", " + LiveValuesText.Position(PlayheadTick, run);
        }
    }

    /// <summary>A run can be asked for while a demo is open and the drawn ruleset composes.</summary>
    public bool CanRunLive => HasDemo && SelectedRuleset is not null && _composed is { } composed && composed.Chain.IsLowered;

    private void InitLive(LiveValuesHost? live)
    {
        _liveService = new LiveValuesService(live?.Jobs ?? (static () => null), live?.DemoPath ?? (static () => null),
            live?.DemoHash ?? (static () => null), live?.Log);
        _demoPath = live?.DemoPath ?? (static () => null);
        _post = live?.Post ?? (static action => action());
        _currentTick = live?.CurrentTick;
        _watchPlayhead = live?.WatchPlayhead;
        _seekToTick = live?.SeekToTick;
        live?.OnDemoChanged?.Invoke(DemoChanged);
    }

    // The host joins the playhead hub only while listened to, so the watch lives from activation to deactivation.
    private void StartPlayhead(IModuleContext? context)
    {
        _moduleContext = context;
        if (_playheadWatch is null && _watchPlayhead is not null)
        {
            _playheadWatch = _watchPlayhead(OnPlayhead);
            OnPlayhead();
        }
    }

    private void StopPlayhead()
    {
        _playheadWatch?.Dispose();
        _playheadWatch = null;
        if (PlayheadTick is not null)
        {
            PlayheadTick = null;
        }
    }

    // Runs once per rendered frame during playback: only what the tick moved is re-said.
    private void OnPlayhead()
    {
        if (_currentTick is null)
        {
            return;
        }

        int tick = _currentTick();
        if (PlayheadTick == tick)
        {
            return;
        }

        PlayheadTick = tick;
    }

    /// <summary>Opens the host's Library tab, where a demo is opened from.</summary>
    [RelayCommand]
    private void OpenLibrary() => _selectTab(LibraryTabId);

    /// <summary>Evaluates the drawn ruleset over the open demo now. A run already in flight is cancelled.</summary>
    [RelayCommand(CanExecute = nameof(CanRunLive))]
    private Task RunLive() => RunLiveAsync();

    /// <summary>
    ///     Shows <paramref name="run" /> as if it had just arrived for the drawn ruleset: for previews and
    ///     tests, which have no demo. The run is current, not stale, until the next write.
    /// </summary>
    public void ShowLiveRun(LiveValuesRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _liveRulesetId = SelectedRuleset?.Id;
        _liveFingerprint = Fingerprint();
        Accept(run);
    }

    /// <summary>Drops the run, the pending auto-run and any run in flight; every badge clears.</summary>
    public void ClearLive()
    {
        _liveGeneration++;
        _liveDelay?.Cancel();
        _liveDelay = null;
        PendingLiveRun = null;
        _liveRunCts?.Cancel();
        _liveRunCts = null;
        _liveRulesetId = null;
        _liveFingerprint = "";
        bool had = LiveRun is not null || IsLiveRunning || IsLiveStale;
        IsLiveRunning = false;
        IsLiveStale = false;
        LiveRun = null;
        Instances.Clear();
        SelectedInstance = null;
        IsStepMode = false;
        if (had)
        {
            ApplyLive(Nodes, Connections);
        }

        NotifyLiveState();
    }

    private async Task RunLiveAsync()
    {
        if (SelectedRuleset is not { } selected || _composed is not { } composed || !composed.Chain.IsLowered || !HasDemo)
        {
            return;
        }

        _liveDelay?.Cancel();
        _liveDelay = null;
        _liveRunCts?.Cancel();
        CancellationTokenSource cts = _liveRunCts = new CancellationTokenSource();
        int generation = ++_liveGeneration;
        _liveRulesetId = selected.Id;
        string fingerprint = Fingerprint();
        IsLiveRunning = true;
        LiveValuesRun? run;
        try
        {
            // The documents as the engine takes them: an unfinished entry of the open one is left out.
            run = await _liveService.RunAsync(selected.Id, composed.Evaluable.Count > 0 ? composed.Evaluable : [.. composed.Documents.Values], cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_liveRunCts, cts))
            {
                _liveRunCts = null;
                IsLiveRunning = false;
            }

            cts.Dispose();
        }

        // Superseded, cleared, or drawn for another ruleset meanwhile: the run is for text no longer shown.
        if (run is null || generation != _liveGeneration || SelectedRuleset?.Id != selected.Id)
        {
            return;
        }

        _liveFingerprint = fingerprint;
        Accept(run);
    }

    // After a write: wait for the pause, then run on the UI thread. Another write restarts the wait.
    private void ScheduleLiveRun()
    {
        _liveDelay?.Cancel();
        _liveDelay = null;
        if (!HasDemo)
        {
            PendingLiveRun = null;
            return;
        }

        CancellationTokenSource cts = _liveDelay = new CancellationTokenSource();
        PendingLiveRun = AutoRunAsync(cts.Token);
    }

    private async Task AutoRunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(LiveAutoRunDelay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() => _ = RunLiveAsync().ContinueWith(_ => done.TrySetResult(), TaskScheduler.Default));
        await done.Task;
    }

    private void Accept(LiveValuesRun run)
    {
        LiveRun = run;
        LiveInstance? was = SelectedInstance;
        Instances.Clear();
        foreach (LiveInstance instance in run.Instances)
        {
            Instances.Add(instance);
        }

        LiveInstance? kept = was is null ? null : run.Instances.FirstOrDefault(i => i.Slot == was.Slot);
        SelectedInstance = kept ?? run.Instances.FirstOrDefault(i => i.Slot is not null) ?? (run.Instances.Count > 0 ? run.Instances[0] : null);
        IsLiveStale = Fingerprint() != _liveFingerprint;
        ApplyLive(Nodes, Connections);
        NotifyLiveState();
    }

    // Called by Redraw once the nodes are built: another ruleset drops the run, a recompose re-checks the
    // text against the run's, and the badges are put back on the fresh view models.
    private void SyncLive(RulesetCorpus.Entry? value, bool recompose, IEnumerable<CanvasNodeViewModel> nodes, IEnumerable<CanvasConnectionViewModel> wires)
    {
        // No run was ever accepted: nothing to badge, but a pinned viewer still gets its panel.
        if (_liveRulesetId is null)
        {
            ApplyLive(nodes, wires);
            NotifyLiveState();
            return;
        }

        if (value is null || !string.Equals(value.Id, _liveRulesetId, StringComparison.Ordinal))
        {
            ClearLive();
            return;
        }

        if (recompose && LiveRun is not null)
        {
            IsLiveStale = Fingerprint() != _liveFingerprint;
        }

        ApplyLive(nodes, wires);
        NotifyLiveState();
    }

    // The full pass: every node and wire gets its text for the instance, and the playhead bindings are rebuilt.
    private void ApplyLive(IEnumerable<CanvasNodeViewModel> nodes, IEnumerable<CanvasConnectionViewModel> wires)
    {
        _liveNodes.Clear();
        _liveWires.Clear();
        _stepTicks = null;
        ClearFlash();
        LiveValuesRun? run = LiveRun;
        LiveInstance? instance = SelectedInstance;
        if (run is null || instance is null)
        {
            foreach (CanvasNodeViewModel node in nodes)
            {
                node.LiveBadge = "";
                node.LiveTip = "";
                node.IsLiveStale = false;
                node.LiveViewer = ViewerFor(node, null, null, null, false, null);
                foreach (CanvasColumnViewModel column in node.Columns)
                {
                    column.LiveBadge = "";
                    column.LiveTip = "";
                    column.IsLiveStale = false;
                }
            }

            foreach (CanvasConnectionViewModel wire in wires)
            {
                wire.LiveTip = "";
            }

            return;
        }

        HashSet<RulesetDocumentNodeKey> unbound = [.. run.Unbound];
        bool stale = IsLiveStale;
        int? tick = TickInRun(run);
        foreach (CanvasNodeViewModel node in nodes)
        {
            bool isBool = node.ValueType == "bool";
            NodeTrack? track = TrackFor(run, node.Key, instance.Index);
            node.LiveViewer = ViewerFor(node, run, instance, track, stale, tick);
            if (node.IsDependency)
            {
                ShowDependencyLive(node, run, instance, stale);
            }
            else if (track is not null)
            {
                LiveNodeBinding binding = new(node, track, isBool) { Viewer = node.LiveViewer };
                _liveNodes.Add(binding);
                binding.Show(instance, stale, tick);
                node.IsLiveStale = stale;
            }
            else if (unbound.Contains(node.Key))
            {
                node.LiveBadge = LiveValuesText.NoData;
                node.LiveTip = "The run has no engine node for this entry: its view did not bind, or it was excluded.";
                node.IsLiveStale = stale;
            }
            else
            {
                node.LiveBadge = "";
                node.LiveTip = "";
                node.IsLiveStale = false;
            }
        }

        List<CanvasConnectionViewModel> wireList = [.. wires];
        foreach (CanvasConnectionViewModel wire in wireList)
        {
            if (WireTrackFor(run, wire, instance.Index) is { } own)
            {
                LiveWireBinding binding = new(wire, own, wire.LiveKind);
                _liveWires.Add(binding);
                binding.Show(stale, tick);
            }
            else
            {
                wire.LiveTip = WireFallback(run, wire, instance.Index, stale);
            }
        }

        // A show node's column carries the surfaced stat's badge: the stat at the other end of its wire, or
        // the stat the column names when nothing is wired to it.
        List<CanvasNodeViewModel> nodeList = [.. nodes];
        foreach (CanvasNodeViewModel show in nodeList.Where(n => n.IsShow))
        {
            foreach (CanvasColumnViewModel column in show.Columns)
            {
                CanvasNodeViewModel? stat = SurfacedStat(column, show, nodeList, wireList);
                if (stat is not null && TrackFor(run, stat.Key, instance.Index) is { } track)
                {
                    LiveNodeBinding binding = new(column, track, stat.ValueType == "bool");
                    _liveNodes.Add(binding);
                    binding.Show(instance, stale, tick);
                    column.IsLiveStale = stale;
                }
                else
                {
                    column.LiveBadge = "";
                    column.LiveTip = "";
                    column.IsLiveStale = false;
                }
            }
        }
    }

    private static CanvasNodeViewModel? SurfacedStat(CanvasColumnViewModel column, CanvasNodeViewModel show,
        List<CanvasNodeViewModel> nodes, List<CanvasConnectionViewModel> wires)
    {
        if (column.Port is { } port && wires.FirstOrDefault(w => ReferenceEquals(w.Target, port))?.Source.Owner is { } wired)
        {
            return wired;
        }

        return nodes.FirstOrDefault(n => n.IsEntry && !n.IsDefine && ShowSurface.Refers(column.Stat, n.Id, show.Ruleset));
    }

    // A folded dependency speaks for the entries the open document reads: the fires summed on the badge,
    // one line per entry on the tip. Nothing when the run tracked none of them.
    private static void ShowDependencyLive(CanvasNodeViewModel node, LiveValuesRun run, LiveInstance instance, bool stale)
    {
        List<(string Entry, NodeTrack Track)> tracks = [];
        foreach (string entry in node.DependencyEntries)
        {
            if (TrackFor(run, new RulesetDocumentNodeKey(node.Id, entry), instance.Index) is { } track)
            {
                tracks.Add((entry, track));
            }
        }

        if (tracks.Count == 0)
        {
            node.LiveBadge = "";
            node.LiveTip = "";
            node.IsLiveStale = false;
            return;
        }

        int fires = tracks.Sum(t => t.Track.FireCount);
        node.LiveBadge = LiveValuesText.Fires(fires);
        node.LiveTip = LiveValuesText.DependencyTip(tracks, instance, stale);
        node.IsLiveStale = stale;
    }

    // A wire with no engine edge of its own speaks from a node at one of its ends.
    private static string WireFallback(LiveValuesRun run, CanvasConnectionViewModel wire, int instance, bool stale)
    {
        if (wire.LiveKind == LiveWireKind.Read && wire.Source.Owner is { } source && TrackFor(run, source.Key, instance) is { } sourceTrack)
        {
            return LiveValuesText.ReadFallback(sourceTrack, source.ValueType == "bool", stale);
        }

        return wire.Target.Owner is { } target && TrackFor(run, target.Key, instance) is { } targetTrack
            ? LiveValuesText.TargetFallback(targetTrack, stale)
            : "";
    }

    // The playhead moved: one pass over the bindings, no node or wire re-created, nothing recomposed.
    private void ApplyPlayhead()
    {
        LiveValuesRun? run = LiveRun;
        LiveInstance? instance = SelectedInstance;
        if (run is null || instance is null)
        {
            return;
        }

        int? tick = TickInRun(run);
        bool stale = IsLiveStale;
        foreach (LiveNodeBinding binding in _liveNodes)
        {
            binding.Show(instance, stale, tick);
        }

        foreach (LiveWireBinding binding in _liveWires)
        {
            binding.Show(stale, tick);
        }
    }

    // The playhead's tick while it lies inside the run; null means the badges show final values.
    private int? TickInRun(LiveValuesRun run) => PlayheadTick is { } tick && run.Covers(tick) ? tick : null;

    // A node kept once for the game has that one track under every instance.
    private static NodeTrack? TrackFor(LiveValuesRun run, RulesetDocumentNodeKey key, int instance) =>
        run.Nodes.TryGetValue((key, instance), out NodeTrack? own) ? own
        : run.Nodes.TryGetValue((key, 0), out NodeTrack? game) ? game
        : null;

    private static WireTrack? WireTrackFor(LiveValuesRun run, CanvasConnectionViewModel wire, int instance)
    {
        if (wire.Source.Owner is not { } source || wire.Target.Owner is not { } target)
        {
            return null;
        }

        LiveWireKey key = new(source.Key, target.Key, wire.LiveKind);
        return run.Wires.TryGetValue((key, instance), out WireTrack? own) ? own
            : run.Wires.TryGetValue((key, 0), out WireTrack? game) ? game
            : null;
    }

    // The composed documents' texts as the files hold them, so a write and its undo compare equal to the run's.
    private string Fingerprint()
    {
        if (_composed is not { } composed || _corpus is null)
        {
            return "";
        }

        StringBuilder all = new();
        foreach (string id in composed.Documents.Keys.Order(StringComparer.Ordinal))
        {
            string? path = _corpus.Entries.FirstOrDefault(e => e.Id == id)?.Path;
            string text;
            try
            {
                text = path is null ? "" : File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                text = "";
            }

            all.Append(id).Append('\0').Append(text).Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())));
    }

    private void DemoChanged()
    {
        ClearLive();
        NotifyLiveState();
    }

    private void NotifyLiveState()
    {
        OnPropertyChanged(nameof(HasDemo));
        OnPropertyChanged(nameof(HasLiveRun));
        OnPropertyChanged(nameof(DemoChipText));
        OnPropertyChanged(nameof(LiveState));
        OnPropertyChanged(nameof(CanRunLive));
        RunLiveCommand.NotifyCanExecuteChanged();
        NotifyStep();
    }

    partial void OnSelectedInstanceChanged(LiveInstance? value) => ApplyLive(Nodes, Connections);

    partial void OnIsLiveStaleChanged(bool value)
    {
        ApplyLive(Nodes, Connections);
        OnPropertyChanged(nameof(LiveState));
    }

    partial void OnIsLiveRunningChanged(bool value) => OnPropertyChanged(nameof(LiveState));

    partial void OnLiveRunChanged(LiveValuesRun? value)
    {
        OnPropertyChanged(nameof(HasLiveRun));
        OnPropertyChanged(nameof(LiveState));
    }

    partial void OnPlayheadTickChanged(int? value)
    {
        ApplyPlayhead();
        OnPropertyChanged(nameof(LiveState));
    }

    // One badge, on a node or a show column: the point shown is remembered, so a tick that lands on the same
    // fire is free. The final part of the tip is kept between playhead moves; a move writes one line and one join.
    private sealed class LiveNodeBinding(ILiveBadge node, NodeTrack track, bool isBool)
    {
        private int _shown = int.MinValue;
        private bool _stale;
        private LiveInstance? _instance;
        private string _rest = "";

        /// <summary>The pinned viewer under the node, whose marker follows the playhead; null when none is pinned.</summary>
        public CanvasLiveViewerViewModel? Viewer { get; init; }

        public void Show(LiveInstance instance, bool stale, int? tick)
        {
            Viewer?.SetPlayhead(tick);
            int index = tick is { } at && track.Series.Count > 0 ? track.Series.IndexAt(at) : int.MaxValue;
            bool same = stale == _stale && ReferenceEquals(instance, _instance);
            if (index == _shown && same)
            {
                return;
            }

            if (!same || _instance is null)
            {
                _rest = LiveValuesText.Tip(track, instance, isBool, stale);
                _stale = stale;
                _instance = instance;
            }

            _shown = index;
            if (tick is { } t && index != int.MaxValue)
            {
                node.LiveBadge = LiveValuesText.BadgeAt(track, isBool, index);
                node.LiveTip = LiveValuesText.PlayheadLine(track, isBool, t, index) + "\n" + _rest;
            }
            else
            {
                node.LiveBadge = LiveValuesText.Badge(track, isBool);
                node.LiveTip = _rest;
            }
        }
    }

    private sealed class LiveWireBinding(CanvasConnectionViewModel wire, WireTrack track, LiveWireKind kind)
    {
        private int _shown = int.MinValue;
        private bool _stale;
        private string? _rest;

        public void Show(bool stale, int? tick)
        {
            int count = tick is { } at && track.Ticks.Count > 0 ? track.Ticks.CountThrough(at) : int.MaxValue;
            if (count == _shown && stale == _stale && _rest is not null)
            {
                return;
            }

            if (_rest is null || stale != _stale)
            {
                _rest = LiveValuesText.WireTip(track, kind, stale);
                _stale = stale;
            }

            _shown = count;
            wire.LiveTip = tick is { } t && count != int.MaxValue ? LiveValuesText.FiresByLine(count, t) + "\n" + _rest : _rest;
        }
    }
}
