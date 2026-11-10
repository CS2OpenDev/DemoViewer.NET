#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>One opening duel a player took: the round, the side and whether the player won it.</summary>
/// <param name="Round">The clip around the duel.</param>
/// <param name="Side">2 = T, 3 = CT.</param>
/// <param name="Won">The player got the kill.</param>
public sealed record PlayerOpening(TendencyRound Round, int Side, bool Won);

/// <summary>One round the player was left the last alive of their side with at least one enemy standing.</summary>
/// <param name="Round">The clip from the moment the player was left alone to the round's end.</param>
/// <param name="Versus">Enemies alive at that moment.</param>
/// <param name="Won">The player's side won the round.</param>
public sealed record PlayerClutch(TendencyRound Round, int Versus, bool Won);

/// <summary>
///     One player's numbers over a set of demos (one map, or every map). The rates read the <c>player_match</c>
///     facts and are null when no demo had them; opening duels and clutches read Round Facts and need no facts.
/// </summary>
public sealed class PlayerStatLine
{
    private readonly List<PlayerClutch> _clutches = [];
    private readonly List<string> _demoKeys = [];
    private readonly List<PlayerOpening> _openings = [];

    public required string SteamId { get; init; }

    /// <summary>The demos in scope that hold the player.</summary>
    public int Demos { get; private set; }

    /// <summary>The demos whose <c>player_match</c> row for the player was read.</summary>
    public int WithFacts { get; private set; }

    /// <summary>The demos whose facts are not written yet (the library computes them in the background).</summary>
    public int Pending { get; private set; }

    /// <summary>The demos whose facts ruleset failed to run.</summary>
    public int Failed { get; private set; }

    /// <summary>The demos whose K/D came from Round Facts kills because no facts row was there.</summary>
    public int KdFromRoundFacts { get; private set; }

    public double Rounds { get; private set; }

    private double RatingRounds { get; set; }

    private double RatingSum { get; set; }

    private double DamageRounds { get; set; }

    private double Damage { get; set; }

    private double KastRounds { get; set; }

    public int Kills { get; private set; }

    public int Deaths { get; private set; }

    /// <summary>AWP kills, over the demos with facts.</summary>
    public int AwpKills { get; private set; }

    /// <summary>Rounds-weighted mean of the stored HLTV 2.0 rating; null without facts.</summary>
    public double? Rating => RatingRounds > 0 ? RatingSum / RatingRounds : null;

    /// <summary>Enemy damage per round; null without facts.</summary>
    public double? Adr => DamageRounds > 0 ? Damage / DamageRounds : null;

    /// <summary>KAST as a fraction; null without facts.</summary>
    public double? Kast => DamageRounds > 0 ? Math.Min(KastRounds, DamageRounds) / DamageRounds : null;

    /// <summary>Kills over deaths; null with no kill or death counted.</summary>
    public double? KillDeath => Kills + Deaths == 0 ? null : Deaths == 0 ? Kills : (double)Kills / Deaths;

    public IReadOnlyList<PlayerOpening> Openings => _openings;

    public IReadOnlyList<PlayerClutch> Clutches => _clutches;

    /// <summary>Each demo holding the player once, as a hash when it has one, else its path: a Player Overview scope.</summary>
    public IReadOnlyList<string> DemoKeys => _demoKeys;

    internal void AddDemo(string key)
    {
        Demos++;
        _demoKeys.Add(key);
    }

    internal void AddFacts(IReadOnlyDictionary<string, double> row)
    {
        WithFacts++;
        double rounds = row.GetValueOrDefault(PlayerStatsService.RoundsColumn);
        if (rounds <= 0)
        {
            return;
        }

        Rounds += rounds;
        if (row.TryGetValue("HLTV", out double hltv))
        {
            RatingSum += hltv * rounds;
            RatingRounds += rounds;
        }

        DamageRounds += rounds;
        Damage += row.GetValueOrDefault("EnemyDmg");
        KastRounds += row.GetValueOrDefault("KAST%") / 100 * rounds;
        AwpKills += (int)row.GetValueOrDefault("AWP");
    }

    internal void AddKillDeath(int kills, int deaths, bool fromRoundFacts)
    {
        Kills += kills;
        Deaths += deaths;
        if (fromRoundFacts)
        {
            KdFromRoundFacts++;
        }
    }

    internal void AddStatus(FactStatus status)
    {
        switch (status)
        {
            case FactStatus.Failed:
                Failed++;
                break;
            case FactStatus.Absent:
            case FactStatus.Stale:
                Pending++;
                break;
        }
    }

