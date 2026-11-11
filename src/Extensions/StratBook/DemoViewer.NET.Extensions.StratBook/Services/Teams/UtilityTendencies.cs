#region

using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>One demo's inputs to <see cref="UtilityTendencies" />: the team's rounds, the grenades indexed for it.</summary>
/// <param name="Path">The demo.</param>
/// <param name="Sha256">Its content hash, or null.</param>
/// <param name="TickRate">The frame clock's rate.</param>
/// <param name="Roster">The team's SteamIDs; null or empty falls back to the thrower's side.</param>
/// <param name="Grenades">The demo's grenades; empty means none were indexed.</param>
/// <param name="Rounds">The demo's round facts.</param>
/// <param name="SideAt">The team's side (2 or 3) in a round, or null when it is not on one.</param>
public sealed record UtilityDemo(
    string Path,
    string? Sha256,
    int TickRate,
    IReadOnlySet<string>? Roster,
    IReadOnlyList<IndexedGrenade> Grenades,
    IReadOnlyList<RoundFacts> Rounds,
    Func<int, int?> SideAt);

/// <summary>
///     Where the team's grenades landed: one cluster per kind group (smoke S, molotov and incendiary M, flash F,
///     HE H) and landing place, with the rounds it was thrown in and the median seconds after freeze end.
/// </summary>
/// <param name="Letter">"S", "M", "F" or "H".</param>
/// <param name="Place">The landing place; "unplaced" when the map has none for it.</param>
/// <param name="Landing">The mean landing point.</param>
/// <param name="Throws">Grenades in the cluster.</param>
/// <param name="MedianSeconds">The median release, seconds after freeze end.</param>
/// <param name="Rounds">One clip per round it was thrown in, around the round's first such throw.</param>
public sealed record UtilityCluster(
    string Letter,
    string Place,
    WorldPoint Landing,
    int Throws,
    double MedianSeconds,
    IReadOnlyList<TendencyRound> Rounds)
{
    /// <summary>Every throw that landed in the cluster: where it was thrown from, by whom, when.</summary>
    public IReadOnlyList<UtilityThrow> Thrown { get; init; } = [];

    /// <summary>"smoke", "molotov", "flash", "HE".</summary>
    public string Noun => UtilityTendencies.Noun(Letter);
}

/// <summary>The utility numbers of one map side, buy-filtered.</summary>
public sealed record UtilityBlock
{
    /// <summary>Rounds of the scope in demos with grenades indexed.</summary>
    public int Rounds { get; init; }

    /// <summary>First-grenade clock bars, ending with the "none" bar.</summary>
    public IReadOnlyList<TendencyBucket> FirstClock { get; init; } = [];

    /// <summary>Most rounds first.</summary>
    public IReadOnlyList<UtilityCluster> Clusters { get; init; } = [];
}

/// <summary>One grenade of a cluster: where it left the thrower's hand, and who threw it when.</summary>
/// <param name="Origin">Where it was thrown from.</param>
/// <param name="SteamId">The thrower's SteamID64, or null.</param>
/// <param name="Name">The thrower's in-game name, or null.</param>
/// <param name="Seconds">Seconds after freeze end.</param>
/// <param name="JumpThrow">A jump throw.</param>
/// <param name="Round">Its round, clipped around the throw.</param>
/// <param name="ReleaseTick">The tick it left the hand.</param>
public sealed record UtilityThrow(
    WorldPoint Origin,
    string? SteamId,
    string? Name,
    double Seconds,
    bool JumpThrow,
    TendencyRound Round,
    int ReleaseTick);

/// <summary>Derives <see cref="UtilityBlock" /> from demos; pure, so a test feeds it synthetic grenades.</summary>
public static class UtilityTendencies
{
    /// <summary>The bar for rounds the team threw nothing in.</summary>
    public const string NoUtility = "none";

    /// <summary>The kind group letter, or null for a kind that is not drawn.</summary>
    /// <param name="kind">The grenade.</param>
    public static string? LetterOf(GrenadeKind kind) => kind switch
    {
        GrenadeKind.Smoke => "S",
        GrenadeKind.Molotov or GrenadeKind.Incendiary => "M",
        GrenadeKind.Flash => "F",
        GrenadeKind.He => "H",
        _ => null
    };

    /// <summary>The noun of a letter.</summary>
    /// <param name="letter">The letter.</param>
    public static string Noun(string letter) => letter switch
    {
        "S" => "smoke",
        "M" => "molotov",
        "F" => "flash",
        _ => "HE"
    };

