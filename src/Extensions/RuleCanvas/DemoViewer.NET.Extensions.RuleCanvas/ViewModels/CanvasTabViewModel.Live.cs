#region

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Live values: the drawn ruleset evaluated over the open demo, kept as one <see cref="LiveValuesRun" />
///     and shown as a badge on every node and a line on every wire for the selected instance. A write marks
///     the run stale and queues a replacement after a pause; a ruleset or demo switch drops it. The run is
///     re-applied after every redraw, since a redraw rebuilds the node view models.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>The host's Library tab, where a demo is opened from.</summary>
    public const string LibraryTabId = "builtin.library";

    private LiveValuesService _liveService = null!;
    private Func<string?> _demoPath = null!;
    private Action<Action> _post = null!;
    private string? _liveRulesetId;
    private string _liveFingerprint = "";
    private int _liveGeneration;
    private CancellationTokenSource? _liveDelay;
    private CancellationTokenSource? _liveRunCts;

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

    /// <summary>The run's instances, the game first, for the picker.</summary>
    public ObservableCollection<LiveInstance> Instances { get; } = [];

    /// <summary>How long after a write the replacement run is queued. Another write restarts the wait.</summary>
    public TimeSpan LiveAutoRunDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>The auto-run a write scheduled, through its evaluation; null when none is pending. For tests.</summary>
    public Task? PendingLiveRun { get; private set; }

    public bool HasDemo => _demoPath() is not null;

    public bool HasLiveRun => LiveRun is not null;

    /// <summary>The strip's demo chip: the open demo's file name, or that none is open.</summary>
    public string DemoChipText => _demoPath() is { } path ? Path.GetFileName(path) : "no demo open";

    /// <summary>The strip's live state, in words.</summary>
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

            if (LiveRun is null)
            {
                return "no values yet";
            }

            return IsLiveStale ? "stale: edited since the run" : "values from " + Path.GetFileName(LiveRun.DemoPath);
        }
    }

    /// <summary>A run can be asked for while a demo is open and the drawn ruleset composes.</summary>
    public bool CanRunLive => HasDemo && SelectedRuleset is not null && _composed is { } composed && composed.Chain.Nodes.Count > 0;

    private void InitLive(LiveValuesHost? live)
    {
        _liveService = new LiveValuesService(live?.Jobs ?? (static () => null), live?.DemoPath ?? (static () => null),
            live?.DemoHash ?? (static () => null), live?.Log);
        _demoPath = live?.DemoPath ?? (static () => null);
        _post = live?.Post ?? (static action => action());
        live?.OnDemoChanged?.Invoke(DemoChanged);
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
        if (had)
        {
            ApplyLive(Nodes, Connections);
        }

        NotifyLiveState();
    }

    private async Task RunLiveAsync()
    {
        if (SelectedRuleset is not { } selected || _composed is not { } composed || composed.Chain.Nodes.Count == 0 || !HasDemo)
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
            run = await _liveService.RunAsync(selected.Id, [.. composed.Documents.Values], cts.Token);
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
        if (_liveRulesetId is null)
        {
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

    private void ApplyLive(IEnumerable<CanvasNodeViewModel> nodes, IEnumerable<CanvasConnectionViewModel> wires)
    {
        LiveValuesRun? run = LiveRun;
        LiveInstance? instance = SelectedInstance;
        if (run is null || instance is null)
        {
            foreach (CanvasNodeViewModel node in nodes)
            {
                node.LiveBadge = "";
                node.LiveTip = "";
                node.IsLiveStale = false;
            }

            foreach (CanvasConnectionViewModel wire in wires)
            {
                wire.LiveTip = "";
            }

            return;
        }

        HashSet<RulesetDocumentNodeKey> unbound = [.. run.Unbound];
        bool stale = IsLiveStale;
        foreach (CanvasNodeViewModel node in nodes)
        {
            bool isBool = node.ValueType == "bool";
            if (TrackFor(run, node.Key, instance.Index) is { } track)
            {
                node.LiveBadge = LiveValuesText.Badge(track, isBool);
                node.LiveTip = LiveValuesText.Tip(track, instance, isBool, stale);
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

        foreach (CanvasConnectionViewModel wire in wires)
        {
            wire.LiveTip = wire.Target.Owner is { } target && TrackFor(run, target.Key, instance.Index) is { } track
                ? LiveValuesText.WireTip(track, stale)
                : "";
        }
    }

    // A node kept once for the game has that one track under every instance.
    private static NodeTrack? TrackFor(LiveValuesRun run, RulesetDocumentNodeKey key, int instance) =>
        run.Nodes.TryGetValue((key, instance), out NodeTrack? own) ? own
        : run.Nodes.TryGetValue((key, 0), out NodeTrack? game) ? game
        : null;

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
}
