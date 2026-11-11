#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Step mode: with the strip's Step toggle on, <c>.</c> and <c>,</c> move the shell's playhead to the
///     next or previous fire over the union of the drawn nodes' fire ticks (the focused slice when one is
///     focused) and flash the wires that fired at that tick for <see cref="FlashDuration" />. The flash is
///     the one transient paint on the canvas, since the step is the user's own action. Step needs a run.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>How long the wires that fired at a stepped tick stay haloed.</summary>
    public static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(300);

    private readonly List<CanvasConnectionViewModel> _flashing = [];
    private int[]? _stepTicks;
    private IDisposable? _flashTimer;
    private Func<TimeSpan, Action, IDisposable>? _flashScheduler;

    /// <summary>True while the playhead is a message stepper over the drawn nodes' fires.</summary>
    [ObservableProperty]
    private bool _isStepMode;

    /// <summary>Step needs a run to take its ticks from.</summary>
    public bool CanStep => HasLiveRun;

    /// <summary>The toggle's tooltip: what it does, or why it is off.</summary>
    public string StepTip => HasLiveRun
        ? "Step the playhead from fire to fire over the drawn nodes: . forward, , back"
        : "Step is off: no run yet";

    /// <summary>The union of the drawn nodes' fire ticks for the selected instance, sorted; empty without a run.</summary>
    public IReadOnlyList<int> StepTicks => _stepTicks ??= ComputeStepTicks();

    /// <summary>The wires haloed by the last step, until the flash clears.</summary>
    public IReadOnlyList<CanvasConnectionViewModel> FlashingWires => _flashing;

    /// <summary>
    ///     Runs an action after a delay on the UI thread and answers what cancels it. The default posts
    ///     through the host after a <see cref="Task.Delay(TimeSpan)" />; a test substitutes a clock it drives.
    /// </summary>
    public Func<TimeSpan, Action, IDisposable> FlashScheduler
    {
        get => _flashScheduler ?? DelayedPost;
        set => _flashScheduler = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The first fire after the playhead, over every drawn node. The <c>.</c> key.</summary>
    [RelayCommand]
    private void StepForward() => Step(true);

    /// <summary>The last fire before the playhead. The <c>,</c> key.</summary>
    [RelayCommand]
    private void StepBack() => Step(false);

    private void Step(bool forward)
    {
        if (!IsStepMode || LiveRun is null)
        {
            return;
        }

        int at = CurrentTick;
        IReadOnlyList<int> ticks = StepTicks;
        int[] sorted = ticks as int[] ?? [.. ticks];
        int index = LiveLookup.IndexAtOrBefore(sorted, forward ? at : at - 1);
        int? to = forward
            ? index + 1 < sorted.Length ? sorted[index + 1] : null
            : index >= 0 ? sorted[index] : null;
        if (to is not { } tick)
        {
            return;
        }

        SeekTo(tick);
        Flash(tick);
    }

    // Every wire that fired at the tick: its own track's ticks, or its target's fires when it has no edge of its own.
    private void Flash(int tick)
    {
        ClearFlash();
        if (LiveRun is not { } run || SelectedInstance is not { } instance)
        {
            return;
        }

        foreach (CanvasConnectionViewModel wire in Connections)
        {
            if (FiresAt(run, wire, instance.Index, tick))
            {
                wire.IsFlashing = true;
                _flashing.Add(wire);
            }
        }

        if (_flashing.Count > 0)
        {
            _flashTimer = FlashScheduler(FlashDuration, ClearFlash);
        }
    }

    private void ClearFlash()
    {
        _flashTimer?.Dispose();
        _flashTimer = null;
        foreach (CanvasConnectionViewModel wire in _flashing)
        {
            wire.IsFlashing = false;
        }

        _flashing.Clear();
    }

    private static bool FiresAt(LiveValuesRun run, CanvasConnectionViewModel wire, int instance, int tick)
    {
        if (WireTrackFor(run, wire, instance) is { } own)
        {
            return Holds(own.Ticks.Ticks, tick);
        }

        return wire.Target.Owner is { } target && TrackFor(run, target.Key, instance) is { } track && Holds(track.Series.Ticks, tick);
    }

    private static bool Holds(IReadOnlyList<int> ticks, int tick)
    {
        int[] sorted = ticks as int[] ?? [.. ticks];
        int index = LiveLookup.IndexAtOrBefore(sorted, tick);
        return index >= 0 && sorted[index] == tick;
    }

    private int[] ComputeStepTicks()
    {
        if (LiveRun is not { } run || SelectedInstance is not { } instance)
        {
            return [];
        }

        SortedSet<int> union = [];
        foreach (CanvasNodeViewModel node in Nodes)
        {
            if (TrackFor(run, node.Key, instance.Index) is { } track)
            {
                union.UnionWith(track.Series.Ticks);
            }
        }

        return [.. union];
    }

    private Cancel DelayedPost(TimeSpan after, Action action)
    {
        CancellationTokenSource cts = new();
        _ = Task.Delay(after, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _post(action);
            }
        }, TaskScheduler.Default);
        return new Cancel(cts);
    }

    private void NotifyStep()
    {
        OnPropertyChanged(nameof(CanStep));
        OnPropertyChanged(nameof(StepTip));
    }

    partial void OnIsStepModeChanged(bool value)
    {
        if (!value)
        {
            ClearFlash();
        }
    }

    private sealed class Cancel(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