    internal void Add(PlayerOpening opening) => _openings.Add(opening);

    internal void Add(PlayerClutch clutch) => _clutches.Add(clutch);
}

/// <summary>The roster's <see cref="PlayerStatLine" /> per map and over every map, and the team's AWP kills.</summary>
public sealed class PlayerStatsSet
{
    /// <summary>The map key of the lines pooled over every map.</summary>
    public const string AllMaps = "";

    private readonly Dictionary<(string Map, string SteamId), PlayerStatLine> _lines = [];
    private readonly Dictionary<string, int> _teamAwp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>False when the host has no <see cref="IAnalysisFacts" />: the rates can never fill.</summary>
    public bool FactsAvailable { get; init; }

    /// <summary>False when the host has no Round Facts: openings and clutches can never fill.</summary>
    public bool RoundFactsAvailable { get; init; }

    /// <summary>The player's line on a map (or <see cref="AllMaps" />), or null when no demo there holds them.</summary>
    /// <param name="map">The map, or <see cref="AllMaps" />.</param>
    /// <param name="steamId">The SteamID64 as text.</param>
    public PlayerStatLine? For(string map, string steamId) =>
        _lines.GetValueOrDefault((Key(map), steamId));

    /// <summary>The roster's AWP kills on a map (or every map), over the demos with facts.</summary>
    /// <param name="map">The map, or <see cref="AllMaps" />.</param>
    public int TeamAwpKills(string map) => _teamAwp.GetValueOrDefault(Key(map));

    internal PlayerStatLine Line(string map, string steamId)
    {
        (string, string) key = (Key(map), steamId);
        if (!_lines.TryGetValue(key, out PlayerStatLine? line))
        {
            line = new PlayerStatLine { SteamId = steamId };
            _lines[key] = line;
        }

        return line;
    }

    internal void AddAwp(string map, int kills) => _teamAwp[Key(map)] = _teamAwp.GetValueOrDefault(Key(map)) + kills;

    private static string Key(string map) => map.ToLowerInvariant();
}

/// <summary>
///     Builds the Scouting Report's players table numbers for a team's roster.
///     <list type="bullet">
///         <item>
///             Rating, ADR, KAST and AWP kills read the core <c>player_match</c> facts the library writes in the
///             background. HLTV is stored only as the final rating, so it pools as a rounds-weighted mean, the
///             Player Overview's rule; ADR sums enemy damage over summed rounds.
///         </item>
///         <item>K/D reads the facts too, and Round Facts kills on a demo the facts have not reached.</item>
///         <item>
///             Opening duels and clutches read Round Facts. A clutch attempt is a round the player is left the
///             last alive of their side while at least one enemy stands; nothing in the facts counts attempts.
///         </item>
///     </list>
///     Reads one facts file and one record per demo: call it off the UI thread.
/// </summary>
public static class PlayerStatsService
{
    /// <summary>The facts output the table reads.</summary>
    public static FactKey PlayerMatch { get; } = new("player_match", "player_match");

    internal const string RoundsColumn = "rounds";

    // HLTV 2.0's constant terms, the ones a second slot of one account adds again (the App's PlayerAggregate).
    private const double HltvConstant = 0.1587 - (0.2372 * 0.41);
    private const double HltvPerKastPercent = 0.73 / 100;

    // A record without a clock header is still a CS2 demo; the frame clock of every build here is 64.
    private const int FallbackTickRate = 64;

    private static readonly string[] _activity = ["Shots", "TotalD", "EnemyDmg"];

