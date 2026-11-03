#region

using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

#endregion

namespace DemoViewer.NET.TestSupport;

/// <summary>
///     Builds <see cref="GameEvent" /> fixtures over the SDK's payload records.
/// </summary>
/// <remarks>
///     <para>
///         Fixtures used to construct the parser's own generated records positionally:
///         <c>new PlayerDeathEvent("player_death", -1, 0, 0, 0, -1, -1, -1, false, "ak47", …)</c>,
///         twenty-seven arguments deep. Two things changed that. The payload records come from the
///         SDK now and every property is <c>required init</c>, so an object initialiser has to name
///         all of them; and the per-fire transport context moved out to the envelope.
///     </para>
///     <para>
///         Writing that out at each call site would bury the one or two fields a test actually
///         asserts on under twenty defaults. These factories carry the defaults and take only what
///         the test cares about, so a fixture reads as the thing it is testing.
///     </para>
/// </remarks>
public static class TestGameEvents
{
    /// <summary>A <c>round_freeze_end</c> fire. The payload declares no fields.</summary>
    public static GameEvent RoundFreezeEnd(
        int frameNumber = 0, int serverTick = 0, int gameTick = 0, int eventId = -1) =>
        new("round_freeze_end", eventId, frameNumber, serverTick, gameTick, new RoundFreezeEndEvent());

    /// <summary>A <c>round_officially_ended</c> fire. The payload declares no fields.</summary>
    public static GameEvent RoundOfficiallyEnded(
        int frameNumber = 0, int serverTick = 0, int gameTick = 0, int eventId = -1) =>
        new("round_officially_ended", eventId, frameNumber, serverTick, gameTick,
            new RoundOfficiallyEndedEvent());
}
