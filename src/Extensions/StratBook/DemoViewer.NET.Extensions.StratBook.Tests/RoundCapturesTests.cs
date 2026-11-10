#region

using System.Numerics;
using System.Text;
using CS2OpenSchema.Events;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Services.DemoCache;
using TUnit.Assertions.Enums;
using static DemoViewer.NET.AppTests.RoundIndexTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report captures over synthetic frames: the fold's purchases (pickup plus a cash rise on the same
///     frame), AWP ownership and loss, first-shot trails, timeouts and helmet/kit timeline; the builder's rounds
///     (opening, plant, freeze-time buys, AWP evidence); the reader's holders, pistol loadouts and stale refusal; and
///     the positions tuple's schema 6 extras with the old five-wide shape still read.
/// </summary>
public class RoundCapturesTests
{
    private const string Demo = "/d/match.dem";
    private const int Ct = 3;
    private const int T = 2;

    // ── The fold ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task APickup_IsABuy_OnlyWhenTheBuyersCashRoseOnTheSameFrame()
    {
        RoundCapturesFold fold = new();
        Frames frames = new();
        frames.Seat(1, Ct).Seat(2, Ct);
        fold.EndFrame(100, frames.Cash(1, 0).Cash(2, 0));

        fold.OnEvent(Pickup(1, "weapon_awp"));
        fold.OnEvent(Pickup(2, "knife"));
        fold.EndFrame(101, frames.Cash(1, 4750));

        // Two buys of one slot on one frame share the frame's rise; the cost sits on the first.
        fold.OnEvent(Pickup(2, "vesthelm"));
        fold.OnEvent(Pickup(2, "defuser"));
        fold.EndFrame(102, frames.Cash(2, 1400));

        RoundCaptureFrames result = fold.Finish(200);
        await Assert.That(result.Pickups.Select(p => $"{p.Tick}/{p.Slot}/{p.Item}/{p.Bought}/{p.Cost}"))
            .IsEquivalentTo(["101/1/awp/True/4750", "101/2/knife/False/0", "102/2/vesthelm/True/1400",
                "102/2/defuser/True/0"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AnAwp_IsOwnedFromItsPickup_UntilDeath_AndCountsFramesInHand()
    {
        RoundCapturesFold fold = new();
        Frames frames = new();
        frames.Seat(1, Ct).Cash(1, 0);
        fold.EndFrame(10, frames);
        fold.OnEvent(Pickup(1, "awp"));
        fold.EndFrame(11, frames.Cash(1, 4750));
        fold.OnEvent(new RoundFreezeEndEvent());
        fold.EndFrame(12, frames);
        fold.EndFrame(13, frames.Weapon(1, "CWeaponAWP"));
        fold.EndFrame(14, frames);
        fold.EndFrame(15, frames.Weapon(1, "CKnife"));
        fold.EndFrame(16, frames.Weapon(1, "CWeaponAWP"));
        fold.EndFrame(17, frames.Dead(1));
        RoundCaptureFrames result = fold.Finish(40);

        using (Assert.Multiple())
        {
            await Assert.That(result.AwpSpans.Select(s => $"{s.Slot}/{s.FromTick}/{s.ToTick}/{s.End}"))
                .IsEquivalentTo(["1/11/17/Died"], CollectionOrdering.Matching);
            await Assert.That(result.InHand.Select(r => $"{r.FromTick}-{r.ToTick}x{r.Frames}"))
                .IsEquivalentTo(["13-14x2", "16-16x1"], CollectionOrdering.Matching);
            await Assert.That(result.FreezeEnds.Single().Slots.Single().HasAwp).IsTrue();
        }
    }

    [Test]
    public async Task AnItemRemove_OrASideSwap_EndsOwnership_AndARoundEndListsTheAliveHolders()
    {
        RoundCapturesFold fold = new();
        Frames frames = new();
        frames.Seat(1, Ct).Seat(2, T).Weapon(1, "CWeaponAWP").Weapon(2, "CWeaponAWP");
        fold.EndFrame(10, frames);
        fold.OnEvent(RoundEnd());
        fold.EndFrame(11, frames);
        fold.OnEvent(Remove(1, "weapon_awp"));
        fold.EndFrame(12, frames.Weapon(1, "CKnife"));
        fold.EndFrame(13, frames.Seat(2, Ct).Weapon(2, "CKnife"));
        RoundCaptureFrames result = fold.Finish(20);

        using (Assert.Multiple())
        {
            await Assert.That(result.RoundEnds.Single().AliveWithAwp).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
            await Assert.That(result.AwpSpans.Select(s => $"{s.Slot}/{s.End}"))
                .IsEquivalentTo(["1/Removed", "2/SideSwap"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task TakingAnotherPrimary_EndsTheAwpSpan()
    {
        RoundCapturesFold fold = new();
        Frames frames = new();
        frames.Seat(1, Ct).Cash(1, 0);
        fold.OnEvent(Pickup(1, "awp"));
        fold.EndFrame(10, frames.Cash(1, 4750));
        fold.OnEvent(Pickup(1, "m4a1_silencer"));
        fold.EndFrame(20, frames.Cash(1, 7650));
        RoundCaptureFrames result = fold.Finish(40);

        await Assert.That(result.AwpSpans.Select(s => $"{s.FromTick}/{s.ToTick}/{s.End}"))
            .IsEquivalentTo(["10/20/Removed"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task WithoutARoundEnd_ASpanOverTheRoundsEndTick_IsAliveWithTheAwp()
    {
        RoundCaptureFrames frames = new();
        frames.AwpSpans.Add(new CapturedAwpSpan(1, 5000, 9500, AwpSpanEnd.Died));
        frames.AwpSpans.Add(new CapturedAwpSpan(2, 6100, 8000, AwpSpanEnd.Died));
        frames.InHand.Add(new CapturedInHand(1, 6100, 6200, 50));
        frames.InHand.Add(new CapturedInHand(2, 6100, 6200, 50));
        RoundCapturesDocument document = RoundCapturesBuilder.Build(TwoRounds(), 10000, 64, RoundEventRows.Empty,
            frames, "fp");

        await Assert.That(document.Round(2)!.Awp.Select(a => $"{a.Slot}/{a.AliveWithAwpAtEnd}"))
            .IsEquivalentTo(["1/True", "2/False"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task TheFirstAwpShotOfARound_TrailsThePositionAtZeroToThreeSeconds()
    {
        RoundCapturesFold fold = new(ticksPerSecond: 64);
        Frames frames = new();
        frames.Seat(1, Ct).At(1, 0, 0);
        fold.OnEvent(new RoundFreezeEndEvent());
        fold.EndFrame(1000, frames);
        fold.OnEvent(Fire(1, "weapon_awp"));
        fold.EndFrame(1010, frames.At(1, 5, 0));
        fold.OnEvent(Fire(1, "weapon_awp")); // not the first: no second trail
        fold.EndFrame(1050, frames.At(1, 6, 0));
        fold.EndFrame(1074, frames.At(1, 10, 0));
        fold.EndFrame(1140, frames.At(1, 20, 0));
        fold.EndFrame(1202, frames.At(1, 30, 0));
        fold.EndFrame(1300, frames.At(1, 40, 0));
        // A new round re-arms the first shot.
        fold.OnEvent(new RoundFreezeEndEvent());
        fold.EndFrame(5000, frames);
        fold.OnEvent(Fire(1, "awp"));
        fold.EndFrame(5001, frames.At(1, 50, 0));
        RoundCaptureFrames result = fold.Finish(5002);

        using (Assert.Multiple())
        {
            await Assert.That(result.AwpShots.Count).IsEqualTo(3);
            await Assert.That(result.Trails.Count).IsEqualTo(2);
            CapturedTrail first = result.Trails[0];
            await Assert.That(first.ShotTick).IsEqualTo(1010);
            await Assert.That(first.Points.Select(p => $"{p.Tick}:{p.Position!.Value.X}"))
                .IsEquivalentTo(["1010:5", "1074:10", "1140:20", "1202:30"], CollectionOrdering.Matching);
            await Assert.That(result.Trails[1].Points.Count).IsEqualTo(1).Because("the demo ended inside it");
        }
    }

    [Test]
    public async Task ATimeout_RunsFromTheFlagsRiseToItsFall_WithTheCountsAroundIt()
    {
        RoundCapturesFold fold = new();
        Frames frames = new();
        frames.Seat(1, T).Gamerules(new CaptureMatchState(false, false, false, 3, 3));
        fold.EndFrame(100, frames);
        fold.EndFrame(101, frames.Gamerules(new CaptureMatchState(false, true, false, 3, 3)));
        fold.EndFrame(102, frames.Gamerules(new CaptureMatchState(false, true, false, 3, 2)));
        fold.EndFrame(2000, frames.Gamerules(new CaptureMatchState(false, false, false, 3, 2)));
        fold.EndFrame(3000, frames.Gamerules(new CaptureMatchState(false, false, true, 3, 2)));
        RoundCaptureFrames result = fold.Finish(3100);

        await Assert.That(result.Timeouts.Select(t =>
                $"{t.StartTick}-{(t.EndTick is { } end ? end.ToString(System.Globalization.CultureInfo.InvariantCulture) : "open")}/{t.Side}/{t.Technical}/{t.TLeftBefore}->{t.TLeftAfter}"))
            .IsEquivalentTo(["101-2000/2/False/3->2", "3000-open/0/True/2->2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task HelmetAndKit_AreATimeline_ThePositionsTuplesReadAtTheirTick()
    {
        RoundCapturesFold fold = new();
        Frames frames = new();
        frames.Seat(1, Ct).Flags(1, 0);
        fold.EndFrame(100, frames);
        fold.EndFrame(110, frames.Flags(1, RoundPosition.HelmetFlag | RoundPosition.DefuserFlag));
        fold.EndFrame(120, frames);
        LoadoutFlagsTimeline timeline = fold.Finish(200).Flags;

        using (Assert.Multiple())
        {
            await Assert.That(timeline.FlagsAt(1, 99)).IsEqualTo(-1);
            await Assert.That(timeline.FlagsAt(1, 105)).IsEqualTo(0);
            await Assert.That(timeline.FlagsAt(1, 150)).IsEqualTo(3);
            await Assert.That(timeline.FlagsAt(7, 150)).IsEqualTo(-1);
        }
    }

    // ── The builder and the reader ───────────────────────────────────────────────────────────

    private static RoundFactsRows TwoRounds()
    {
        RoundFacts pistol = Round(1, 1000, 5000);
        pistol.Ct.BuyType = BuyType.Pistol;
        pistol.T.BuyType = BuyType.Pistol;
        pistol.PlantSite = BombSite.B;
        return Facts(pistol, Round(2, 6000, 9000));
    }

    private static RoundEventRows Events() => new(
        [
            new KillEventRow(1500, 6, 1, T, "glock", "CWeaponGlock", true, false, -1, new EventSpot(10, 20, 30, "Ramp")),
            new KillEventRow(1600, 2, 6, Ct, "usp_silencer", "CWeaponHKP2000", false, true, 1, new EventSpot(1, 2, 3, null)),
            new KillEventRow(6500, 1, 7, Ct, "awp", "CWeaponAWP", false, false, -1, new EventSpot(4, 5, 6, "Mid"))
        ],
        [
            new DeathEventRow(1500, 1, 6, Ct, "CWeaponHKP2000", new EventSpot(100.4f, 200.6f, 300, "BombsiteA")),
            new DeathEventRow(1600, 6, 2, T, "CWeaponGlock", null)
        ],
        [new PlantEventRow(3000, 8, 412, new EventSpot(-1, -2, -3, "BombsiteB"))]);

    private static RoundCaptureFrames Collected()
    {
        RoundCaptureFrames frames = new();
        // Round 1 freeze time: slot 1 buys a vest and a flash, slot 6 a p250; a warmup-era buy far back is dropped.
        frames.Pickups.Add(new CapturedPickup(-100000, 1, "awp", true, 4750));
        frames.Pickups.Add(new CapturedPickup(900, 1, "vest", true, 650));
        frames.Pickups.Add(new CapturedPickup(910, 1, "flashbang", true, 200));
        frames.Pickups.Add(new CapturedPickup(920, 6, "p250", true, 300));
        frames.Pickups.Add(new CapturedPickup(950, 2, "knife", false, 0));
        // Round 2: slot 1 buys the AWP in freeze time, slot 3 picks one up mid-round.
        frames.Pickups.Add(new CapturedPickup(5900, 1, "awp", true, 4750));
        frames.Pickups.Add(new CapturedPickup(7000, 3, "awp", false, 0));
        frames.FreezeEnds.Add(new CapturedFreezeEnd(1000,
        [
            new CapturedLoadout(1, Ct, true, false, 0, 100, 0, 800, 850),
            new CapturedLoadout(6, T, true, false, 0, 0, 500, 800, 300)
        ]));
        frames.FreezeEnds.Add(new CapturedFreezeEnd(6002,
        [
            new CapturedLoadout(1, Ct, true, true, 1, 100, 200, 5000, 4750),
            new CapturedLoadout(3, Ct, true, false, 0, 100, 3000, 3000, 0)
        ]));
        frames.InHand.Add(new CapturedInHand(1, 6100, 6400, 200));
        frames.AwpShots.Add(new CapturedShot(6490, 1));
        frames.RoundEnds.Add(new CapturedRoundEnd(9000, [1]));
        frames.Trails.Add(new CapturedTrail(1, 6490,
        [
            new CapturedTrailPoint(6490, new Vector3(4, 5, 6), "Mid", true),
            new CapturedTrailPoint(6554, new Vector3(40, 5, 6), "Mid", true)
        ]));
        frames.Timeouts.Add(new CapturedTimeout(5500, 7400, T, false, 3, 3, 3, 2));
        return frames;
    }

    [Test]
    public async Task TheBuilder_PlacesKillsPlantsBuysAndTimeouts_InTheirRounds()
    {
        RoundCapturesDocument document = RoundCapturesBuilder.Build(TwoRounds(), 10000, 64, Events(), Collected(), "fp");
        CapturedRound one = document.Round(1)!;
        CapturedRound two = document.Round(2)!;

        using (Assert.Multiple())
        {
            await Assert.That(one.Pistol).IsTrue();
            await Assert.That(one.Kills.Select(k => $"{k.Tick}/{k.Opening}/{k.Trade}")).IsEquivalentTo(
                ["1500/True/False", "1600/False/True"], CollectionOrdering.Matching);
            await Assert.That(one.Kills[0].Victim).IsEqualTo(new CaptureSpot(100, 201, 300, "BombsiteA"));
            await Assert.That(one.Kills[0].Attacker).IsEqualTo(new CaptureSpot(10, 20, 30, "Ramp"));
            await Assert.That(one.Kills[1].Victim).IsNull();
            await Assert.That(one.Plant).IsEqualTo(new CapturedPlant(3000, 8, 412, BombSite.B,
                new CaptureSpot(-1, -2, -3, "BombsiteB")));
            await Assert.That(one.Purchases.Select(p => $"{p.Slot}/{p.Item}/{p.FreezeTime}")).IsEquivalentTo(
                ["1/vest/True", "1/flashbang/True", "6/p250/True"], CollectionOrdering.Matching);
            await Assert.That(two.Purchases.Select(p => $"{p.Slot}/{p.Item}/{p.FreezeTime}")).IsEquivalentTo(
                ["1/awp/True"], CollectionOrdering.Matching);
            await Assert.That(two.Kills.Single().Opening).IsTrue();
            await Assert.That(two.FirstShots.Single().Points.Count).IsEqualTo(2);
            await Assert.That(document.Timeouts.Single().BeforeRound).IsEqualTo(2);
            await Assert.That(document.Collected).IsTrue();
        }
    }

    [Test]
    public async Task TheReader_NamesAwpHoldersWithConfidence_AndThePistolLoadouts()
    {
        RoundCapturesDocument document = RoundCapturesBuilder.Build(TwoRounds(), 10000, 64, Events(), Collected(), "fp");
        DemoCaptures captures = Read(document);
        RoundCaptureView two = captures.Round(2)!;
        RoundCaptureView one = captures.Round(1)!;

        IReadOnlyList<AwpHolder> holders = two.AwpHolders(Ct);
        IReadOnlyList<PistolLoadout> pistol = one.PistolLoadouts(Ct);
        using (Assert.Multiple())
        {
            await Assert.That(holders.Select(h => $"{h.Slot}/{h.Confidence}/{h.Saved}")).IsEquivalentTo(
                ["1/High/True"], CollectionOrdering.Matching);
            await Assert.That(two.AwpHolders(Ct, AwpConfidence.Low).Select(h => $"{h.Slot}/{h.Confidence}"))
                .IsEquivalentTo(["1/High", "3/Low"], CollectionOrdering.Matching)
                .Because("a pickup never used is listed only on request");
            await Assert.That(holders[0].Evidence).IsEqualTo(new AwpHolding(1, Ct, true, true, false, 200, 6100, 1, 1,
                true));
            await Assert.That(two.AwpBought(Ct)).IsTrue();
            await Assert.That(two.AwpHolders(T)).IsEmpty();
            await Assert.That(one.AwpHolders(Ct)).IsEmpty().Because("the warmup AWP is outside round 1's buy time");
            PistolLoadout only = pistol.Single();
            await Assert.That((only.Slot, only.Armor, only.Helmet, only.Defuser, only.Pistol, only.Taser, only.Spent))
                .IsEqualTo((1, (bool?)true, (bool?)false, (bool?)false, (string?)null, false, 850));
            await Assert.That(pistol.Single().Utility).IsEquivalentTo(["flashbang"], CollectionOrdering.Matching);
            await Assert.That(one.PistolLoadouts(T).Single().Pistol).IsEqualTo("p250");
            await Assert.That(two.PistolLoadouts(Ct)).IsEmpty();
            await Assert.That(one.Opening!.AttackerSlot).IsEqualTo(6);
        }
    }

    [Test]
    public async Task WithoutTheCollector_OnlyKillsAndThePlantAreIn()
    {
        RoundCapturesDocument document = RoundCapturesBuilder.Build(TwoRounds(), 10000, 64, Events(), null, "fp");
        DemoCaptures captures = Read(document);

        using (Assert.Multiple())
        {
            await Assert.That(captures.Collected).IsFalse();
            await Assert.That(captures.Round(1)!.Kills.Count).IsEqualTo(2);
            await Assert.That(captures.Round(1)!.Plant).IsNotNull();
            await Assert.That(captures.Round(1)!.PistolLoadouts(Ct)).IsEmpty();
            await Assert.That(captures.Timeouts).IsEmpty();
        }
    }

    [Test]
    public async Task TheReader_RefusesAStalePart_AndSaysWhy()
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(Demo, sha: "abc"));
        RoundIndexStore store = new(cache.Data());
        RoundIndexDocument index = Document("de_nuke", "fp-old", (1, 1000, 5000, []));
        RoundCapturesDocument captures =
            RoundCapturesBuilder.Build(TwoRounds(), 10000, 64, Events(), Collected(), "fp-old");
        captures.Demo = new RoundPositionsDemo { Sha256 = "abc" };
        store.Write(Demo, index, new RoundPositionsDocument { Fingerprint = "fp-old" }, "fp-old", captures);

        RoundCapturesReader current = new(store, _ => "fp-old");
        RoundCapturesReader moved = new(store, _ => "fp-new");
        DemoCaptures? read = current.Read(Demo, "de_nuke", "abc", out PositionsMiss hit);
        DemoCaptures? stale = moved.Read(Demo, "de_nuke", "abc", out PositionsMiss miss);
        DemoCaptures? foreign = current.Read(Demo, "de_nuke", "def", out PositionsMiss other);

        using (Assert.Multiple())
        {
            await Assert.That(read).IsNotNull();
            await Assert.That(hit).IsEqualTo(PositionsMiss.None);
            await Assert.That(read!.Round(2)!.Kills.Count).IsEqualTo(1);
            await Assert.That(stale).IsNull();
            await Assert.That(miss).IsEqualTo(PositionsMiss.Stale);
            await Assert.That(moved.StaleCount).IsEqualTo(1);
            await Assert.That(foreign).IsNull();
            await Assert.That(other).IsEqualTo(PositionsMiss.Missing);
            await Assert.That(new RoundCapturesReader(null, null).Read(Demo, null, null, out PositionsMiss none))
                .IsNull();
            await Assert.That(none).IsEqualTo(PositionsMiss.NoIndex);
        }
    }

    [Test]
    public async Task ThePart_RoundTripsThroughItsJson()
    {
        RoundCapturesDocument document = RoundCapturesBuilder.Build(TwoRounds(), 10000, 64, Events(), Collected(), "fp");
        RoundCapturesDocument? back = RoundCapturesDocument.TryDeserialize(document.SerializeUtf8());

        using (Assert.Multiple())
        {
            await Assert.That(back).IsNotNull();
            await Assert.That(back!.Serialize()).IsEqualTo(document.Serialize());
            await Assert.That(RoundCapturesDocument.TryDeserialize(Encoding.UTF8.GetBytes("{nope"))).IsNull();
        }
    }

    // ── The positions tuple ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task AFiveWideTuple_StillReads_WithEveryExtraUnknown()
    {
        RoundPositionsDocument? old = RoundPositionsDocument.TryDeserialize(
            """{"schemaVersion":2,"fingerprint":"x;pos=5","demo":{"stableKey":"k"},"cadenceTicks":64,"places":["A"],"rounds":[{"number":1,"freezeEndTick":10,"ct":[1],"pos":[[[1,2,3,4,0]]]}],"players":[]}""");
        RoundPosition tuple = old!.Rounds[0].At(0)[0];

        using (Assert.Multiple())
        {
            await Assert.That(tuple).IsEqualTo(new RoundPosition(1, 2, 3, 4, 0));
            await Assert.That(tuple.IsBare).IsTrue();
            await Assert.That(tuple.HasHelmet).IsNull();
            await Assert.That(old.Weapons).IsNull();
            await Assert.That(old.WeaponOf(0)).IsNull();
            await Assert.That(RoundPositionsDocument.TryDeserialize(
                """{"rounds":[{"number":1,"pos":[[[1,2,3]]]}]}""")).IsNull().Because("a short tuple does not parse");
        }
    }

    [Test]
    public async Task AnExtendedTuple_RoundTrips_AndABareOneStaysFiveWide()
    {
        RoundPositionsDocument document = new()
        {
            Fingerprint = "fp",
            Places = ["A"],
            Weapons = ["CWeaponAWP"],
            Rounds =
            [
                new RoundPositionsRound
                {
                    Number = 1,
                    Pos = [[new RoundPosition(1, 2, 3, 4, 0, 0, 100, 3, 1250), new RoundPosition(2, 5, 6, 7, 0)]]
                }
            ]
        };
        string json = document.Serialize();
        RoundPositionsDocument back = RoundPositionsDocument.TryDeserialize(json)!;

        using (Assert.Multiple())
        {
            await Assert.That(json).Contains("[[[1,2,3,4,0,0,100,3,1250],[2,5,6,7,0]]]");
            await Assert.That(back.Rounds[0].At(0)[0]).IsEqualTo(new RoundPosition(1, 2, 3, 4, 0, 0, 100, 3, 1250));
            await Assert.That(back.Rounds[0].At(0)[0].HasHelmet).IsTrue();
            await Assert.That(back.WeaponOf(0)).IsEqualTo("CWeaponAWP");
            await Assert.That(new RoundPositionsDocument().Serialize()).DoesNotContain("weapons");
        }
    }

    [Test]
    public async Task TheBuilder_PutsEachSamplesWeaponArmourMoneyAndFlags_OnItsTuple()
    {
        LoadoutFlagsTimeline flags = new();
        flags.Add(1, 0, RoundPosition.HelmetFlag);
        Dictionary<(int Tick, int Slot), PositionExtra> extras = new()
        {
            [(1000, 1)] = new PositionExtra("CWeaponAWP", 100, 250),
            [(1000, 6)] = new PositionExtra("CAK47", 0, -1)
        };
        RoundIndexBuild build = RoundIndexBuilder.BuildWithPositions(RoundIndexDemoValues.For(RoundIndexTestData.Demo()),
            Facts(Round(1, 1000, 2000)), RoundIndexOptions.Default, PawnPlaceSource.Instance,
            [
                Sample(1000, 1, "A", 1, 2, 3), Sample(1000, 6, "B", 4, 5, 6), Sample(1000, 7, "B", 4, 5, 6)
            ], extras, flags);
        IReadOnlyList<RoundPosition> tuples = build.Positions.Rounds[0].At(0);

        using (Assert.Multiple())
        {
            await Assert.That(build.Positions.Weapons).IsEquivalentTo(["CWeaponAWP", "CAK47"], CollectionOrdering.Matching);
            await Assert.That(tuples[0] with { X = 0, Y = 0, Z = 0, PlaceId = 0 })
                .IsEqualTo(new RoundPosition(1, 0, 0, 0, 0, 0, 100, RoundPosition.HelmetFlag, 250));
            await Assert.That(tuples[1].WeaponId).IsEqualTo(1);
            await Assert.That(tuples[1].Flags).IsEqualTo(-1);
            await Assert.That(tuples[2].IsBare).IsTrue().Because("slot 7 has no extras row and no flags");
        }
    }

    private static ItemPickupEvent Pickup(int slot, string item) =>
        new() { UserId = slot, Item = item, Silent = false, DefIndex = 0 };

    private static ItemRemoveEvent Remove(int slot, string item) => new() { UserId = slot, Item = item, DefIndex = 0 };

    private static WeaponFireEvent Fire(int slot, string weapon) =>
        new() { UserId = slot, UserIdPawn = 0, Weapon = weapon, Silenced = false };

    private static RoundEndEvent RoundEnd() =>
        new() { Winner = 3, Reason = 8, Message = "", Legacy = 0, PlayerCount = 10, NoMusic = 0 };

    private static DemoCaptures Read(RoundCapturesDocument document)
    {
        DemoCacheStore cache = new(null);
        cache.Upsert(ParsedRecord(Demo));
        RoundIndexStore store = new(cache.Data());
        store.Write(Demo, Document("de_nuke", "fp", (1, 1000, 5000, [])), new RoundPositionsDocument(), "fp",
            document);
        return new RoundCapturesReader(store, null).Read(Demo, "de_nuke", null)!;
    }

    /// <summary>A frame's reads, kept between frames so a test changes only what moves.</summary>
    private sealed class Frames : ICaptureFrameReads
    {
        private readonly SortedDictionary<int, CaptureSlotState> _slots = [];
        private readonly Dictionary<int, Vector3> _at = [];

        public IReadOnlyList<CaptureSlotState> Slots => [.. _slots.Values];

        public CaptureMatchState? Match { get; private set; }

        public (Vector3 Position, string? Place)? Position(int slot) =>
            _at.TryGetValue(slot, out Vector3 at) ? (at, "Mid") : null;

        public CaptureEconomy Economy(int slot) => new(100, 800, 800);

        public Frames Seat(int slot, int team)
        {
            _slots[slot] = _slots.TryGetValue(slot, out CaptureSlotState s)
                ? s with { Team = team }
                : new CaptureSlotState(slot, team, true, "CKnife", -1, -1);
            return this;
        }

        public Frames Cash(int slot, int cash) => Set(slot, s => s with { CashSpent = cash });

        public Frames Weapon(int slot, string weapon) => Set(slot, s => s with { WeaponClass = weapon });

        public Frames Dead(int slot) => Set(slot, s => s with { Alive = false });

        public Frames Flags(int slot, int flags) => Set(slot, s => s with { Flags = flags });

        public Frames At(int slot, float x, float y)
        {
            _at[slot] = new Vector3(x, y, 0);
            return this;
        }

        public Frames Gamerules(CaptureMatchState state)
        {
            Match = state;
            return this;
        }

        private Frames Set(int slot, Func<CaptureSlotState, CaptureSlotState> change)
        {
            _slots[slot] = change(_slots[slot]);
            return this;
        }
    }
}
