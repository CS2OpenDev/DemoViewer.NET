#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>One named plant spot: the cluster's rounds, where its marks centre, and the badge the map and the row share.</summary>
/// <param name="Label">"A spot 1 (1050, 1000)".</param>
/// <param name="Site">"A", "B" or "unknown site".</param>
/// <param name="Badge">"A1": the site and the spot's rank on it.</param>
/// <param name="Bucket">The planted rounds that went there, after the buy filter.</param>
/// <param name="X">World X of the centre of its spots.</param>
/// <param name="Y">World Y of the centre.</param>
/// <param name="Z">World Z of the centre.</param>
/// <param name="HasCentre">False for the spot the positions do not give.</param>
public sealed record PlantSpot(string Label, string Site, string Badge, TendencyBucket Bucket, float X, float Y, float Z,
    bool HasCentre);

/// <summary>
///     What the Plants layer shows for one map, T side and buy: the Post-Plant block and the site split with
///     the buy filter applied to every number, the plant spots named, and the planted rounds won. Pure over the
///     blocks, so a test drives it with hand-built ones.
/// </summary>
public sealed class PlantReading
{
    private const string UnreadSpot = "spot unread";
    private const string NoPlant = "no plant";

    public TendencyBucket SideRounds { get; private init; } = TendencyBucket.Empty("rounds");

    public TendencyBucket Planted { get; private init; } = TendencyBucket.Empty("planted");

    /// <summary>The planted rounds the team won.</summary>
    public TendencyBucket PlantedWon { get; private init; } = TendencyBucket.Empty("planted rounds won");

    /// <summary>A, B, then "no plant": the rounds that went there.</summary>
    public IReadOnlyList<TendencyBucket> SiteSplit { get; private init; } = [];

    /// <summary>Every plant spot, the unread ones last, most rounds first.</summary>
    public IReadOnlyList<PlantSpot> Spots { get; private init; } = [];

    /// <summary>The points to draw, after the buy filter.</summary>
    public IReadOnlyList<PlantSpotPoint> Points { get; private init; } = [];

    public IReadOnlyList<TendencyBucket> HoldShapes { get; private init; } = [];

    public IReadOnlyList<TendencyBucket> HoldPlaces { get; private init; } = [];

    public IReadOnlyList<TendencyBucket> ManCount { get; private init; } = [];

    public IReadOnlyList<TendencyBucket> Outcomes { get; private init; } = [];

    /// <summary>True when the Post-Plant block exists (without it the reading has only the site split).</summary>
    public bool HasBlock { get; private init; }

    /// <summary>Rounds of the scope in the site split's sample.</summary>
    public int SiteRounds { get; private init; }

    /// <summary>The named spot with the most rounds on each site, with the rounds planted on that site.</summary>
    public IEnumerable<(string Site, PlantSpot Top, int OfSite)> TopPerSite() =>
        Spots.Where(s => s.HasCentre).GroupBy(s => s.Site, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.OrderByDescending(s => s.Bucket.Count).First(),
                Spots.Where(s => s.Site == g.Key).Sum(s => s.Bucket.Count)));

    /// <summary>Reads the blocks under a buy filter.</summary>
    /// <param name="block">The T side's Post-Plant block, or null.</param>
    /// <param name="openings">The T side's Opening Tendencies block, or null.</param>
    /// <param name="buy">The buy class to keep; null keeps every round.</param>
    public static PlantReading Read(PostPlantBlock? block, OpeningTendencies? openings, BuyType? buy)
    {
        bool Keep(TendencyRound r) => buy is null
                                      || (block is not null
                                          && block.RoundBuys.TryGetValue((r.DemoPath, r.RoundNumber), out BuyType b)
                                          && b == buy);

        TendencyBucket Filter(TendencyBucket b) => new(b.Label, [.. b.Rounds.Where(Keep)]);

        List<TendencyBucket> FilterAll(IEnumerable<TendencyBucket> buckets) =>
            [.. buckets.Select(Filter).Where(b => b.Count > 0)];

        List<TendencyBucket> split = openings is null ? [] : FilterAll(openings.SiteSplit);
        int siteRounds = openings is null ? 0 : openings.Rounds.Rounds.Count(Keep);
        if (block is null)
        {
            return new PlantReading { SiteSplit = split, SiteRounds = siteRounds };
        }

        List<PlantSpotPoint> points = [.. block.PlantSpots.Where(p => Keep(p.Round))];
        Dictionary<TendencyRound, string> clusterOf = [];
        foreach (TendencyBucket cluster in block.PlantClusters)
        {
            foreach (TendencyRound r in cluster.Rounds)
            {
                clusterOf[r] = cluster.Label;
            }
        }

        List<PlantSpot> spots = [];
        Dictionary<string, int> rank = new(StringComparer.Ordinal);
        foreach (TendencyBucket cluster in FilterAll(block.PlantClusters)
                     .OrderBy(b => b.Label.EndsWith(UnreadSpot, StringComparison.Ordinal))
                     .ThenByDescending(b => b.Count).ThenBy(b => b.Label, StringComparer.Ordinal))
        {
            string site = SiteOf(cluster.Label);
            bool unread = cluster.Label.EndsWith(UnreadSpot, StringComparison.Ordinal);
            rank[site] = rank.GetValueOrDefault(site) + 1;
            List<PlantSpotPoint> mine = [.. points.Where(p => clusterOf.GetValueOrDefault(p.Round) == cluster.Label)];
            string badge = (site is "A" or "B" ? site : "?") + rank[site].ToString(CultureInfo.InvariantCulture);
            spots.Add(unread || mine.Count == 0
                ? new PlantSpot(cluster.Label, site, badge, cluster, 0, 0, 0, false)
                : new PlantSpot(cluster.Label, site, badge, cluster, (float)mine.Average(p => p.X),
                    (float)mine.Average(p => p.Y), (float)mine.Average(p => p.Z), true));
        }

        TendencyBucket planted = Filter(block.Planted);
        HashSet<TendencyRound> won =
        [
            .. block.Outcomes.Where(o => o.Label.StartsWith("won", StringComparison.Ordinal)).SelectMany(o => o.Rounds)
        ];
        return new PlantReading
        {
            HasBlock = true,
            SideRounds = Filter(block.SideRounds),
            Planted = planted,
            PlantedWon = new TendencyBucket("planted rounds won", [.. planted.Rounds.Where(won.Contains)]),
            SiteSplit = split,
            SiteRounds = siteRounds,
            Spots = spots,
            Points = points,
            HoldShapes = FilterAll(block.HoldShapes),
            HoldPlaces = FilterAll(block.HoldPlaces),
            ManCount = FilterAll(block.ManCount),
            Outcomes = FilterAll(block.Outcomes)
        };
    }

    /// <summary>The site a plant cluster's label names.</summary>
    /// <param name="label">"A spot 1 (1050, 1000)", "B spot unread".</param>
    public static string SiteOf(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        int at = label.IndexOf(" spot ", StringComparison.Ordinal);
        return at > 0 ? label[..at] : label;
    }

    /// <summary>Whether a bucket label is the split's "no plant".</summary>
    /// <param name="label">The label.</param>
    public static bool IsNoPlant(string label) => string.Equals(label, NoPlant, StringComparison.Ordinal);
}
