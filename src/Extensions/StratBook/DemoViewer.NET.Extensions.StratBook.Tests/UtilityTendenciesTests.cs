#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Utility layer's numbers over synthetic grenades: only the team's own throws on its side count,
///     the buy filter keeps the rounds of that buy, landings cluster by kind group and place with the median
///     release after freeze end, and every cluster carries the rounds it opens.
/// </summary>
public class UtilityTendenciesTests
{
    private const int Rate = 64;
    private const string Path = "/d/util.dem";
    private static readonly string[] _roster = ["76561"];

    private static int At(int freeze, double seconds) => freeze + (int)(seconds * Rate);

    private static RoundFacts Round(int number, int freeze, BuyType tBuy = BuyType.Full) => new()
    {
        Number = number,
        IsLive = true,
        FreezeEndTick = freeze,
        EndTick = freeze + 100 * Rate,
        Ct = new SideFacts { Side = 3, BuyType = BuyType.Full },
        T = new SideFacts { Side = 2, BuyType = tBuy }
    };

    private static IndexedGrenade Nade(GrenadeKind kind, int round, int team, int release, string? place,
        float x = 0, string steam = "76561") => new(
        new DemoRef(Path, "k", "sha"), "de_nuke",
        new GrenadeRow
        {
            Id = $"g{round}-{release}-{kind}",
            Kind = kind,
            RoundNumber = round,
            ThrowerTeam = team,
            ThrowerSteamId64 = steam,
            ReleaseTick = release,
            DetonationTick = release + 100
        },
        new WorldPoint(0, 0, 0), new WorldPoint(x, 0, 0), place, place is null ? null : "zones:1");

    // The team is T in rounds 1 to 3 and CT in round 4. Rounds 1 and 2 are full buys, round 3 an eco.
    private static UtilityDemo Demo(params IndexedGrenade[] grenades) => new(Path, "sha", Rate,
        new HashSet<string>(_roster), grenades,
        [Round(1, 1000), Round(2, 10000), Round(3, 20000, BuyType.Eco), Round(4, 30000)],
        n => n <= 3 ? 2 : 3);

    private static IndexedGrenade[] Throws() =>
    [
        Nade(GrenadeKind.Smoke, 1, 2, At(1000, 10), "A Ramp", 100),
        Nade(GrenadeKind.Smoke, 1, 2, At(1000, 30), "A Ramp", 300),
        Nade(GrenadeKind.Smoke, 2, 2, At(10000, 20), "A Ramp", 200),
        Nade(GrenadeKind.Molotov, 2, 2, At(10000, 8), "Heaven"),
        Nade(GrenadeKind.Incendiary, 1, 2, At(1000, 40), "Heaven"),
        Nade(GrenadeKind.Flash, 3, 2, At(20000, 2), null),
        Nade(GrenadeKind.Decoy, 1, 2, At(1000, 50), "A Ramp"),
        // Not the team: the other side's throw, and a player off the roster.
        Nade(GrenadeKind.Smoke, 1, 3, At(1000, 1), "A Ramp"),
        Nade(GrenadeKind.Smoke, 2, 2, At(10000, 1), "A Ramp", 0, "76599"),
        // Before freeze end, and in the round the team is CT.
        Nade(GrenadeKind.He, 1, 2, 500, "A Ramp"),
        Nade(GrenadeKind.Smoke, 4, 3, At(30000, 5), "CT Spawn")
    ];

    [Test]
    public async Task OnlyTheTeamsOwnThrowsOnItsSide_AreClustered()
    {
        UtilityBlock t = UtilityTendencies.Build([Demo(Throws())], 2, null);

        await Assert.That(t.Rounds).IsEqualTo(3);
        await Assert.That(t.Clusters.Select(c => $"{c.Letter} {c.Place} {c.Throws}/{c.Rounds.Count}"))
            .IsEquivalentTo(["S A Ramp 3/2", "M Heaven 2/2", "F unplaced 1/1"]);
        await Assert.That(t.Clusters.Any(c => c.Place == "CT Spawn")).IsFalse();
    }

    [Test]
    public async Task ClustersAreRankedByRounds_AndLandAtTheMeanPoint()
    {
        UtilityBlock t = UtilityTendencies.Build([Demo(Throws())], 2, null);

        await Assert.That(t.Clusters[0].Letter).IsEqualTo("S");
        await Assert.That(t.Clusters[0].Landing.X).IsEqualTo(200f);
    }

    [Test]
    public async Task MedianTiming_IsSecondsAfterFreezeEnd()
    {
        UtilityBlock t = UtilityTendencies.Build([Demo(Throws())], 2, null);

        UtilityCluster smoke = t.Clusters.Single(c => c.Letter == "S");
        await Assert.That(smoke.MedianSeconds).IsEqualTo(20.0);
        UtilityCluster molotov = t.Clusters.Single(c => c.Letter == "M");
        await Assert.That(molotov.MedianSeconds).IsEqualTo(24.0);
    }

    [Test]
    public async Task TheBuyFilter_KeepsOnlyRoundsOfThatBuy()
    {
        UtilityBlock eco = UtilityTendencies.Build([Demo(Throws())], 2, BuyType.Eco);

        await Assert.That(eco.Rounds).IsEqualTo(1);
        await Assert.That(eco.Clusters.Select(c => c.Letter)).IsEquivalentTo(["F"]);
        await Assert.That(UtilityTendencies.Build([Demo(Throws())], 2, BuyType.Force).Rounds).IsEqualTo(0);
    }

    [Test]
    public async Task TheFirstGrenadeClock_BinsEachRoundByItsEarliestThrow()
    {
        UtilityBlock t = UtilityTendencies.Build([Demo(Throws())], 2, null);

        // Round 1: the smoke at 10 s; round 2: the molotov at 8 s; round 3: the flash at 2 s.
        await Assert.That(string.Join(",", t.FirstClock.Select(b => $"{b.Label}:{b.Count}")))
            .IsEqualTo("0-5 s:1,5-10 s:1,10-15 s:1");
    }

    [Test]
    public async Task ARoundWithoutThrows_IsNone_AndEveryClusterOpensItsRounds()
    {
        UtilityBlock ct = UtilityTendencies.Build([Demo(Throws())], 3, null);

        await Assert.That(ct.Rounds).IsEqualTo(1);
        await Assert.That(ct.Clusters.Single().Rounds.Single().RoundNumber).IsEqualTo(4);
        UtilityBlock t = UtilityTendencies.Build([Demo(Nade(GrenadeKind.Smoke, 1, 2, At(1000, 3), "A Ramp"))], 2, null);
        await Assert.That(t.FirstClock.Single(b => b.Label == UtilityTendencies.NoUtility).Count).IsEqualTo(2);
        TendencyRound clip = t.Clusters.Single().Rounds.Single();
        await Assert.That(clip.DemoPath).IsEqualTo(Path);
        await Assert.That(clip.FromTick).IsGreaterThanOrEqualTo(1000);
        await Assert.That(clip.ToTick).IsGreaterThan(clip.FromTick);
    }

    [Test]
    public async Task ADemoWithoutIndexedGrenades_IsNotCountedAsNoUtility()
    {
        UtilityBlock t = UtilityTendencies.Build([Demo()], 2, null);

        await Assert.That(t.Rounds).IsEqualTo(0);
        await Assert.That(t.FirstClock.Count).IsEqualTo(0);
    }
}
