#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The capture-fed Economy and Timeouts answers over eight synthetic rounds. Team A (slots 0..4) is T in
///     rounds 1 to 4 and CT in 5 to 8; team B is slots 5..9:
///     <list type="number">
///         <item>pistol (A on T): A loses.</item>
///         <item>A force, loses.</item>
///         <item>A eco, loses. B (CT) timeout before it.</item>
///         <item>A full, wins, plants A. A timeout (T) before it: three lost in a row, trailing 0-3.</item>
///         <item>pistol (A on CT): A loses. A technical timeout before it.</item>
///         <item>A semi, wins. A timeout (CT) before it.</item>
///         <item>A force, wins.</item>
///         <item>A full, wins.</item>
///     </list>
/// </summary>
public class EconomyCaptureTests
{
    private const string Demo = "/d/econ.dem";
    private const int Rate = 64;

    private static readonly int[] _a = [0, 1, 2, 3, 4];
    private static readonly int[] _b = [5, 6, 7, 8, 9];

    // (A's buy, A won, A plants A).
    private static readonly (BuyType Buy, bool Won, bool Plant)[] _script =
    [
        (BuyType.Pistol, false, false), (BuyType.Force, false, false), (BuyType.Eco, false, false),
        (BuyType.Full, true, true), (BuyType.Pistol, false, false), (BuyType.Semi, true, false),
        (BuyType.Force, true, false), (BuyType.Full, true, false)
    ];

    private static IReadOnlyList<TeamRound> Rounds()
    {
        List<RoundFacts> rounds = [];
        int own = 0;
        int enemy = 0;
        for (int i = 0; i < _script.Length; i++)
        {
            int n = i + 1;
            bool aIsT = n <= 4;
            int aSide = aIsT ? 2 : 3;
            (BuyType buy, bool won, bool plant) = _script[i];
            SideFacts a = new() { Side = aSide, Slots = _a, PlayersAtFreezeEnd = 5, BuyType = buy, ScoreBefore = own };
            SideFacts b = new()
            {
                Side = 5 - aSide, Slots = _b, PlayersAtFreezeEnd = 5, ScoreBefore = enemy,
                BuyType = buy == BuyType.Pistol ? BuyType.Pistol : BuyType.Full
            };
            rounds.Add(new RoundFacts
            {
                Number = n,
                IsLive = true,
                FreezeEndTick = n * 10000,
                EndTick = n * 10000 + 100 * Rate,
                PlantTick = plant ? n * 10000 + 40 * Rate : null,
                PlantSite = plant ? BombSite.A : BombSite.Unknown,
                WinnerSide = won ? aSide : 5 - aSide,
                Ct = aIsT ? b : a,
                T = aIsT ? a : b
            });
            if (won)
            {
                own++;
            }
            else
            {
                enemy++;
            }
        }

        RoundFactsRows rows = new()
        {
            Schema = RoundFactsRecords.Schema, Clock = new RoundFactsClock { TickRate = Rate }, Rounds = rounds
        };
        return TeamRounds.FromRows(Demo, "abc", "de_nuke", rows, n => n <= 4 ? 2 : 3);
    }

    private static RoundTimeout Timeout(int before, int side, bool technical = false) =>
        new(before * 10000 - 500, before * 10000 - 100, side, technical, 2, 2, 1, 2, before);

    private static RoundLoadout Kit(int slot, int side, int armor, bool? helmet = null, bool? defuser = null) =>
        new(slot, side, true, armor, helmet, defuser, 0, 800, 0);

    private static CapturedPurchase Buy(int slot, string item, int round) =>
        new(round * 10000 - 200, slot, item, 0, true);

    private static List<CapturedTeamRound> Captured()
    {
        CapturedRound pistolT = new()
        {
            Number = 1, Pistol = true,
            Loadouts =
            [
                Kit(0, 2, 100, true), Kit(1, 2, 100, false), Kit(2, 2, 100, false), Kit(3, 2, 100, false),
                Kit(4, 2, 0), Kit(5, 3, 100, false, true)
            ],
            Purchases =
            [
                Buy(0, "flashbang", 1), Buy(0, "smokegrenade", 1), Buy(1, "flashbang", 1), Buy(2, "p250", 1),
                Buy(3, "tec9", 1), Buy(5, "deagle", 1), Buy(5, "defuser", 1)
            ]
        };
        CapturedRound pistolCt = new()
        {
            Number = 5, Pistol = true,
            Loadouts =
            [
                Kit(0, 3, 100, true, true), Kit(1, 3, 100, false, true), Kit(2, 3, 100, false, false),
                Kit(3, 3, 0, null, false), Kit(4, 3, 100, false, false)
            ],
            Purchases = [Buy(0, "hegrenade", 5), Buy(4, "usp_silencer", 5)]
        };
        CapturedRound force = new()
        {
            Number = 2,
            Purchases =
            [
                Buy(0, "mp9", 2), Buy(1, "mp9", 2), Buy(2, "galilar", 2), Buy(3, "vest", 2), Buy(3, "flashbang", 2),
                Buy(6, "ak47", 2)
            ]
        };
        CapturedRound eco = new() { Number = 3, Purchases = [Buy(1, "p250", 3), Buy(5, "ak47", 3)] };
        CapturedRound full = new() { Number = 4, Purchases = [Buy(0, "ak47", 4)] };
        CapturedRound semi = new() { Number = 6, Purchases = [Buy(2, "mp9", 6), Buy(3, "m4a1_silencer", 6)] };
        CapturedRound force2 = new() { Number = 7, Purchases = [Buy(0, "mp9", 7), Buy(1, "famas", 7)] };
        RoundCapturesDocument document = new()
        {
            Collected = true, TickRate = Rate,
            Rounds = [pistolT, force, eco, full, pistolCt, semi, force2]
        };
        DemoCaptures captures = new(document);
        List<CapturedTeamRound> paired = [];
        foreach (TeamRound round in Rounds())
        {
            if (captures.Round(round.Number) is { } view)
            {
                paired.Add(new CapturedTeamRound(round, view));
            }
        }

        return paired;
    }

