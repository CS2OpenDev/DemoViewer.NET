#region

using System.Collections.ObjectModel;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace DemoViewer.NET.Debugging;

/// <summary>
///     Holds the set of active breakpoints, decides whether navigation should halt
///     at a given frame, and exposes the most-recently-hit breakpoint for UI.
///     Stateless w.r.t. parse: the actual seek/step is driven by MainViewModel;
///     this service just answers "stop here?"
/// </summary>
public sealed class DebuggerService
{
    // What the parser check walks. The check runs on the entity seek's pool thread while the panel edits
    // Breakpoints on the UI thread, so it reads a copy replaced on every edit rather than the live list.
    private Breakpoint[] _parserSnapshot = [];
    private volatile bool _suppress;

    /// <summary>Creates an empty debugger.</summary>
    public DebuggerService()
    {
        Breakpoints.CollectionChanged += (_, _) => Volatile.Write(ref _parserSnapshot, [.. Breakpoints]);
    }

    /// <summary>All breakpoints, in insertion order. UI lists bind to this.</summary>
    public ObservableCollection<Breakpoint> Breakpoints { get; } = [];

    /// <summary>The breakpoint that most recently halted execution; null when running or never stopped.</summary>
    public Breakpoint? LastHit { get; private set; }

    /// <summary>
    ///     Frame index at the moment <see cref="LastHit" /> was set, when known. Tier 1 frame/tick/
    ///     event hits set this naturally (the matching frame IS the hit point). Tier 3 parser hits
    ///     set this via the optional <c>currentFrameIndex</c> arg on <see cref="CheckParserState" />:
    ///     the parser is iterating frames during seek when the hit fires, and the index is captured
    ///     so the UI can "Jump to" the right frame even though the seek continues past it.
    ///     -1 when no hit, or when the hit had no associated frame.
    /// </summary>
    public int LastHitFrameIndex { get; private set; } = -1;

    /// <summary>
    ///     Suppression flag: when true, all CheckFrame/CheckParserState calls return null
    ///     without recording hits. Used by the UI when auto-navigating to a captured hit frame
    ///     so the back-navigation seek doesn't re-trigger the same breakpoint.
    /// </summary>
    public bool Suppress
    {
        get => _suppress;
        set => _suppress = value;
    }

    // ── Add / remove ─────────────────────────────────────────────────────────

    /// <summary>Add.</summary>
    public void Add(BreakpointKind kind, int intValue = 0, string? stringValue = null) =>
        Breakpoints.Add(new Breakpoint { Kind = kind, IntValue = intValue, StringValue = stringValue });

    // ── "Stop here?" predicates ──────────────────────────────────────────────
    //
    // These are called by the navigation driver (MainViewModel.Continue / StepFrame).
    // Each returns the first matching breakpoint, or null if no match. The caller
    // is responsible for halting + updating UI.

    /// <summary>
    ///     Test whether arriving at <paramref name="frame" /> should trip a frame/tick/event
    ///     breakpoint. The caller should call this BEFORE advancing past the frame.
    /// </summary>
    public Breakpoint? CheckFrame(DemoFrame frame)
    {
        if (Suppress)
        {
            return null;
        }

        foreach (Breakpoint bp in Breakpoints)
        {
            if (!bp.Enabled)
            {
                continue;
            }

            bool hit = bp.Kind switch
            {
                BreakpointKind.FrameNumber => frame.FrameNumber == bp.IntValue,
                BreakpointKind.TickNumber => frame.ServerTick == bp.IntValue,
                BreakpointKind.GameEventName => FrameContainsEvent(frame, bp.StringValue),
                BreakpointKind.RoundTransition => FrameContainsEvent(frame, "round_start")
                    || FrameContainsEvent(frame, "round_end"),
                _ => false
            };

            if (hit)
            {
                bp.HitCount++;
                LastHit = bp;
                LastHitFrameIndex = frame.FrameNumber;
                StateChanged?.Invoke();
                return bp;
            }
        }

        return null;
    }

    /// <summary>
    ///     Test whether the entity tracker reaching a given (packet#, error-state) tuple
    ///     should trip a Tier 3 parser breakpoint. Called from MainViewModel's tracker
    ///     PacketProcessed handler: <paramref name="currentFrameIndex" /> is the frame
    ///     the tracker is currently iterating over (passed so the UI can later jump there).
    /// </summary>
    public void CheckParserState(int packetCount, bool hasNewDecodeError, int newDeltaUnknownDelta,
        int currentFrameIndex)
    {
        if (MatchParserState(packetCount, hasNewDecodeError, newDeltaUnknownDelta) is { } bp)
        {
            RecordParserHit(bp, currentFrameIndex);
        }
    }

    /// <summary>
    ///     The Tier 3 breakpoint the tracker's state trips, without recording it. Safe on any thread:
    ///     it reads the snapshot, never the live list.
    /// </summary>
    internal Breakpoint? MatchParserState(int packetCount, bool hasNewDecodeError, int newDeltaUnknownDelta)
    {
        if (Suppress)
        {
            return null;
        }

        foreach (Breakpoint bp in Volatile.Read(ref _parserSnapshot))
        {
            if (!bp.Enabled)
            {
                continue;
            }

            bool hit = bp.Kind switch
            {
                BreakpointKind.PacketIndex => packetCount == bp.IntValue,
                BreakpointKind.ParserDecodeError => hasNewDecodeError,
                BreakpointKind.DeltaOnUnknown => newDeltaUnknownDelta > 0,
                _ => false
            };

            if (hit)
            {
                return bp;
            }
        }

        return null;
    }

    /// <summary>
    ///     Records a hit <see cref="MatchParserState" /> found. UI thread only: it raises
    ///     <see cref="StateChanged" />, which the panel turns into bound property changes. Dropped when
    ///     suppression came on between the match and now, so a hit queued from a replay cannot overwrite
    ///     the stop a command has just recorded.
    /// </summary>
    internal void RecordParserHit(Breakpoint bp, int currentFrameIndex)
    {
        if (Suppress)
        {
            return;
        }

        bp.HitCount++;
        LastHit = bp;
        LastHitFrameIndex = currentFrameIndex;
        StateChanged?.Invoke();
    }

    /// <summary>Clear.</summary>
    public void Clear() => Breakpoints.Clear();

    /// <summary>
    ///     Clear <see cref="LastHit" />; called by Continue to indicate execution is running again.
    /// </summary>
    public void Continue()
    {
        if (LastHit is null)
        {
            return;
        }

        LastHit = null;
        LastHitFrameIndex = -1;
        StateChanged?.Invoke();
    }

    /// <summary>Remove.</summary>
    public void Remove(int id)
    {
        for (int i = 0; i < Breakpoints.Count; i++)
        {
            if (Breakpoints[i].Id == id)
            {
                Breakpoints.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>Fired after <see cref="LastHit" /> changes (including to null when continued).</summary>
    public event Action? StateChanged;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool FrameContainsEvent(DemoFrame frame, string? eventName)
    {
        if (string.IsNullOrEmpty(eventName))
        {
            return false;
        }

        foreach (NetMessage msg in frame.InnerMessages)
        {
            // GameEventMessage IS-A NetMessage (enrichment pass 3 replaces the raw slot).
            // Its DecodedEvent.Name carries the event name like "player_death".
            if (msg is GameEventMessage gem
                && string.Equals(gem.DecodedEvent.Name, eventName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
