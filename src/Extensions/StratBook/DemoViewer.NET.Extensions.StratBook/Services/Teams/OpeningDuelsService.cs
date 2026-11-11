#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     Builds a team's <see cref="OpeningDuelSet" /> from Round Facts and the round positions files: per map and
///     side, every round's opening duel (<see cref="OpeningTendenciesService.OpeningDuel" />) and the team's first
///     death, with whether a lost duel was traded, whether the round was won, and where the team's player stood.
///     A round counts when Team Identity puts the team on that side in it; no demo is opened.
///     <para>
///         <b>Position:</b> the exact spot the round's captures hold for the kill
///         (<see cref="ExactSpot" />) when the demo has captures; otherwise the 1 Hz sample at or before the kill
///         (<see cref="SpotAt" />), up to a second early. <b>Trade:</b> the captured trade flag
///         (<see cref="CapturedTrade" />) when the demo has captures; otherwise the killer dying to a teammate of
///         the victim within <see cref="TradeWindowSeconds" /> of the death. The two are compared on every
///         capture-backed death (<see cref="OpeningDuelSet.TradeCompared" />).
///     </para>
///     <para>Stateless and safe off the UI thread.</para>
/// </summary>
public static class OpeningDuelsService
{
    /// <summary>Seconds after a death in which the killer dying to the victim's teammate counts as a trade.</summary>
    public const double TradeWindowSeconds = 5;

    /// <summary>Earlier samples tried when the one at or before the kill does not hold the player.</summary>
    public const int PositionLookbackSteps = 1;

    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    /// <summary>The team's opening duels. Reads records, rows and positions files: call it off the UI thread.</summary>
    /// <param name="teams">Team Identity: the team's demos and its side per round.</param>
    /// <param name="library">The library: map, hash and roster per demo.</param>
    /// <param name="roundFacts">The Round Facts rows per demo.</param>
    /// <param name="positions">The round positions files; null draws no marks.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="captures">The round captures, for exact positions and the trade flag; null uses the sample.</param>
    public static OpeningDuelSet Build(TeamIdentityService teams, IExtensionLibrary library, IRoundFacts roundFacts,
        PositionsReader? positions, Guid teamId, RoundCapturesReader? captures = null)
    {
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(roundFacts);
        Dictionary<(string Map, int Side), Cell> cells = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int read = 0;
        int withoutPositions = 0;

        foreach ((DemoRef demo, _, _) in teams.SidesOf(teamId))
        {
            if (!seen.Add(demo.Path)
                || library.Find(demo.Path) is not { MapName: { Length: > 0 } map } record
                || roundFacts.TryGet(demo.Path) is not { } rows)
            {
                continue;
            }

            read++;
            RoundPositionsDocument? file = positions?.Read(demo.Path, map, record.Sha256);
            if (file is null)
            {
                withoutPositions++;
            }

            DemoCaptures? captured = captures?.Read(demo.Path, map, record.Sha256);
            int rate = rows.Clock?.TickRate is > 0 and var r ? r : FallbackTickRate;
            Dictionary<int, (string Key, string Name)> players = [];
            foreach (LibraryPlayer player in library.Detail(demo.Path)?.Players ?? [])
            {
                string name = DisplayText.Sanitize(player.Name);
                string key = player.SteamId64 != 0 ? player.SteamId64.ToString(CultureInfo.InvariantCulture) : name;
                players[player.Slot] = (key, name);
            }

            foreach (RoundFacts round in rows.Rounds.OrderBy(x => x.Number))
            {
                if (!round.IsLive || teams.SideAtRound(demo.Path, teamId, round.Number) is not { } side)
                {
                    continue;
                }

                if (!cells.TryGetValue((map, side), out Cell? cell))
                {
                    cell = new Cell();
                    cells[(map, side)] = cell;
                }

                cell.Add(new Demo(demo.Path, record.Sha256, rate, players, file, captured), round, side);
            }
        }

        return new OpeningDuelSet
        {
            Blocks =
            [
                .. cells.OrderBy(c => c.Key.Map, StringComparer.Ordinal).ThenBy(c => c.Key.Side)
                    .Select(c => c.Value.ToBlock(c.Key.Map, c.Key.Side))
            ],
            DemosRead = read,
            DemosWithoutPositions = withoutPositions
        };
    }

    /// <summary>The team's first death to an enemy in the round, or null when no enemy killed one of them.</summary>
    /// <param name="round">The round.</param>
    /// <param name="side">The team's side: 2 = T, 3 = CT.</param>
    public static KillStep? FirstDeath(RoundFacts round, int side)
    {
        ArgumentNullException.ThrowIfNull(round);
        HashSet<int> ours = [.. (side == 3 ? round.Ct : round.T).Slots];
        HashSet<int> theirs = [.. (side == 3 ? round.T : round.Ct).Slots];
        return round.Kills.OrderBy(k => k.Tick)
            .FirstOrDefault(k => ours.Contains(k.VictimSlot) && theirs.Contains(k.AttackerSlot));
    }

