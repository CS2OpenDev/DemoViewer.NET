#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>A map's site regions for the Setups areas: the places counted as each site, the site place included.</summary>
/// <param name="A">The A region.</param>
/// <param name="B">The B region.</param>
public sealed record SetupSiteRegions(IReadOnlySet<string> A, IReadOnlySet<string> B);

/// <summary>The three areas a CT setup is counted in: A, mid, B.</summary>
public enum SetupArea
{
    A,
    Mid,
    B
}

/// <summary>
///     Puts a CT position in A, mid or B. A sample whose place is a bombsite is that site. When the map's site
///     regions are known (Suggested Tags' learned or edited table: each site's place plus its approaches), a place
///     in one site's region and not the other's is that site. Any other is the site whose centre (the mean of every
///     CT sample named after it) is near, where near is within <see cref="SiteShare" /> of the distance between the
///     two centres, and mid otherwise. Height counts double, so stacked sites (de_nuke) stay apart. Without both
///     centres every place outside the regions is mid.
/// </summary>
public sealed class SetupAreaClassifier
{
    /// <summary>The share of the site-to-site distance a position may be from a site's centre and still be on it.</summary>
    public const double SiteShare = 0.4;

    private const double HeightWeight = 2;
    private const string SiteA = "BombsiteA";
    private const string SiteB = "BombsiteB";

    private readonly (double X, double Y, double Z)? _a;
    private readonly (double X, double Y, double Z)? _b;
    private readonly HashSet<string> _aOnly;
    private readonly HashSet<string> _bOnly;

    private SetupAreaClassifier((double, double, double)? a, (double, double, double)? b,
        SetupSiteRegions? regions)
    {
        _a = a;
        _b = b;
        // A place both regions hold (a shared mid approach) is no evidence for either site: distance decides it.
        _aOnly = new HashSet<string>(regions?.A ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        _bOnly = new HashSet<string>(regions?.B ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        _aOnly.ExceptWith(regions?.B ?? Enumerable.Empty<string>());
        _bOnly.ExceptWith(regions?.A ?? Enumerable.Empty<string>());
    }

    /// <summary>The map's site regions put places on a site, beyond the two bombsite places.</summary>
    public bool HasRegions => _aOnly.Count > 1 || _bOnly.Count > 1;

    /// <summary>Both site centres are known, so a position off the site places can still be put on a site.</summary>
    public bool HasSites => _a is not null && _b is not null;

    /// <summary>Learns the site centres from every CT sample of the rounds.</summary>
    /// <param name="rounds">The rounds of one map, every buy.</param>
    /// <param name="regions">The map's site regions, or null to decide by distance alone.</param>
    public static SetupAreaClassifier Learn(IEnumerable<SetupHeatmapRound> rounds, SetupSiteRegions? regions = null)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        (double X, double Y, double Z, int N) a = default;
        (double X, double Y, double Z, int N) b = default;
        foreach (SetupRoundPlayer player in rounds.Select(r => r.Detail).OfType<SetupRoundDetail>()
                     .SelectMany(d => d.Players))
        {
            foreach (SetupSample s in player.Setup.Concat(player.AfterContact.Select(t => t.Sample)))
            {
                if (s.Place == SiteA)
                {
                    a = (a.X + s.X, a.Y + s.Y, a.Z + s.Z, a.N + 1);
                }
                else if (s.Place == SiteB)
                {
                    b = (b.X + s.X, b.Y + s.Y, b.Z + s.Z, b.N + 1);
                }
            }
        }

        return new SetupAreaClassifier(a.N > 0 ? (a.X / a.N, a.Y / a.N, a.Z / a.N) : null,
            b.N > 0 ? (b.X / b.N, b.Y / b.N, b.Z / b.N) : null, regions);
    }

    /// <summary>The area of one sample.</summary>
    /// <param name="sample">The sample.</param>
    public SetupArea Of(SetupSample sample) => sample.Place switch
    {
        SiteA => SetupArea.A,
        SiteB => SetupArea.B,
        _ => InRegion(sample.Place) ?? Near(sample.X, sample.Y, sample.Z)
    };

    /// <summary>The area a player held over a window: their most-sampled place when it is a site, else their mean position.</summary>
    /// <param name="samples">The samples; mid when there are none.</param>
    public SetupArea Of(IReadOnlyList<SetupSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return SetupArea.Mid;
        }

        string? place = SetupsDerivation.DominantPlace(samples);
        if (place is SiteA or SiteB)
        {
            return place == SiteA ? SetupArea.A : SetupArea.B;
        }

        if (InRegion(place) is { } region)
        {
            return region;
        }

        return Near(samples.Average(s => s.X), samples.Average(s => s.Y), samples.Average(s => s.Z));
    }

