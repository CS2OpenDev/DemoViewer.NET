namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>One AWP route: the rounds whose trails cluster together and the medoid trail drawn for them.</summary>
/// <param name="Name">"CTSpawn → A site", from the medoid's start and end places.</param>
/// <param name="Members">The rounds, the medoid among them.</param>
/// <param name="Medoid">The round whose trail is closest to every other member's.</param>
/// <param name="Trail">The medoid's trail to the window's end.</param>
public sealed record AwpRoute(string Name, IReadOnlyList<AwpRound> Members, AwpRound Medoid,
    IReadOnlyList<AwpPoint> Trail);

/// <summary>The routes of a scope, biggest first, and the rounds that fell in no route.</summary>
/// <param name="Routes">At most <see cref="AwpRoutes.MaxRoutes" />.</param>
/// <param name="Other">Rounds in clusters too small to be a route.</param>
/// <param name="Clustered">Rounds with a trail: the routes' and other's sample.</param>
public sealed record AwpRouteSet(IReadOnlyList<AwpRoute> Routes, IReadOnlyList<AwpRound> Other, int Clustered);

/// <summary>
///     AWP route clustering by path similarity (spec Q9), Strat Mining's approach on whole trails: the distance of
///     two trails is the mean world distance between their points second by second (the shorter one held at its
///     last point, where the holder died or the window closed), clusters merge by complete linkage until the
///     closest pair is further apart than <see cref="Cutoff" />, and each cluster is drawn as its medoid. A cluster
///     under <see cref="MinRouteRounds" /> rounds or <see cref="MinRouteShare" /> of the sample is "other".
/// </summary>
public static class AwpRoutes
{
    /// <summary>
    ///     The linkage cut in world units: no two trails in one route are further apart than this on average. Tuned on
    ///     five FURIA Mirage demos (41 CT AWP rounds): at 700 other holds 1, 11 and 12 rounds to 0:15, 0:30 and first
    ///     contact, where 400 left 6, 17 and 19 and 900 started merging Middle into Snipers' Nest.
    /// </summary>
    public const double Cutoff = 700;

    /// <summary>The fewest rounds a route has.</summary>
    public const int MinRouteRounds = 2;

    /// <summary>The smallest share of the sample a route has.</summary>
    public const double MinRouteShare = 0.08;

    /// <summary>The most routes named; smaller clusters go to other.</summary>
    public const int MaxRoutes = 4;

    /// <summary>The mean distance of two trails second by second; infinity when either is empty.</summary>
    /// <param name="a">One trail.</param>
    /// <param name="b">The other.</param>
    public static double Distance(IReadOnlyList<AwpPoint> a, IReadOnlyList<AwpPoint> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0)
        {
            return double.PositiveInfinity;
        }