    /// <summary>The roster's lines over the team's demos.</summary>
    /// <param name="library">The library: hash and roster per demo.</param>
    /// <param name="facts">The analysis facts; null leaves the rates empty.</param>
    /// <param name="roundFacts">Round Facts; null leaves openings and clutches empty.</param>
    /// <param name="demos">The team's demos.</param>
    /// <param name="steamIds">The roster, SteamID64 as text.</param>
    public static PlayerStatsSet Build(IExtensionLibrary library, IAnalysisFacts? facts, IRoundFacts? roundFacts,
        IReadOnlyList<TeamDemoFacts> demos, IReadOnlyCollection<string> steamIds)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(demos);
        ArgumentNullException.ThrowIfNull(steamIds);
        PlayerStatsSet set = new() { FactsAvailable = facts is not null, RoundFactsAvailable = roundFacts is not null };
        HashSet<string> roster = new(steamIds, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (TeamDemoFacts demo in demos)
        {
            if (!seen.Add(demo.Path) || library.Detail(demo.Path) is not { } detail)
            {
                continue;
            }

            string? sha = library.Find(demo.Path)?.Sha256;
            Dictionary<string, List<int>> slots = new(StringComparer.Ordinal);
            foreach (LibraryPlayer player in detail.Players)
            {
                string id = player.SteamId64.ToString(CultureInfo.InvariantCulture);
                if (player.SteamId64 != 0 && roster.Contains(id))
                {
                    (slots.TryGetValue(id, out List<int>? list) ? list : slots[id] = []).Add(player.Slot);
                }
            }

            if (slots.Count == 0)
            {
                continue;
            }

            FactStatus status = facts?.Status(demo.Path, PlayerMatch) ?? FactStatus.Absent;
            FactTable? table = status is FactStatus.Current or FactStatus.Stale ? facts!.TryGet(demo.Path, PlayerMatch) : null;
            RoundFactsRows? rows = roundFacts?.TryGet(demo.Path);
            int rate = rows?.Clock?.TickRate is > 0 and var r ? r : detail.TickRate > 0 ? detail.TickRate : FallbackTickRate;
            Dictionary<int, string> bySlot = [];
            foreach ((string id, List<int> s) in slots)
            {
                s.ForEach(slot => bySlot[slot] = id);
            }

            foreach ((string id, List<int> playerSlots) in slots)
            {
                IReadOnlyDictionary<string, double>? row = table is null ? null : Merge(table.Rows, id);
                (int kills, int deaths) = row is null ? KillsFromRoundFacts(rows, playerSlots) : (0, 0);
                foreach (string map in (string[])[demo.Map, PlayerStatsSet.AllMaps])
                {
                    PlayerStatLine line = set.Line(map, id);
                    line.AddDemo(sha ?? demo.Path);
                    if (row is not null)
                    {
                        line.AddFacts(row);
                        line.AddKillDeath((int)row.GetValueOrDefault("TotalK"), (int)row.GetValueOrDefault("TotalD"), false);
                        set.AddAwp(map, (int)row.GetValueOrDefault("AWP"));
                    }
                    else
                    {
                        line.AddStatus(table is null ? status : FactStatus.Current);
                        if (rows is not null)
                        {
                            line.AddKillDeath(kills, deaths, true);
                        }
                    }
                }
            }

            if (rows is null)
            {
                continue;
            }

            foreach (RoundFacts round in rows.Rounds.Where(x => x.IsLive).OrderBy(x => x.Number))
            {
                foreach (int side in (int[])[2, 3])
                {
                    if (OpeningTendenciesService.OpeningDuel(round, side) is { } duel
                        && bySlot.TryGetValue(duel.Slot, out string? opener))
                    {
                        PlayerOpening opening = new(Clip(demo.Path, sha, rate, round,
                                duel.Kill.Tick - OpeningTendenciesService.ClipLeadSeconds * rate,
                                duel.Kill.Tick + OpeningTendenciesService.ClipTailSeconds * rate),
                            side, duel.Won);
                        set.Line(demo.Map, opener).Add(opening);
                        set.Line(PlayerStatsSet.AllMaps, opener).Add(opening);
                    }

                    if (Clutch(round, side) is { } clutch && bySlot.TryGetValue(clutch.Slot, out string? alone))
                    {
                        PlayerClutch attempt = new(Clip(demo.Path, sha, rate, round,
                                clutch.Tick - OpeningTendenciesService.ClipLeadSeconds * rate,
                                round.EndTick ?? clutch.Tick + 60 * rate),
                            clutch.Versus, round.WinnerSide == side);
                        set.Line(demo.Map, alone).Add(attempt);
                        set.Line(PlayerStatsSet.AllMaps, alone).Add(attempt);
                    }
                }
            }
        }

        return set;
    }