    /// <summary>Builds the block.</summary>
    /// <param name="demos">The team's demos on the map.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    /// <param name="buy">The buy of the team's side to keep; null keeps every round.</param>
    public static UtilityBlock Build(IEnumerable<UtilityDemo> demos, int side, BuyType? buy)
    {
        ArgumentNullException.ThrowIfNull(demos);
        int rounds = 0;
        Dictionary<string, List<TendencyRound>> clock = new(StringComparer.Ordinal);
        Dictionary<(string Letter, string Place), Group> groups = [];
        foreach (UtilityDemo demo in demos)
        {
            if (demo.Grenades.Count == 0)
            {
                continue;
            }

            int rate = Math.Max(1, demo.TickRate);
            foreach (RoundFacts round in demo.Rounds.OrderBy(r => r.Number))
            {
                SideFacts ours = side == 3 ? round.Ct : round.T;
                if (!round.IsLive || demo.SideAt(round.Number) != side || (buy is { } b && ours.BuyType != b))
                {
                    continue;
                }

                rounds++;
                int freeze = round.FreezeEndTick;
                List<IndexedGrenade> thrown =
                [
                    .. demo.Grenades
                        .Where(g => g.Row.RoundNumber == round.Number && g.Row.ReleaseTick >= freeze
                                                                      && Throws(demo, g, side))
                        .OrderBy(g => g.Row.ReleaseTick)
                ];
                if (thrown.Count == 0)
                {
                    Add(clock, NoUtility,
                        Clip(demo, round, freeze, freeze + OpeningTendenciesService.ClockCapSeconds * rate));
                }
                else
                {
                    Add(clock, OpeningTendenciesService.ClockLabel((thrown[0].Row.ReleaseTick - freeze) / (double)rate),
                        ClipAround(demo, round, thrown[0]));
                }

                HashSet<(string, string)> seen = [];
                foreach (IndexedGrenade g in thrown)
                {
                    if (LetterOf(g.Kind) is not { } letter)
                    {
                        continue;
                    }

                    string place = g.LandingPlace ?? "unplaced";
                    if (!groups.TryGetValue((letter, place), out Group? group))
                    {
                        group = new Group();
                        groups[(letter, place)] = group;
                    }

                    double seconds = (g.Row.ReleaseTick - freeze) / (double)rate;
                    group.Seconds.Add(seconds);
                    group.Thrown.Add(new UtilityThrow(g.Origin, g.Row.ThrowerSteamId64, g.Row.ThrowerName, seconds,
                        g.Row.JumpThrow, ClipAround(demo, round, g), g.Row.ReleaseTick));
                    group.X += g.Landing.X;
                    group.Y += g.Landing.Y;
                    group.Z += g.Landing.Z;
                    if (seen.Add((letter, place)))
                    {
                        group.Rounds.Add(ClipAround(demo, round, g));
                    }
                }
            }
        }

        return new UtilityBlock
        {
            Rounds = rounds,
            FirstClock = Clock(clock),
            Clusters =
            [
                .. groups
                    .Select(p => p.Value.ToCluster(p.Key.Letter, p.Key.Place))
                    .OrderByDescending(c => c.Rounds.Count)
                    .ThenByDescending(c => c.Throws)
                    .ThenBy(c => c.Letter, StringComparer.Ordinal)
                    .ThenBy(c => c.Place, StringComparer.Ordinal)
            ]
        };
    }

    private static bool Throws(UtilityDemo demo, IndexedGrenade g, int side) =>
        g.Row.ThrowerSteamId64 is { } steam && demo.Roster is { Count: > 0 } roster
            ? roster.Contains(steam) && g.Row.ThrowerTeam == side
            : g.Row.ThrowerTeam == side;

    private static TendencyRound ClipAround(UtilityDemo demo, RoundFacts round, IndexedGrenade g)
    {
        int rate = Math.Max(1, demo.TickRate);
        int release = g.Row.ReleaseTick;
        return Clip(demo, round, release - OpeningTendenciesService.ClipLeadSeconds * rate,
            (g.Row.DetonationTick ?? release) + OpeningTendenciesService.ClipTailSeconds * rate);
    }

    private static TendencyRound Clip(UtilityDemo demo, RoundFacts round, int from, int to)
    {
        int start = Math.Max(round.FreezeEndTick, from);
        int end = round.EndTick is { } e ? Math.Min(e, to) : to;
        return new TendencyRound(demo.Path, demo.Sha256, round.Number, start, Math.Max(start, end),
            Math.Max(1, demo.TickRate));
    }

    private static void Add(Dictionary<string, List<TendencyRound>> into, string label, TendencyRound round)
    {
        if (!into.TryGetValue(label, out List<TendencyRound>? list))
        {
            list = [];
            into[label] = list;
        }

        list.Add(round);
    }

    private static List<TendencyBucket> Clock(Dictionary<string, List<TendencyRound>> rounds)
    {
        int step = OpeningTendenciesService.ClockBinSeconds;
        int last = -1;
        for (int bin = 0; bin <= OpeningTendenciesService.ClockCapSeconds / step; bin++)
        {
            if (rounds.ContainsKey(OpeningTendenciesService.ClockLabel(bin * step)))
            {
                last = bin;
            }
        }

        List<TendencyBucket> bars = [];
        for (int bin = 0; bin <= last; bin++)
        {
            string label = OpeningTendenciesService.ClockLabel(bin * step);
            bars.Add(new TendencyBucket(label, rounds.TryGetValue(label, out List<TendencyRound>? r) ? r : []));
        }

        if (rounds.TryGetValue(NoUtility, out List<TendencyRound>? none))
        {
            bars.Add(new TendencyBucket(NoUtility, none));
        }

        return bars;
    }

    private sealed class Group
    {
        public List<double> Seconds { get; } = [];

        public List<TendencyRound> Rounds { get; } = [];

        public List<UtilityThrow> Thrown { get; } = [];

        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }

        public UtilityCluster ToCluster(string letter, string place)
        {
            int n = Seconds.Count;
            List<double> sorted = [.. Seconds.Order()];
            double median = n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
            return new UtilityCluster(letter, place, new WorldPoint((float)(X / n), (float)(Y / n), (float)(Z / n)), n,
                median, Rounds) { Thrown = Thrown };
        }
    }
}