        int n = Math.Max(a.Count, b.Count);
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            AwpPoint p = a[Math.Min(i, a.Count - 1)];
            AwpPoint q = b[Math.Min(i, b.Count - 1)];
            double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
            sum += Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        return sum / n;
    }

    /// <summary>
    ///     Complete linkage over a distance matrix: merges the closest pair of clusters until none is within
    ///     <paramref name="cutoff" />. A cluster's distance to another is its farthest pair. Each cluster's members
    ///     in index order; clusters in order of their first member.
    /// </summary>
    /// <param name="d">The n × n distances, row-major.</param>
    /// <param name="n">The item count.</param>
    /// <param name="cutoff">The cut.</param>
    public static List<List<int>> CompleteLinkage(double[] d, int n, double cutoff)
    {
        ArgumentNullException.ThrowIfNull(d);
        double[] m = (double[])d.Clone();
        List<List<int>?> clusters = [.. Enumerable.Range(0, n).Select(i => new List<int> { i })];
        while (true)
        {
            int a = -1, b = -1;
            double best = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                if (clusters[i] is null)
                {
                    continue;
                }

                for (int j = i + 1; j < n; j++)
                {
                    if (clusters[j] is not null && m[i * n + j] < best)
                    {
                        best = m[i * n + j];
                        (a, b) = (i, j);
                    }
                }
            }

            if (a < 0 || best > cutoff)
            {
                break;
            }

            clusters[a]!.AddRange(clusters[b]!);
            clusters[b] = null;
            for (int k = 0; k < n; k++)
            {
                if (k != a && clusters[k] is not null)
                {
                    m[a * n + k] = m[k * n + a] = Math.Max(m[a * n + k], m[b * n + k]);
                }
            }
        }

        return [.. clusters.OfType<List<int>>().Select(c => c.Order().ToList()).OrderBy(c => c[0])];
    }

    /// <summary>The scope's routes for a window over the AWP rounds with a trail.</summary>
    /// <param name="rounds">The AWP rounds; those without a path are left out.</param>
    /// <param name="window">How far the trails run.</param>
    /// <param name="cutoff">The linkage cut; <see cref="Cutoff" /> unless tuning.</param>
    public static AwpRouteSet Build(IEnumerable<AwpRound> rounds, AwpWindow window, double cutoff = Cutoff)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<(AwpRound Round, IReadOnlyList<AwpPoint> Trail)> items =
        [
            .. rounds.Where(r => r.HasAwp).Select(r => (r, r.Trail(window))).Where(x => x.Item2.Count > 0)
        ];
        int n = items.Count;
        double[] d = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                d[i * n + j] = d[j * n + i] = Distance(items[i].Trail, items[j].Trail);
            }
        }

        int least = Math.Max(MinRouteRounds, (int)Math.Ceiling(MinRouteShare * n));
        List<List<int>> clusters =
        [
            .. CompleteLinkage(d, n, cutoff).OrderByDescending(c => c.Count).ThenBy(c => c[0])
        ];
        List<AwpRoute> routes = [];
        List<AwpRound> other = [];
        foreach (List<int> cluster in clusters)
        {
            if (cluster.Count < least || routes.Count == MaxRoutes)
            {
                other.AddRange(cluster.Select(i => items[i].Round));
                continue;
            }

            int medoid = cluster.MinBy(i => cluster.Sum(j => d[i * n + j]));
            routes.Add(new AwpRoute("", [.. cluster.Select(i => items[i].Round)], items[medoid].Round,
                items[medoid].Trail));
        }

        return new AwpRouteSet(Named(routes), [.. other.OrderBy(r => r.Round.DemoPath, StringComparer.Ordinal)
            .ThenBy(r => r.Round.Number)], n);
    }

    // "start → end" by the medoid's places, "holds X" when they match; a name two routes share gets its middle place.
    private static List<AwpRoute> Named(List<AwpRoute> routes)
    {
        List<string> names = [.. routes.Select(r => NameOf(r.Trail, false))];
        for (int i = 0; i < routes.Count; i++)
        {
            if (names.Count(n => n == names[i]) > 1)
            {
                names[i] = NameOf(routes[i].Trail, true);
            }
        }

        return [.. routes.Select((r, i) => r with { Name = names[i] })];
    }

    private static string NameOf(IReadOnlyList<AwpPoint> trail, bool via)
    {
        string start = Label(trail.FirstOrDefault(p => p.Place is not null)?.Place);
        string end = Label(trail.LastOrDefault(p => p.Place is not null)?.Place);
        string middle = Label(trail[trail.Count / 2].Place ?? trail.LastOrDefault(p => p.Place is not null)?.Place);
        if (start == end)
        {
            return via && middle != start ? $"{start} via {middle}" : $"holds {start}";
        }

        return via && middle != start && middle != end ? $"{start} → {middle} → {end}" : $"{start} → {end}";
    }

    private static string Label(string? place) => place is null ? "unplaced" : SetupsDerivation.PlaceLabel(place);
}
