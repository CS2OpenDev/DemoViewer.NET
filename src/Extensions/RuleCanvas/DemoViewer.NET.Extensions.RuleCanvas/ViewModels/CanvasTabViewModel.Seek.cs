#region

using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Seek to a fire: the node and wire menus, and the ] and [ keys with a node or wire selected, move the
///     shell's playhead to the next, previous or first fire of the selected instance's track. An event
///     node needs no run: its items go through the host's event navigator with the node's wire events.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    private const string NextVerb = "Next fire";
    private const string PreviousVerb = "Previous fire";
    private const string FirstVerb = "First fire";
    private const string NextEventVerb = "Next event";
    private const string PreviousEventVerb = "Previous event";

    /// <summary>The ticks the selection stands for in the run, as the menu will read them.</summary>
    public int CurrentTick => _currentTick?.Invoke() ?? _moduleContext?.CurrentTick ?? 0;

    /// <summary>The first selected node, else the first selected wire, else null.</summary>
    public object? SeekTarget() =>
        SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault() ?? (object?)SelectedWires.OfType<CanvasConnectionViewModel>().FirstOrDefault();

    /// <summary>Seeks to the first fire after the playhead on <paramref name="target" />, or on the selection when null.</summary>
    [RelayCommand]
    private void NextFire(object? target) => Seek(target ?? SeekTarget(), SeekDirection.Next);

    /// <summary>Seeks to the last fire before the playhead.</summary>
    [RelayCommand]
    private void PreviousFire(object? target) => Seek(target ?? SeekTarget(), SeekDirection.Previous);

    /// <summary>Seeks to the first fire of the track.</summary>
    [RelayCommand]
    private void FirstFire(object? target) => Seek(target ?? SeekTarget(), SeekDirection.First);

    /// <summary>
    ///     Fills the seek items of <paramref name="target" />'s menu for the run, the instance and the playhead
    ///     as they are now. Called as the menu opens.
    /// </summary>
    public void RefreshSeekMenu(object? target)
    {
        CanvasSeekMenuViewModel? menu = (target as CanvasNodeViewModel)?.SeekMenu ?? (target as CanvasConnectionViewModel)?.SeekMenu;
        if (menu is null)
        {
            return;
        }

        if (target is CanvasNodeViewModel { Ruleset: "~event" } node)
        {
            string? reason = EventReason(node);
            menu.Set(LiveValuesText.SeekItem(NextEventVerb, reason, false), reason is null,
                LiveValuesText.SeekItem(PreviousEventVerb, reason, false), reason is null,
                LiveValuesText.SeekItem(FirstVerb, "events are not tracked", false), false);
            return;
        }

        (TickSource? ticks, string? blocked) = TicksOf(target);
        if (ticks is null)
        {
            menu.Set(LiveValuesText.SeekItem(NextVerb, blocked, false), false,
                LiveValuesText.SeekItem(PreviousVerb, blocked, false), false,
                LiveValuesText.SeekItem(FirstVerb, blocked, false), false);
            return;
        }

        int at = CurrentTick;
        int? next = ticks.After(at);
        int? previous = ticks.Before(at);
        int? first = ticks.First;
        bool sampled = ticks.IsSampled;
        menu.Set(
            LiveValuesText.SeekItem(NextVerb, next is null ? Inv($"none after tick {at}") : null, sampled), next is not null,
            LiveValuesText.SeekItem(PreviousVerb, previous is null ? Inv($"none before tick {at}") : null, sampled), previous is not null,
            LiveValuesText.SeekItem(FirstVerb, first is null ? "never fired" : null, sampled), first is not null);
    }

    private void Seek(object? target, SeekDirection direction)
    {
        if (target is CanvasNodeViewModel { Ruleset: "~event" } node)
        {
            if (EventReason(node) is not null || direction == SeekDirection.First)
            {
                return;
            }

            IReadOnlyCollection<string> names = node.Events.Count > 0 ? node.Events : [node.Id];
            if (direction == SeekDirection.Next)
            {
                _moduleContext!.RequestNextEvent(names);
            }
            else
            {
                _moduleContext!.RequestPrevEvent(names);
            }

            return;
        }

        (TickSource? ticks, _) = TicksOf(target);
        if (ticks is null)
        {
            return;
        }

        int at = CurrentTick;
        int? tick = direction switch
        {
            SeekDirection.Next => ticks.After(at),
            SeekDirection.Previous => ticks.Before(at),
            _ => ticks.First
        };
        if (tick is { } to)
        {
            SeekTo(to);
        }
    }

    // The shell's clock when the host hands one, the module context's otherwise.
    private void SeekTo(int tick)
    {
        if (_seekToTick is not null)
        {
            _seekToTick(tick);
        }
        else
        {
            _moduleContext?.RequestSeekToTick(tick);
        }
    }

    // Why an event node's items are off: the host has no navigator here, or no demo is open.
    private string? EventReason(CanvasNodeViewModel node) =>
        _moduleContext is null ? "no host navigator"
        : !HasDemo && !_moduleContext.HasDemo ? "no demo open"
        : node.Events.Count == 0 && node.Id.Length == 0 ? "no event names"
        : null;

    // The fire ticks behind a node or a wire for the selected instance, or why there are none.
    private (TickSource? Ticks, string? Reason) TicksOf(object? target)
    {
        if (!HasDemo)
        {
            return (null, "no demo open");
        }

        if (LiveRun is not { } run || SelectedInstance is not { } instance)
        {
            return (null, "no run yet");
        }

        switch (target)
        {
            case CanvasNodeViewModel node:
                return TrackFor(run, node.Key, instance.Index) is { } track
                    ? (new TickSource(track.Series.Ticks, track.Series.IsSampled), null)
                    : (null, "no data");
            case CanvasConnectionViewModel wire:
                if (WireTrackFor(run, wire, instance.Index) is { } own)
                {
                    return (new TickSource(own.Ticks.Ticks, own.Ticks.IsSampled), null);
                }

                // A wire with no edge of its own fires when its target does.
                return wire.Target.Owner is { } owner && TrackFor(run, owner.Key, instance.Index) is { } targetTrack
                    ? (new TickSource(targetTrack.Series.Ticks, targetTrack.Series.IsSampled), null)
                    : (null, "no data");
            default:
                return (null, "nothing selected");
        }
    }

    private static string Inv(FormattableString text) => text.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private enum SeekDirection
    {
        Next,
        Previous,
        First
    }

    // A sorted tick list, whichever bounded list it came from.
    private sealed class TickSource(IReadOnlyList<int> ticks, bool sampled)
    {
        private readonly int[] _ticks = [.. ticks];

        public bool IsSampled { get; } = sampled;

        public int? First => _ticks.Length > 0 ? _ticks[0] : null;

        public int? After(int tick)
        {
            int index = LiveLookup.IndexAtOrBefore(_ticks, tick) + 1;
            return index < _ticks.Length ? _ticks[index] : null;
        }

        public int? Before(int tick)
        {
            int index = LiveLookup.IndexAtOrBefore(_ticks, tick - 1);
            return index >= 0 ? _ticks[index] : null;
        }
    }
}