    [Test]
    public async Task Timeouts_AreScopedToTheTeam_TechnicalOnesCountedApart_AndTheCallIsTheNextRound()
    {
        IReadOnlyList<RoundTimeout> stored =
        [
            Timeout(3, 3), // B, the CT side in round 3: not ours
            Timeout(4, 2), // ours, T
            Timeout(5, 0, true), // technical
            Timeout(6, 3) // ours, CT
        ];
        TeamTimeouts result = TimeoutDerivation.Build(Rounds(), p => p == Demo ? stored : null);
        TeamTimeout t = result.Tactical[0];
        TeamTimeout ct = result.Tactical[1];
        using (Assert.Multiple())
        {
            await Assert.That(result.Tactical.Count).IsEqualTo(2);
            await Assert.That(result.Technical).IsEqualTo(1);
            await Assert.That(t.Round.Number).IsEqualTo(4);
            await Assert.That(t.Call).IsEqualTo("A plant");
            await Assert.That(t.LostInARow).IsEqualTo(3);
            await Assert.That(t.State).IsEqualTo(ScoreState.Trailing);
            await Assert.That(t.Won).IsTrue();
            await Assert.That(ct.Round.Number).IsEqualTo(6);
            await Assert.That(ct.Call).IsNull().Because("nothing names a CT call");
            await Assert.That(ct.LostInARow).IsEqualTo(1);
            await Assert.That(TimeoutDerivation.Build(Rounds(), _ => null).Tactical).IsEmpty();
        }
    }

    [Test]
    public async Task Timeouts_OfADemoWithoutTheTeamsRounds_AreNotTheirs()
    {
        IReadOnlyList<RoundTimeout> stored = [Timeout(2, 2), Timeout(9, 2)];
        TeamTimeouts result = TimeoutDerivation.Build(Rounds().Where(r => r.Number > 4), _ => stored);
        await Assert.That(result.Tactical).IsEmpty();
    }

    [Test]
    public async Task ThePistolLoadout_CountsOnlyTheTeamsPlayers_AndSharesAcrossItsPistolRounds()
    {
        IReadOnlyList<CapturedTeamRound> rounds = Captured();
        PistolKit t = EconomyLoadouts.PistolKitOf(rounds, 2)!;
        PistolKit ct = EconomyLoadouts.PistolKitOf(rounds, 3)!;
        using (Assert.Multiple())
        {
            await Assert.That(t.Players).IsEqualTo(5).Because("the enemy's slot 5 is not ours");
            await Assert.That(t.Armor.Players).IsEqualTo(4);
            await Assert.That(t.Helmet.Players).IsEqualTo(1);
            await Assert.That(t.Utility.Players).IsEqualTo(2);
            await Assert.That(string.Join(", ", t.UtilityItems.Select(i => $"{i.Name}{i.Count}")))
                .IsEqualTo("flash2, smoke1");
            await Assert.That(string.Join(", ", t.UpgradeNames.Select(i => i.Name))).IsEqualTo("P250, Tec-9");
            await Assert.That(t.Upgrade.Players).IsEqualTo(2);
            await Assert.That(t.Defuser.Players).IsEqualTo(0);
            await Assert.That(t.Rounds.Select(r => r.Number)).IsEquivalentTo([1]);
            await Assert.That(ct.Players).IsEqualTo(5);
            await Assert.That(ct.Defuser.Players).IsEqualTo(2);
            await Assert.That(ct.Armor.Players).IsEqualTo(4);
            await Assert.That(ct.UpgradeNames.Single().Name).IsEqualTo("USP-S");
            await Assert.That(ct.Utility.Players).IsEqualTo(1);
            await Assert.That(EconomyLoadouts.PistolKitOf([], 2)).IsNull();
        }
    }

    [Test]
    public async Task LowBuyWeapons_GroupBoughtGunsByBuyType_LeavingOutKit_Enemies_AndFullBuys()
    {
        IReadOnlyList<LowBuyWeapons> low = EconomyLoadouts.LowBuys(Captured());
        string Shape(LowBuyWeapons l) => string.Join(", ", l.Weapons.Select(w => $"{w.Name}×{w.Count}"));
        using (Assert.Multiple())
        {
            await Assert.That(low.Select(l => l.Buy)).IsEquivalentTo([BuyType.Eco, BuyType.Semi, BuyType.Force]);
            await Assert.That(Shape(low[0])).IsEqualTo("P250×1");
            await Assert.That(Shape(low[1])).IsEqualTo("M4A1-S×1, MP9×1");
            await Assert.That(Shape(low[2])).IsEqualTo("MP9×3, FAMAS×1, Galil×1");
            await Assert.That(low[2].Rounds.Select(r => r.Number)).IsEquivalentTo([2, 7]);
            await Assert.That(EconomyLoadouts.NameOf("ak47")).IsEqualTo("AK-47");
            await Assert.That(EconomyLoadouts.NameOf("unknown_item")).IsEqualTo("unknown_item");
        }
    }
}