    private SetupArea? InRegion(string? place) =>
        place is null ? null
        : _aOnly.Contains(place) ? SetupArea.A
        : _bOnly.Contains(place) ? SetupArea.B
        : null;

    private SetupArea Near(double x, double y, double z)
    {
        if (_a is not { } a || _b is not { } b)
        {
            return SetupArea.Mid;
        }

        double between = Distance(a, b);
        double toA = Distance(a, (x, y, z));
        double toB = Distance(b, (x, y, z));
        if (toA <= toB && toA <= SiteShare * between)
        {
            return SetupArea.A;
        }

        return toB < toA && toB <= SiteShare * between ? SetupArea.B : SetupArea.Mid;
    }

    private static double Distance((double X, double Y, double Z) p, (double X, double Y, double Z) q)
    {
        double dx = p.X - q.X;
        double dy = p.Y - q.Y;
        double dz = (p.Z - q.Z) * HeightWeight;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>A place one player held in the setup window, with the rounds they held it in.</summary>
/// <param name="Key">The player's key (<see cref="SetupRoundCapture.KeyOf" />).</param>
/// <param name="Name">The player's latest name in the rounds.</param>
/// <param name="Place">The place.</param>
/// <param name="Rounds">The rounds it was the player's main place (most of their samples in the window).</param>
/// <param name="Of">The rounds the player was sampled in the window.</param>
/// <param name="IsMain">The player's most-held place.</param>
/// <param name="X">The mean X of the player's samples there.</param>
/// <param name="Y">The mean Y.</param>
/// <param name="Z">The mean Z.</param>
public sealed record SetupPlayerPlace(
    string Key,
    string Name,
    string Place,
    IReadOnlyList<SetupHeatmapRound> Rounds,
    int Of,
    bool IsMain,
    float X,
    float Y,
    float Z)
{
    /// <summary>Held in at least <see cref="SetupHeatmapService.FixedShare" /> of the player's rounds.</summary>
    public bool IsFixed => Of > 0 && Rounds.Count >= SetupHeatmapService.FixedShare * Of;
}

/// <summary>One count of CTs per area, "2-1-2", with its rounds and how many were won.</summary>
/// <param name="A">CTs on A.</param>
/// <param name="Mid">CTs in mid.</param>
/// <param name="B">CTs on B.</param>
/// <param name="Rounds">The rounds set up this way.</param>
/// <param name="Won">How many of them the team won.</param>
public sealed record SetupFormation(int A, int Mid, int B, IReadOnlyList<SetupHeatmapRound> Rounds, int Won)
{
    /// <summary>"2-1-2", "3-1-1, A stack".</summary>
    public string Label
    {
        get
        {
            string counts = string.Create(CultureInfo.InvariantCulture, $"{A}-{Mid}-{B}");
            return A >= 3 ? counts + ", A stack" : B >= 3 ? counts + ", B stack" : counts;
        }
    }
}

/// <summary>One planted round's rotation: the hit site, and when the first CT from elsewhere reached it.</summary>
/// <param name="Round">The round.</param>
/// <param name="Site">The site the bomb went down on.</param>
/// <param name="ArrivalSeconds">Seconds after first contact the first rotator was on the site; null when none got there.</param>
/// <param name="Rotator">The first rotator's name, or null.</param>
/// <param name="BeforePlant">The first rotator was on the site before the plant.</param>
public sealed record SetupRotation(
    SetupHeatmapRound Round,
    BombSite Site,
    double? ArrivalSeconds,
    string? Rotator,
    bool BeforePlant);

/// <summary>
///     The Setups layer's numbers over a set of CT rounds: per player places, CTs per area and rotations. Pure
///     functions of <see cref="SetupHeatmapRound.Detail" />, so they are tested on synthetic rounds.
/// </summary>
public static class SetupsDerivation
{
    /// <summary>A second place shows under a player when it is their main place in at least this share of their rounds.</summary>
    public const double AlternateShare = 0.25;

    /// <summary>
    ///     Per player, the place they held most (their main place in most rounds) and, when it reaches
    ///     <see cref="AlternateShare" />, the next one. Players with the most sampled rounds first.
    /// </summary>
    /// <param name="rounds">The rounds.</param>
    public static IReadOnlyList<SetupPlayerPlace> PlacesByPlayer(IEnumerable<SetupHeatmapRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        Dictionary<string, PlayerTally> players = new(StringComparer.Ordinal);
        foreach (SetupHeatmapRound round in rounds)
        {
            if (round.Detail is not { } detail)
            {
                continue;
            }

            foreach (SetupRoundPlayer player in detail.Players.Where(p => p.Setup.Count > 0))
            {
                if (!players.TryGetValue(player.Key, out PlayerTally? tally))
                {
                    tally = new PlayerTally();
                    players[player.Key] = tally;
                }

                tally.Name = player.Name;
                tally.Of++;
                if (DominantPlace(player.Setup) is not { } place)
                {
                    continue;
                }

                if (!tally.Places.TryGetValue(place, out PlaceTally? at))
                {
                    at = new PlaceTally();
                    tally.Places[place] = at;
                }

                at.Rounds.Add(round);
                foreach (SetupSample s in player.Setup.Where(s => s.Place == place))
                {
                    at.X += s.X;
                    at.Y += s.Y;
                    at.Z += s.Z;
                    at.N++;
                }
            }
        }

        List<SetupPlayerPlace> result = [];
        foreach ((string key, PlayerTally tally) in players.OrderByDescending(p => p.Value.Of)
                     .ThenBy(p => p.Value.Name, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            List<KeyValuePair<string, PlaceTally>> ranked =
            [
                .. tally.Places.OrderByDescending(p => p.Value.Rounds.Count).ThenBy(p => p.Key, StringComparer.Ordinal)
            ];
            for (int i = 0; i < ranked.Count && i < 2; i++)
            {
                (string place, PlaceTally at) = (ranked[i].Key, ranked[i].Value);
                if (i == 1 && at.Rounds.Count < AlternateShare * tally.Of)
                {
                    break;
                }

                result.Add(new SetupPlayerPlace(key, tally.Name, place, at.Rounds, tally.Of, i == 0,
                    (float)(at.X / at.N), (float)(at.Y / at.N), (float)(at.Z / at.N)));
            }
        }

        return result;
    }

    /// <summary>Every count of CTs per area the rounds were set up in, most rounds first.</summary>
    /// <param name="rounds">The rounds.</param>
    /// <param name="areas">The map's areas.</param>
    public static IReadOnlyList<SetupFormation> Formations(IEnumerable<SetupHeatmapRound> rounds,
        SetupAreaClassifier areas)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(areas);
        Dictionary<(int, int, int), List<SetupHeatmapRound>> cells = [];
        foreach (SetupHeatmapRound round in rounds)
        {
            if (round.Detail is not { Decided: true } detail)
            {
                continue;
            }

            int a = 0, mid = 0, b = 0;
            foreach (SetupRoundPlayer player in detail.Players.Where(p => p.Setup.Count > 0))
            {
                switch (areas.Of(player.Setup))
                {
                    case SetupArea.A:
                        a++;
                        break;
                    case SetupArea.B:
                        b++;
                        break;
                    default:
                        mid++;
                        break;
                }
            }

            if (a + mid + b == 0)
            {
                continue;
            }

            if (!cells.TryGetValue((a, mid, b), out List<SetupHeatmapRound>? list))
            {
                list = [];
                cells[(a, mid, b)] = list;
            }

            list.Add(round);
        }

        return
        [
            .. cells.Select(c => new SetupFormation(c.Key.Item1, c.Key.Item2, c.Key.Item3, c.Value,
                    c.Value.Count(r => r.Detail!.Won)))
                .OrderByDescending(f => f.Rounds.Count)
                .ThenBy(f => f.Label, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The rotation of every planted round with a first contact before the plant and at least one CT set up
    ///     off the hit site: when the first of those reached the site (by <paramref name="areas" />), and whether
    ///     that was before the plant. A round where nobody got there inside the followed window has no arrival.
    /// </summary>
    /// <param name="rounds">The rounds.</param>
    /// <param name="areas">The map's areas.</param>
    public static IReadOnlyList<SetupRotation> Rotations(IEnumerable<SetupHeatmapRound> rounds,
        SetupAreaClassifier areas)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(areas);
        List<SetupRotation> result = [];
        foreach (SetupHeatmapRound round in rounds)
        {
            if (round.Detail is not { PlantAfterContactSeconds: { } plant } detail
                || detail.PlantSite is not (BombSite.A or BombSite.B))
            {
                continue;
            }

            SetupArea hit = detail.PlantSite == BombSite.A ? SetupArea.A : SetupArea.B;
            double? first = null;
            string? rotator = null;
            bool any = false;
            foreach (SetupRoundPlayer player in detail.Players)
            {
                IReadOnlyList<SetupSample> setup = player.Setup.Count > 0
                    ? player.Setup
                    : player.AfterContact.Count > 0
                        ? [player.AfterContact[0].Sample]
                        : [];
                if (setup.Count == 0 || areas.Of(setup) == hit)
                {
                    continue;
                }

                any = true;
                foreach (SetupTimedSample t in player.AfterContact)
                {
                    if (areas.Of(t.Sample) != hit)
                    {
                        continue;
                    }

                    if (first is null || t.Seconds < first)
                    {
                        first = t.Seconds;
                        rotator = player.Name;
                    }

                    break;
                }
            }

            if (any)
            {
                result.Add(new SetupRotation(round, detail.PlantSite, first, rotator, first is { } f && f < plant));
            }
        }

        return result;
    }

    /// <summary>
    ///     The fake rounds the team defended that the T side planted on the real site after contact: whether a CT set
    ///     up on the real site (by <paramref name="areas" />) was off it after contact and before the plant, which
    ///     is the fake pulling a defender away. A round with no CT set up on the real site has nobody to pull and is
    ///     left out.
    /// </summary>
    /// <param name="rounds">The rounds.</param>
    /// <param name="areas">The map's areas.</param>
    /// <param name="fakeOf">The round's fake proposal, or null.</param>
    public static IReadOnlyList<FakeRotation> FakeRotations(IEnumerable<SetupHeatmapRound> rounds,
        SetupAreaClassifier areas, Func<SetupHeatmapRound, FakeRound?> fakeOf)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(fakeOf);
        List<FakeRotation> result = [];
        foreach (SetupHeatmapRound round in rounds)
        {
            if (fakeOf(round) is not { } fake
                || round.Detail is not { PlantAfterContactSeconds: { } plant } detail
                || detail.PlantSite != fake.Real)
            {
                continue;
            }

            SetupArea real = fake.Real == BombSite.A ? SetupArea.A : SetupArea.B;
            bool anyHolder = false;
            double? first = null;
            string? rotator = null;
            foreach (SetupRoundPlayer player in detail.Players.Where(p => p.Setup.Count > 0))
            {
                if (areas.Of(player.Setup) != real)
                {
                    continue;
                }

                anyHolder = true;
                foreach (SetupTimedSample t in player.AfterContact)
                {
                    if (t.Seconds >= plant)
                    {
                        break;
                    }

                    if (areas.Of(t.Sample) != real)
                    {
                        if (first is null || t.Seconds < first)
                        {
                            first = t.Seconds;
                            rotator = player.Name;
                        }

                        break;
                    }
                }
            }

            if (anyHolder)
            {
                result.Add(new FakeRotation(round, fake, first is not null, rotator));
            }
        }

        return result;
    }

    /// <summary>The median of the values, or null with none.</summary>
    /// <param name="values">The values.</param>
    public static double? Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        double[] sorted = [.. values.Order()];
        if (sorted.Length == 0)
        {
            return null;
        }

        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The place most of the samples name; ties go to the ordinal first. Null when none is placed.</summary>
    /// <param name="samples">The samples.</param>
    public static string? DominantPlace(IReadOnlyList<SetupSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return samples.Where(s => s.Place is not null)
            .GroupBy(s => s.Place!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>"A site" and "B site" for the bombsites; any other place as the map names it.</summary>
    /// <param name="place">The place.</param>
    public static string PlaceLabel(string place) => place switch
    {
        "BombsiteA" => "A site",
        "BombsiteB" => "B site",
        _ => place
    };

    private sealed class PlayerTally
    {
        public string Name { get; set; } = "";

        public int Of { get; set; }

        public Dictionary<string, PlaceTally> Places { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PlaceTally
    {
        public List<SetupHeatmapRound> Rounds { get; } = [];

        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }

        public int N { get; set; }
    }
}