    /// <summary>
    ///     The moment a side is down to one player with at least one enemy alive: the survivor's slot, the tick
    ///     of the kill that left them alone, and the enemies standing. Null when the side never got there, or the
    ///     survivor cannot be told from the kills (a disconnect).
    /// </summary>
    /// <param name="round">The round.</param>
    /// <param name="side">The side: 2 = T, 3 = CT.</param>
    public static (int Slot, int Tick, int Versus)? Clutch(RoundFacts round, int side)
    {
        ArgumentNullException.ThrowIfNull(round);
        HashSet<int> alive = [.. (side == 3 ? round.Ct : round.T).Slots];
        foreach (KillStep kill in round.Kills.OrderBy(k => k.Tick))
        {
            alive.Remove(kill.VictimSlot);
            int ours = side == 3 ? kill.CtAlive : kill.TAlive;
            int theirs = side == 3 ? kill.TAlive : kill.CtAlive;
            if (ours <= 0)
            {
                return null;
            }

            if (ours == 1 && theirs >= 1)
            {
                return alive.Count == 1 ? (alive.Single(), kill.Tick, theirs) : null;
            }
        }

        return null;
    }

    /// <summary>
    ///     One account's rows of a demo's <c>player_match</c> table as one row. A reconnect leaves a ghost row on
    ///     the old slot, so a row counts only when it ended on a side or shows activity. Counts add up,
    ///     <c>rounds</c> is the match's, and HLTV adds up less the constant terms each extra slot repeats (the
    ///     Player Overview's merge). Null when no row of the account counts.
    /// </summary>
    /// <param name="rows">The table's rows.</param>
    /// <param name="steamId">The SteamID64 as text.</param>
    public static IReadOnlyDictionary<string, double>? Merge(IReadOnlyList<FactRow> rows, string steamId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Dictionary<string, double>? merged = null;
        int slots = 0;
        double kastRounds = 0, kastPercents = 0;
        foreach (FactRow row in rows)
        {
            if (!string.Equals(row.Dimensions.GetValueOrDefault("steam_id")?.AsText(), steamId, StringComparison.Ordinal)
                || (row.Dimensions.GetValueOrDefault("team")?.AsInteger() is not (2 or 3)
                    && !_activity.Any(c => row.Values.GetValueOrDefault(c)?.AsNumber() > 0)))
            {
                continue;
            }

            merged ??= new Dictionary<string, double>(row.Values.Count, StringComparer.Ordinal);
            slots++;
            foreach ((string column, FactValue cell) in row.Values)
            {
                double value = cell.AsNumber() ?? 0;
                merged[column] = column == RoundsColumn
                    ? Math.Max(merged.GetValueOrDefault(column), value)
                    : merged.GetValueOrDefault(column) + value;
            }

            double kast = row.Values.GetValueOrDefault("KAST%")?.AsNumber() ?? 0;
            kastPercents += kast;
            kastRounds += kast * (row.Values.GetValueOrDefault(RoundsColumn)?.AsNumber() ?? 0) / 100;
        }

        if (merged is null || slots == 1)
        {
            return merged;
        }

        double rounds = merged.GetValueOrDefault(RoundsColumn);
        double kastPercent = rounds == 0 ? 0 : Math.Min(kastRounds, rounds) / rounds * 100;
        merged["KAST%"] = kastPercent;
        merged["HLTV"] = merged.GetValueOrDefault("HLTV") - ((slots - 1) * HltvConstant)
                         - ((kastPercents - kastPercent) * HltvPerKastPercent);
        return merged;
    }

    // Enemy kills and deaths of the player's slots over the live rounds: the K/D of a demo without facts.
    private static (int Kills, int Deaths) KillsFromRoundFacts(RoundFactsRows? rows, List<int> slots)
    {
        if (rows is null)
        {
            return (0, 0);
        }

        int kills = 0, deaths = 0;
        foreach (RoundFacts round in rows.Rounds.Where(r => r.IsLive))
        {
            foreach (KillStep kill in round.Kills)
            {
                bool victimCt = round.Ct.Slots.Contains(kill.VictimSlot);
                bool attackerCt = round.Ct.Slots.Contains(kill.AttackerSlot);
                bool enemy = kill.AttackerSlot >= 0 && victimCt != attackerCt
                                                    && (attackerCt || round.T.Slots.Contains(kill.AttackerSlot));
                if (slots.Contains(kill.VictimSlot))
                {
                    deaths++;
                }
                else if (enemy && slots.Contains(kill.AttackerSlot))
                {
                    kills++;
                }
            }
        }

        return (kills, deaths);
    }

    private static TendencyRound Clip(string path, string? sha, int rate, RoundFacts round, int from, int to)
    {
        int start = Math.Max(round.FreezeEndTick, from);
        int end = round.EndTick is { } e ? Math.Min(e, to) : to;
        return new TendencyRound(path, sha, round.Number, start, Math.Max(start, end), rate);
    }
}