    /// <summary>
    ///     True when <paramref name="death" />'s killer died to a teammate of the victim within
    ///     <see cref="TradeWindowSeconds" />. A death with no killer is never traded.
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="death">The death.</param>
    /// <param name="victimSide">The victim's side: 2 = T, 3 = CT.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static bool IsTraded(RoundFacts round, KillStep death, int victimSide, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(death);
        if (death.AttackerSlot < 0)
        {
            return false;
        }

        HashSet<int> mates = [.. (victimSide == 3 ? round.Ct : round.T).Slots];
        mates.Remove(death.VictimSlot);
        double window = TradeWindowSeconds * tickRate;
        return round.Kills.Any(k => !ReferenceEquals(k, death)
                                    && k.VictimSlot == death.AttackerSlot
                                    && mates.Contains(k.AttackerSlot)
                                    && k.Tick >= death.Tick
                                    && k.Tick - death.Tick <= window);
    }

    /// <summary>
    ///     The captured kill matching a Round Facts kill (same victim and killer), or null without captures.
    /// </summary>
    /// <param name="capture">The round's captures, or null.</param>
    /// <param name="kill">The Round Facts kill.</param>
    public static CapturedKill? CapturedOf(RoundCaptureView? capture, KillStep kill)
    {
        ArgumentNullException.ThrowIfNull(kill);
        return capture?.Kills.FirstOrDefault(k => k.VictimSlot == kill.VictimSlot
                                                  && k.AttackerSlot == kill.AttackerSlot);
    }

    /// <summary>
    ///     The exact spot of the killer or the victim of a kill, from the round's captures; null when the demo has
    ///     none, the kill is not in them or they hold no spot for that player.
    /// </summary>
    /// <param name="capture">The round's captures, or null.</param>
    /// <param name="kill">The Round Facts kill.</param>
    /// <param name="victim">True for the victim's spot, false for the killer's.</param>
    public static OpeningSpot? ExactSpot(RoundCaptureView? capture, KillStep kill, bool victim)
    {
        CaptureSpot? spot = CapturedOf(capture, kill) is { } k ? victim ? k.Victim : k.Attacker : null;
        return spot is null ? null : new OpeningSpot(spot.X, spot.Y, spot.Z, spot.Place, true);
    }

    /// <summary>
    ///     Whether the captures say a death was traded: some captured kill is flagged as a trade of the victim.
    ///     Null without captures or when the death is not in them.
    /// </summary>
    /// <param name="capture">The round's captures, or null.</param>
    /// <param name="death">The Round Facts death.</param>
    public static bool? CapturedTrade(RoundCaptureView? capture, KillStep death)
    {
        if (CapturedOf(capture, death) is null)
        {
            return null;
        }

        return capture!.Kills.Any(k => k.Trade && k.TradedSlot == death.VictimSlot);
    }

    /// <summary>
    ///     Where a player stood at a tick: the positions sample at or before it, or up to
    ///     <see cref="PositionLookbackSteps" /> earlier when that one does not hold the player. Null without the
    ///     round, before freeze end, or when no tried sample holds the player. The one place positions are looked
    ///     up, so exact captures can replace it.
    /// </summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="roundNumber">The Round Facts round number.</param>
    /// <param name="slot">The player's slot.</param>
    /// <param name="tick">Frame clock.</param>
    public static OpeningSpot? SpotAt(RoundPositionsDocument? positions, int roundNumber, int slot, int tick)
    {
        if (positions?.Round(roundNumber) is not { } stored || positions.CadenceTicks <= 0
                                                          || tick < stored.FreezeEndTick || stored.Pos.Count == 0)
        {
            return null;
        }

        int step = Math.Min(positions.StepFor(stored, tick), stored.Pos.Count - 1);
        for (int s = step; s >= Math.Max(0, step - PositionLookbackSteps); s--)
        {
            foreach (RoundPosition p in stored.At(s))
            {
                if (p.Slot == slot)
                {
                    return new OpeningSpot(p.X, p.Y, p.Z, positions.PlaceOf(p.PlaceId));
                }
            }
        }

        return null;
    }

    private sealed record Demo(
        string Path,
        string? Sha256,
        int Rate,
        IReadOnlyDictionary<int, (string Key, string Name)> Players,
        RoundPositionsDocument? Positions,
        DemoCaptures? Captures = null)
    {
        public (string Key, string Name) Player(int slot) =>
            Players.TryGetValue(slot, out (string Key, string Name) p) && p.Name.Length > 0
                ? p
                : ($"slot {slot}", $"slot {slot}");

        // The clip for a round, clamped to the live window: never before freeze end, never past the end.
        public TendencyRound Clip(RoundFacts round, int from, int to)
        {
            int start = Math.Max(round.FreezeEndTick, from);
            int end = round.EndTick is { } e ? Math.Min(e, to) : to;
            return new TendencyRound(Path, Sha256, round.Number, start, Math.Max(start, end), Rate);
        }

        public TendencyRound Around(RoundFacts round, int tick) =>
            Clip(round, tick - OpeningTendenciesService.ClipLeadSeconds * Rate,
                tick + OpeningTendenciesService.ClipTailSeconds * Rate);
    }

    private sealed class Cell
    {
        private readonly List<OpeningDuelRound> _duels = [];
        private readonly List<OpeningDeathRound> _firstDeaths = [];
        private readonly List<TendencyRound> _rounds = [];
        private readonly HashSet<string> _exactDemos = new(StringComparer.Ordinal);
        private readonly HashSet<string> _sampledDemos = new(StringComparer.Ordinal);
        private int _positionRounds;
        private int _tradeCompared;
        private int _tradeDisagreed;

        // The captured flag when the round has one (counting how often the 5 s rule disagrees), else the rule.
        private bool Traded(RoundCaptureView? capture, RoundFacts round, KillStep death, int side, int rate,
            bool count = true)
        {
            bool heuristic = IsTraded(round, death, side, rate);
            if (CapturedTrade(capture, death) is not { } captured)
            {
                return heuristic;
            }

            if (count)
            {
                _tradeCompared++;
                if (captured != heuristic)
                {
                    _tradeDisagreed++;
                }
            }

            return captured;
        }

        // Exact when the round has captures and they hold the spot, else the 1 Hz sample.
        private OpeningSpot? Spot(Demo demo, RoundCaptureView? capture, RoundFacts round, KillStep kill, int slot,
            bool victim)
        {
            if (ExactSpot(capture, kill, victim) is { } exact)
            {
                _exactDemos.Add(demo.Path);
                return exact;
            }

            OpeningSpot? sampled = SpotAt(demo.Positions, round.Number, slot, kill.Tick);
            if (sampled is not null)
            {
                _sampledDemos.Add(demo.Path);
            }

            return sampled;
        }

        public void Add(Demo demo, RoundFacts round, int side)
        {
            RoundCaptureView? capture = demo.Captures?.Round(round.Number);
            _rounds.Add(demo.Clip(round, round.FreezeEndTick,
                round.FreezeEndTick + OpeningTendenciesService.ClockCapSeconds * demo.Rate));
            if (demo.Positions?.Round(round.Number) is not null)
            {
                _positionRounds++;
            }

            bool won = round.WinnerSide == side;
            (KillStep Kill, int Slot, bool Won)? opening = OpeningTendenciesService.OpeningDuel(round, side);
            if (opening is { } duel)
            {
                (string key, string name) = demo.Player(duel.Slot);
                int tick = duel.Kill.Tick;
                _duels.Add(new OpeningDuelRound(
                    demo.Around(round, tick),
                    demo.Clip(round, tick - OpeningTendenciesService.ClipLeadSeconds * demo.Rate,
                        round.EndTick ?? tick + OpeningTendenciesService.ClockCapSeconds * demo.Rate),
                    key,
                    name,
                    duel.Won,
                    !duel.Won && Traded(capture, round, duel.Kill, side, demo.Rate),
                    won,
                    Spot(demo, capture, round, duel.Kill, duel.Slot, !duel.Won),
                    round.IsDecided()));
            }

            if (FirstDeath(round, side) is { } death)
            {
                // The same death as a lost opening duel was already compared there.
                bool isOpening = opening is { Won: false } lost && ReferenceEquals(lost.Kill, death);
                _firstDeaths.Add(new OpeningDeathRound(
                    demo.Around(round, death.Tick),
                    demo.Player(death.VictimSlot).Name,
                    isOpening,
                    Traded(capture, round, death, side, demo.Rate, !isOpening),
                    Spot(demo, capture, round, death, death.VictimSlot, true)));
            }
        }

        public OpeningDuelBlock ToBlock(string map, int side) => new()
        {
            Map = map,
            Side = side,
            Rounds = new TendencyBucket("rounds", [.. _rounds]),
            PositionRounds = _positionRounds,
            ExactDemos = _exactDemos.Count,
            SampledDemos = _sampledDemos.Except(_exactDemos).Count(),
            TradeCompared = _tradeCompared,
            TradeDisagreed = _tradeDisagreed,
            Duels = [.. _duels],
            FirstDeaths = [.. _firstDeaths]
        };
    }
}
