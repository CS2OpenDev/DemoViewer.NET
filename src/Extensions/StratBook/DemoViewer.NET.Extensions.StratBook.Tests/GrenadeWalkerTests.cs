#region

using System.Globalization;
using System.Numerics;
using CS2DemoKit.Analysis.Clips;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using DemoViewer.NET.Extensions.StratBook.Modules.UtilityBook;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The grenade walk's pure steps on synthetic table rows: the named thresholds, jump-throw for every
///     (coverage, press, ground flag) case, the console line, the polyline thinning, the thrower and release
///     plan, the detonation per kind, and a whole row assembled from hand-made facts. No demo is parsed.
/// </summary>
public class GrenadeWalkerTests
{
    private const int Slot = 3;
    private const int Index = 120;
    private const int Serial = 7;

    private static readonly int[] _bounceTicks = [117, 133, 151];

    // A pawn read with the fields a row prints.
    private static ThrowerRead Read(int tick, Vector3? at, float? pitch = null, float? yaw = null,
        bool? onGround = null, bool? ducked = null,
        int team = 2, float? strength = null, bool held = false) =>
        new(tick, at, pitch, yaw, onGround, ducked, team, strength, held);

    private static ReleaseFacts Release(int slot, int tick, string weapon, ThrowerRead? pawn = null,
        Vector3? before = null) =>
        new(slot, tick, GrenadeRules.ProjectileClassOfWeapon(weapon)!, weapon, pawn ?? Read(tick, null, team: 0),
            before);

    // An HE thrown at tick 100, flying east, bouncing twice, at rest from 190, removed on 204.
    private static ProjectileFacts He(int thrower = Slot, ThrowerAtSpawn? atSpawn = null, ThrowerRead? offset = null,
        Vector3? offsetBefore = null,
        int? explode = null, int index = Index, bool removed = true) =>
        new(index, Serial, GrenadeProjectileClasses.HEGrenade, thrower, 100, 204, removed, new Vector3(0, 0, 64),
            new Vector3(500, 0, 100),
            new Vector3(460, 0, 64), 190, 2, null, explode, null, null, atSpawn, offset, offsetBefore);

    private static ProjectileFacts Facts(int index, string cls, int thrower, int spawn, int end, Vector3? last = null,
        bool removed = true,
        int? smokeTick = null, Vector3? smokeAt = null) =>
        new(index, Serial, cls, thrower, spawn, end, removed, thrower >= 0 ? new Vector3(1, 1, 1) : null, null,
            last, end, 0, null, null,
            smokeTick, smokeAt, thrower >= 0 ? new ThrowerAtSpawn(new Vector3(1, 1, 1), true, 2, -1, 0) : null, null,
            null);

    private static GrenadeTrajectories NoFlights => new(new Dictionary<(int, int), List<TrajectoryPoint>>(), 4);

    // ── thresholds and classification ────────────────────────────────────────────────────────────────

    [Test]
    [Arguments(0f, MovementClass.Stationary)]
    [Arguments(9.99f, MovementClass.Stationary)]
    [Arguments(10f, MovementClass.Walking)]
    [Arguments(139.9f, MovementClass.Walking)]
    [Arguments(140f, MovementClass.Running)]
    [Arguments(245f, MovementClass.Running)]
    public async Task Movement_SplitsAtTheNamedThresholds(float speed, MovementClass expected) =>
        await Assert.That(GrenadeRules.ClassifyMovement(speed)).IsEqualTo(expected);

    [Test]
    public async Task Movement_WithoutASpeed_IsUnknown() =>
        await Assert.That(GrenadeRules.ClassifyMovement(null)).IsEqualTo(MovementClass.Unknown);

    [Test]
    [Arguments(1.0f, true, ThrowStrengthClass.Full)]
    [Arguments(0.95f, true, ThrowStrengthClass.Full)]
    [Arguments(0.5f, true, ThrowStrengthClass.Half)]
    [Arguments(0.0f, true, ThrowStrengthClass.Underhand)]
    [Arguments(0.8f, true, ThrowStrengthClass.Other)]
    [Arguments(1.0f, false, ThrowStrengthClass.Unknown)]
    public async Task Strength_ClassesAtTheNamedThresholds_AndUnknownOffAGrenade(float strength, bool held,
        ThrowStrengthClass expected) =>
        await Assert.That(GrenadeRules.ClassifyStrength(strength, held)).IsEqualTo(expected);

    [Test]
    public async Task JumpThrow_InputsDecideWhenTheyCoverTheWindow()
    {
        using (Assert.Multiple())
        {
            // Covered with a press: a jump-throw, whatever the ground flag says (the press-after-release bind
            // leaves the pawn grounded at the release but not at the spawn).
            await Assert.That(GrenadeRules.ClassifyJumpThrow(true, 95, true))
                .IsEqualTo((true, JumpThrowSource.Inputs, (int?)95));
            // Covered with no press: standing, even when the pawn is airborne (a ledge drop).
            await Assert.That(GrenadeRules.ClassifyJumpThrow(true, null, false))
                .IsEqualTo((false, JumpThrowSource.Inputs, (int?)null));
            // Not covered: the spawn frame's ground flag.
            await Assert.That(GrenadeRules.ClassifyJumpThrow(false, null, false))
                .IsEqualTo((true, JumpThrowSource.GroundFlag, (int?)null));
            await Assert.That(GrenadeRules.ClassifyJumpThrow(false, null, true))
                .IsEqualTo((false, JumpThrowSource.GroundFlag, (int?)null));
            // Nothing to read at all.
            await Assert.That(GrenadeRules.ClassifyJumpThrow(false, null, null))
                .IsEqualTo((false, JumpThrowSource.None, (int?)null));
        }
    }

    [Test]
    public async Task Kind_TellsAMolotovFromAnIncendiaryByTheWeaponFirst()
    {
        using (Assert.Multiple())
        {
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, "weapon_incgrenade", false))
                .IsEqualTo(GrenadeKind.Incendiary);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, "weapon_molotov", true))
                .IsEqualTo(GrenadeKind.Molotov);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, null, true))
                .IsEqualTo(GrenadeKind.Incendiary);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Molotov, null, null))
                .IsEqualTo(GrenadeKind.Molotov);
            await Assert.That(GrenadeRules.KindOf(GrenadeProjectileClasses.Flashbang, null, null))
                .IsEqualTo(GrenadeKind.Flash);
            await Assert.That(GrenadeRules.KindOf("CBaseEntity", null, null)).IsNull();
            await Assert.That(GrenadeRules.ProjectileClassOfWeapon("weapon_smokegrenade"))
                .IsEqualTo(GrenadeProjectileClasses.Smoke);
            await Assert.That(GrenadeRules.ProjectileClassOfWeapon("incgrenade"))
                .IsEqualTo(GrenadeProjectileClasses.Molotov);
            await Assert.That(GrenadeRules.ProjectileClassOfWeapon("weapon_ak47")).IsNull();
        }
    }

    [Test]
    public async Task Console_PrintsTwoDecimalsInTheInvariantCulture_AndNothingWithoutARelease()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            GrenadeRow row = new()
            {
                ReleasePosition = new WorldPoint(-2260f, -1036.004f, -414.456f),
                ReleaseEyePitch = -18.1249f,
                ReleaseEyeYaw = -15.3f
            };

            using (Assert.Multiple())
            {
                await Assert.That(GrenadeConsole.Format(row))
                    .IsEqualTo("setpos -2260.00 -1036.00 -414.46; setang -18.12 -15.30 0.00");
                await Assert.That(GrenadeConsole.Format(new GrenadeRow { ReleasePosition = new WorldPoint(1, 2, 3) }))
                    .IsNull()
                    .Because("no eye angles is no setang, never a zero one");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // ── the polyline ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(1000)]
    public async Task Trajectory_KeepsTheFirstTheLastAndEveryBounceVertex_AtAnyStride(int stride)
    {
        TrajectoryBuilder builder = new(stride);
        builder.Start(100, new Vector3(0, 0, 64));
        int bounces = 0;
        for (int t = 101; t <= 160; t++)
        {
            if (t is 117 or 133 or 151)
            {
                bounces++;
            }

            builder.Offer(t, new Vector3((t - 100) * 3, 0, 64), bounces);
        }

        // At rest: the same point again is not movement.
        await Assert.That(builder.Offer(161, new Vector3(180, 0, 64), bounces)).IsFalse();
        List<TrajectoryPoint> points = builder.Finish();

        using (Assert.Multiple())
        {
            await Assert.That(points[0]).IsEqualTo(new TrajectoryPoint(100, 0, 0, 64, 0));
            await Assert.That(points[^1].Tick).IsEqualTo(160);
            foreach (int bounceTick in _bounceTicks)
            {
                await Assert.That(points.Any(p => p.Tick == bounceTick)).IsTrue()
                    .Because($"bounce at {bounceTick}, stride {stride}");
            }

            await Assert.That(points.Zip(points.Skip(1)).All(pair => pair.First.Tick < pair.Second.Tick)).IsTrue();
            if (stride == 1)
            {
                await Assert.That(points.Count).IsEqualTo(61);
            }
        }
    }

    // ── Thrower and release ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Plan_AResolvedThrowerTakesItsLatestFireInTheLookback()
    {
        ReleaseFacts latest = Release(Slot, 93, "weapon_hegrenade", Read(93, new Vector3(0, 0, 0)),
            new Vector3(-40, 0, 0));
        List<ReleaseFacts> releases =
        [
            Release(Slot, 40, "weapon_hegrenade"), // outside the 40-tick lookback
            Release(Slot, 90, "weapon_hegrenade"),
            latest,
            Release(4, 94, "weapon_hegrenade"), // another slot
            Release(Slot, 95, "weapon_flashbang") // another family
        ];

        GrenadeThrow plan = GrenadeWalker.Plan([He()], releases).Single();

        using (Assert.Multiple())
        {
            await Assert.That(plan.ThrowerSlot).IsEqualTo(Slot);
            await Assert.That(plan.ThrowerSource).IsEqualTo(ThrowerSource.Entity);
            await Assert.That(plan.ReleaseSource).IsEqualTo(ReleaseSource.WeaponFire);
            await Assert.That(plan.ReleaseTick).IsEqualTo(93);
            await Assert.That(plan.Release).IsEqualTo(latest.Pawn);
            await Assert.That(plan.Before).IsEqualTo(new Vector3(-40, 0, 0));
            await Assert.That(plan.Weapon).IsEqualTo("weapon_hegrenade");
        }
    }

    [Test]
    public async Task Plan_AnUnresolvedSmokeJoinsTheUnclaimedFireNearestTheSevenTickGap()
    {
        // Two smokes spawn at 200; one has its thrower, one only its landing, its chain lost (#56).
        List<ProjectileFacts> projectiles =
        [
            Facts(1, GrenadeProjectileClasses.Smoke, 1, 200, 1400),
            Facts(2, GrenadeProjectileClasses.Smoke, -1, 200, 1400)
        ];
        List<ReleaseFacts> releases =
        [
            Release(8, 180, "weapon_smokegrenade"), // 20 ticks before: outside [spawn - 15, spawn - 6]
            Release(6, 190, "weapon_smokegrenade"),
            Release(1, 193, "weapon_smokegrenade")
        ];

        List<GrenadeThrow> plans = GrenadeWalker.Plan(projectiles, releases);

        using (Assert.Multiple())
        {
            await Assert.That(plans[0].ThrowerSlot).IsEqualTo(1);
            await Assert.That(plans[0].ReleaseTick).IsEqualTo(193);
            await Assert.That(plans[1].ThrowerSlot).IsEqualTo(6).Because("slot 1's fire is claimed by its own smoke");
            await Assert.That(plans[1].ThrowerSource).IsEqualTo(ThrowerSource.WeaponFire);
            await Assert.That(plans[1].ReleaseTick).IsEqualTo(190);
        }
    }

    // A projectile whose chain was lost, appearing at a known position.
    private static ProjectileFacts Lost(int index, string cls, int spawn, Vector3? at) =>
        Facts(index, cls, -1, spawn, spawn + 1000) with { InitialPosition = at };

    // A release with the thrower standing at a position.
    private static ReleaseFacts ReleaseAt(int slot, int tick, string weapon, Vector3? at) =>
        Release(slot, tick, weapon, Read(tick, at));

    [Test]
    public async Task Plan_AShortGapIsADeathMidThrow_AndStillJoins()
    {
        // The pro case: weapon_fire at 73162, the thrower killed at 73164, the HE at rest on 73165.
        List<ProjectileFacts> projectiles =
        [
            Facts(1, GrenadeProjectileClasses.HEGrenade, 7, 3165, 3589),
            Lost(2, GrenadeProjectileClasses.Smoke, 5000, new Vector3(10, 0, 64))
        ];
        List<ReleaseFacts> releases =
        [
            ReleaseAt(7, 3162, "weapon_hegrenade", new Vector3(0, 0, 0)),
            ReleaseAt(4, 4997, "weapon_smokegrenade", new Vector3(0, 0, 0))
        ];

        List<GrenadeThrow> plans = GrenadeWalker.Plan(projectiles, releases);

        using (Assert.Multiple())
        {
            await Assert.That(plans[0].ReleaseSource).IsEqualTo(ReleaseSource.WeaponFire);
            await Assert.That(plans[0].ReleaseTick).IsEqualTo(3162);
            await Assert.That(plans[1].ThrowerSlot).IsEqualTo(4).Because("a 3-tick gap no longer drops the join");
            await Assert.That(plans[1].ThrowerSource).IsEqualTo(ThrowerSource.WeaponFire);
            await Assert.That(plans[1].ReleaseTick).IsEqualTo(4997);
        }
    }

    [Test]
    public async Task Plan_ACompletedThrowsGapBeatsAShortOne_EvenAJumpThrows()
    {
        // Slot 5 jump-throws 14 ticks before; slot 6 fires 3 ticks before, nearer the seven-tick median.
        List<ProjectileFacts> projectiles = [Lost(1, GrenadeProjectileClasses.Flashbang, 300, new Vector3(0, 0, 64))];
        List<ReleaseFacts> releases =
        [
            ReleaseAt(5, 286, "weapon_flashbang", new Vector3(20, 0, 0)),
            ReleaseAt(6, 297, "weapon_flashbang", new Vector3(-20, 0, 0))
        ];

        GrenadeThrow plan = GrenadeWalker.Plan(projectiles, releases).Single();

        await Assert.That(plan.ThrowerSlot).IsEqualTo(5);
    }

    [Test]
    public async Task Plan_TwoPlayersThrowingTogether_JoinByWhereTheyStood()
    {
        // Two lost smokes on one tick, thrown from opposite ends of the map; by the tick gap alone the first
        // would take slot 2's seven-tick release.
        List<ProjectileFacts> projectiles =
        [
            Lost(1, GrenadeProjectileClasses.Smoke, 200, new Vector3(1000, 0, 64)),
            Lost(2, GrenadeProjectileClasses.Smoke, 200, new Vector3(-1000, 0, 64))
        ];
        List<ReleaseFacts> releases =
        [
            ReleaseAt(2, 192, "weapon_smokegrenade", new Vector3(1010, 20, 0)),
            ReleaseAt(1, 193, "weapon_smokegrenade", new Vector3(-990, -15, 0))
        ];

        List<GrenadeThrow> plans = GrenadeWalker.Plan(projectiles, releases);

        using (Assert.Multiple())
        {
            await Assert.That(plans[0].ThrowerSlot).IsEqualTo(2);
            await Assert.That(plans[1].ThrowerSlot).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Plan_AReleaseTooFarFromTheSpawn_LeavesTheGrenadeUnjoinedRatherThanMisnamed()
    {
        List<ProjectileFacts> projectiles = [Lost(1, GrenadeProjectileClasses.Smoke, 200, new Vector3(0, 0, 64))];
        List<ReleaseFacts> releases =
        [
            ReleaseAt(2, 193, "weapon_smokegrenade",
                new Vector3(GrenadeRules.ReleaseToSpawnMaxDistance + 1, 0, 0))
        ];

        GrenadeThrow plan = GrenadeWalker.Plan(projectiles, releases).Single();

        await Assert.That(plan.ThrowerSource).IsEqualTo(ThrowerSource.None);
    }

    [Test]
    public async Task Plan_OnePlayersRapidThrows_EachTakeTheirOwnRelease()
    {
        // Two flashes 30 ticks apart, both resolved, and two smokes by one player with their chains lost.
        List<ProjectileFacts> projectiles =
        [
            Facts(1, GrenadeProjectileClasses.Flashbang, Slot, 108, 212),
            Facts(2, GrenadeProjectileClasses.Flashbang, Slot, 137, 241),
            Lost(3, GrenadeProjectileClasses.Smoke, 507, new Vector3(5, 5, 64)),
            Lost(4, GrenadeProjectileClasses.Smoke, 540, new Vector3(30, 5, 64))
        ];
        List<ReleaseFacts> releases =
        [
            Release(Slot, 100, "weapon_flashbang"),
            Release(Slot, 130, "weapon_flashbang"),
            ReleaseAt(8, 500, "weapon_smokegrenade", new Vector3(0, 0, 0)),
            ReleaseAt(8, 532, "weapon_smokegrenade", new Vector3(25, 0, 0))
        ];

        List<GrenadeThrow> plans = GrenadeWalker.Plan(projectiles, releases);

        await Assert.That(string.Join(" ", plans.Select(p => $"{p.ThrowerSlot}@{p.ReleaseTick}")))
            .IsEqualTo($"{Slot}@100 {Slot}@130 8@500 8@532");
    }

    [Test]
    public async Task Plan_FallsBackToTheSpawnOffsetAndTheRowsLookBacks_OnlyForAResolvedThrower()
    {
        ThrowerRead offset = Read(93, new Vector3(7, 7, 7), -1, 2, true, false, 2, 1f, true);
        List<ProjectileFacts> projectiles =
        [
            He(offset: offset, offsetBefore: new Vector3(-1, 7, 0)),
            Facts(2, GrenadeProjectileClasses.Decoy, -1, 500, 900)
        ];

        List<GrenadeThrow> plans = GrenadeWalker.Plan(projectiles, []);

        using (Assert.Multiple())
        {
            await Assert.That(plans[0].ReleaseSource).IsEqualTo(ReleaseSource.SpawnOffset);
            await Assert.That(plans[0].ReleaseTick).IsEqualTo(100 - GrenadeRules.ReleaseToSpawnTicks);
            await Assert.That(plans[0].Release).IsEqualTo(offset);
            await Assert.That(plans[0].Before).IsEqualTo(new Vector3(-1, 7, 0));
            await Assert.That(plans[0].Weapon).IsNull();
            await Assert.That(plans[1].ThrowerSlot).IsEqualTo(-1);
            await Assert.That(plans[1].ThrowerSource).IsEqualTo(ThrowerSource.None);
            await Assert.That(plans[1].Release).IsNull();
        }
    }

    [Test]
    public async Task Plan_AGrenadeInTheFirstSnapshot_WasThrownBeforeTheRecording_AndNothingAtItsSpawnIsRead()
    {
        // Five decoys first seen on tick 1 of a matchmaking demo, already bounced: their weapon_fire predates it.
        ThrowerRead offset = Read(-6, new Vector3(7, 7, 7), -1, 2, true, false, 2, 1f, true);
        ProjectileFacts inFlight = Facts(1, GrenadeProjectileClasses.Decoy, Slot, 1, 65) with
        {
            AtSpawn = new ThrowerAtSpawn(new Vector3(1, 1, 1), true, 3, -1, 0, true), OffsetRelease = offset
        };
        ProjectileFacts thrown = He(atSpawn: new ThrowerAtSpawn(null, true, 2, -1, 1, true), index: 2);
        GrenadeTableRows tables = new([inFlight, thrown], [Release(Slot, 93, "weapon_hegrenade")],
            GrenadeEventIndex.FromDetonations([]));

        GrenadeWalk walk = GrenadeWalker.Assemble(new GrenadeWalkInputs(tables, NoFlights), [], [], 64, 0);
        GrenadeRow row = walk.Rows[0];

        using (Assert.Multiple())
        {
            await Assert.That(row.ReleaseSource).IsEqualTo(ReleaseSource.BeforeRecording);
            await Assert.That(row.ReleaseTick).IsEqualTo(1);
            await Assert.That(row.ReleasePosition).IsNull().Because("the -7 look-back is from before the demo");
            await Assert.That(row.ThrowerSlot).IsEqualTo(Slot);
            await Assert.That(row.ThrowerTeam).IsEqualTo(3);
            await Assert.That(row.ThrowerOnGroundAtSpawn).IsNull();
            await Assert.That(row.ThrowerPositionAtSpawn).IsNull();
            await Assert.That(row.JumpThrowSource).IsEqualTo(JumpThrowSource.None);
            await Assert.That(walk.Rows[1].ReleaseSource).IsEqualTo(ReleaseSource.WeaponFire);
            await Assert.That(walk.InputCoverage).IsEqualTo(1.0)
                .Because("a grenade thrown before the recording has no throw for the commands to cover");
        }
    }

    [Test]
    public async Task Plan_ADeadThrowersGrenadeWithNoFire_IsADeathDrop_AndOneWithAFireIsStillAThrow()
    {
        // A flash that appeared the tick after its holder's death, with no weapon_fire anywhere before it.
        ThrowerRead offset = Read(93, new Vector3(7, 7, 7), -1, 2, true, false, 2, 1f, true);
        ThrowerAtSpawn dead = new(new Vector3(1, 1, 1), false, 2, -1, 1, false);
        List<ProjectileFacts> projectiles =
        [
            He(atSpawn: dead, offset: offset, index: 1),
            He(thrower: 4, atSpawn: dead, offset: offset, index: 2),
            He(thrower: 5, atSpawn: dead with { Alive = null }, offset: offset, index: 3),
            He(thrower: 6, atSpawn: dead with { Alive = true }, offset: offset, index: 4)
        ];

        // Slot 4 released at 97 and died before the projectile appeared at 100: killed mid-throw, a throw.
        List<GrenadeThrow> plans = GrenadeWalker.Plan(projectiles, [Release(4, 97, "weapon_hegrenade")]);

        using (Assert.Multiple())
        {
            await Assert.That(plans[0].ReleaseSource).IsEqualTo(ReleaseSource.DeathDrop);
            await Assert.That(plans[0].ReleaseTick).IsEqualTo(100);
            await Assert.That(plans[0].Release).IsNull().Because("nothing was thrown, so there is no lineup");
            await Assert.That(plans[0].Before).IsNull();
            await Assert.That(plans[1].ReleaseSource).IsEqualTo(ReleaseSource.WeaponFire);
            await Assert.That(plans[1].ReleaseTick).IsEqualTo(97);
            await Assert.That(plans[2].ReleaseSource).IsEqualTo(ReleaseSource.SpawnOffset)
                .Because("an unread life state is not a death");
            await Assert.That(plans[3].ReleaseSource).IsEqualTo(ReleaseSource.SpawnOffset);
        }
    }

    // ── detonation per kind ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Detonation_TheEventByEntityWins_ThenTheEntityField_ThenTheLastSample()
    {
        Vector3 pop = new(460, 0, 64);
        GrenadeEventIndex events = GrenadeEventIndex.FromDetonations(
        [
            new GrenadeDetonation(GrenadeEventIndex.HeDetonate, 204, Index, pop),
            new GrenadeDetonation(GrenadeEventIndex.HeDetonate, 150, Index + 1, Vector3.Zero)
        ]);

        (int? tick, Vector3? position, DetonationSource source, _) =
            GrenadeWalker.ResolveDetonation(He(), events, 0, []);
        await Assert.That((tick, position, source)).IsEqualTo(((int?)204, (Vector3?)pop, DetonationSource.Event));

        // The same index reused by a later projectile: its event is not this one's.
        GrenadeEventIndex later =
            GrenadeEventIndex.FromDetonations([new GrenadeDetonation(GrenadeEventIndex.HeDetonate, 900, Index, pop)]);
        (tick, _, source, _) = GrenadeWalker.ResolveDetonation(He(), later, 0, []);
        await Assert.That((tick, source)).IsEqualTo(((int?)203, DetonationSource.LastSample))
            .Because("the last sample sits the tick before the removal");

        (tick, _, source, _) =
            GrenadeWalker.ResolveDetonation(He(explode: 1204), GrenadeEventIndex.FromDetonations([]), 1000, []);
        await Assert.That((tick, source)).IsEqualTo(((int?)204, DetonationSource.Entity))
            .Because("the effect tick is server clock: minus the server start tick");
    }

    [Test]
    public async Task Detonation_ASmokeFallsBackToItsPopFields_AndANeverPoppedOneIsNone()
    {
        Vector3 at = new(100, 200, 30);
        GrenadeEventIndex none = GrenadeEventIndex.FromDetonations([]);

        (int? tick, Vector3? position, DetonationSource source, _) = GrenadeWalker.ResolveDetonation(
            Facts(Index, GrenadeProjectileClasses.Smoke, Slot, 10, 11, new Vector3(9, 2, 3), smokeTick: 70,
                smokeAt: at), none, 10, []);
        await Assert.That((tick, position, source)).IsEqualTo(((int?)60, (Vector3?)at, DetonationSource.Entity));

        (tick, _, source, _) =
            GrenadeWalker.ResolveDetonation(
                Facts(Index, GrenadeProjectileClasses.Smoke, Slot, 10, 11, new Vector3(9, 2, 3)), none, 0, []);
        await Assert.That((tick, source)).IsEqualTo(((int?)null, DetonationSource.None));
    }

    [Test]
    public async Task Detonation_AMolotovTakesTheNearestUnclaimedFireWithinFourTicks()
    {
        ProjectileFacts molotov = Facts(Index, GrenadeProjectileClasses.Molotov, Slot, 10, 41, new Vector3(300, 0, 0));
        GrenadeEventIndex events = GrenadeEventIndex.FromDetonations(
        [
            new GrenadeDetonation(GrenadeEventIndex.InfernoStart, 47, 900,
                new Vector3(300, 0, 0)), // six ticks after the removal
            new GrenadeDetonation(GrenadeEventIndex.InfernoStart, 42, 901, new Vector3(2000, 0, 0)),
            new GrenadeDetonation(GrenadeEventIndex.InfernoStart, 42, 902, new Vector3(305, 0, 0))
        ]);
        HashSet<GrenadeDetonation> claimed = [];

        (int? tick, _, DetonationSource source, int? inferno) =
            GrenadeWalker.ResolveDetonation(molotov, events, 0, claimed);

        using (Assert.Multiple())
        {
            await Assert.That((tick, source, inferno)).IsEqualTo(((int?)42, DetonationSource.Event, (int?)902));
            await Assert.That(claimed.Count).IsEqualTo(1);
        }

        (_, _, _, inferno) = GrenadeWalker.ResolveDetonation(molotov, events, 0, claimed);
        await Assert.That(inferno).IsEqualTo(901).Because("the first fire is claimed");
    }

    // ── a whole row ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Assemble_BuildsTheRowFromTheThrowTheEventsAndTheFlight()
    {
        // Eight ticks before the release, 40 units east: 40 * 64 / 8 = 320 u/s, running. The jump rose one
        // tick after the release, inside the window; the thrower was airborne at the spawn.
        ProjectileFacts he = He(atSpawn: new ThrowerAtSpawn(new Vector3(20, 0, 30), false, 2, 94, 1));
        List<ReleaseFacts> releases =
        [
            Release(Slot, 93, "weapon_hegrenade",
                Read(93, new Vector3(0, 0, 0), -12.5f, 90f, true, true, 2, 1.0f, true), new Vector3(-40, 0, 0))
        ];
        GrenadeEventIndex events = GrenadeEventIndex.FromDetonations([
            new GrenadeDetonation(GrenadeEventIndex.HeDetonate, 204, Index, new Vector3(460, 0, 64))
        ]);
        GrenadeTrajectories flights =
            new(
                new Dictionary<(int, int), List<TrajectoryPoint>>
                {
                    [(Index, Serial)] =
                        [new TrajectoryPoint(100, 0, 0, 64, 0), new TrajectoryPoint(190, 460, 0, 64, 2)]
                }, 4);
        PlayerInfo thrower = new(Slot, "thrower", 76561198000000001UL, 12, 2, false);

        GrenadeWalk walk = GrenadeWalker.Assemble(
            new GrenadeWalkInputs(new GrenadeTableRows([he], releases, events), flights),
            [new ClipRound(1, 10), new ClipRound(2, 90)], [thrower], 64, 0);
        GrenadeRow row = walk.Rows.Single();

        using (Assert.Multiple())
        {
            await Assert.That(walk.InputCoverage).IsEqualTo(1);
            await Assert.That(walk.InputDecoder).IsEqualTo(GrenadeWalker.InputDecoderName);
            await Assert.That(walk.TrajectoryStride).IsEqualTo(4);
            await Assert.That(row.Kind).IsEqualTo(GrenadeKind.He);
            await Assert.That(row.ThrowerSteamId64).IsEqualTo("76561198000000001");
            await Assert.That(row.ThrowerName).IsEqualTo("thrower");
            await Assert.That(row.ThrowerTeam).IsEqualTo(2);
            await Assert.That(row.RoundNumber).IsEqualTo(2);
            await Assert.That(row.ReleaseTick).IsEqualTo(93);
            await Assert.That(row.ReleasePosition).IsEqualTo(new WorldPoint(0, 0, 0));
            await Assert.That(row.ReleaseEyePitch).IsEqualTo(-12.5f);
            await Assert.That(row.ReleaseEyeYaw).IsEqualTo(90f);
            await Assert.That(row.ReleaseOnGround).IsTrue();
            await Assert.That(row.ReleaseCrouched).IsTrue();
            await Assert.That(row.ThrowStrengthClass).IsEqualTo(ThrowStrengthClass.Full);
            await Assert.That(row.SpeedAtRelease).IsEqualTo(320f);
            await Assert.That(row.Movement).IsEqualTo(MovementClass.Running);
            await Assert.That(row.SpawnPosition).IsEqualTo(new WorldPoint(0, 0, 64));
            await Assert.That(row.ThrowerPositionAtSpawn).IsEqualTo(new WorldPoint(20, 0, 30));
            await Assert.That(row.ThrowerOnGroundAtSpawn).IsFalse();
            await Assert.That(row.JumpThrow).IsTrue();
            await Assert.That(row.JumpThrowSource).IsEqualTo(JumpThrowSource.Inputs);
            await Assert.That(row.JumpPressTick).IsEqualTo(94);
            await Assert.That(row.DetonationTick).IsEqualTo(204);
            await Assert.That(row.DetonationSource).IsEqualTo(DetonationSource.Event);
            await Assert.That(row.EndKind).IsEqualTo(GrenadeEndKind.Detonated);
            await Assert.That(row.EndTick).IsEqualTo(204);
            await Assert.That(row.BounceCount).IsEqualTo(2);
            await Assert.That(row.AirTimeTicks).IsEqualTo(90).Because("at rest from tick 190, before the fuse at 204");
            await Assert.That(row.Trajectory.Select(p => p.Tick))
                .IsEquivalentTo([100, 190], CollectionOrdering.Matching);
            await Assert.That(GrenadeConsole.Format(row)).IsEqualTo("setpos 0.00 0.00 0.00; setang -12.50 90.00 0.00");
        }
    }

    [Test]
    public async Task Assemble_AJumpOutsideTheWindow_OrAThrowerWithoutCommands_FallsThroughTheClassifier()
    {
        // A press 30 ticks before the spawn is outside the 27-tick window; no coverage falls back to the ground flag.
        ProjectileFacts early = He(atSpawn: new ThrowerAtSpawn(null, true, 2, 70, 1), index: 1);
        ProjectileFacts silent = He(atSpawn: new ThrowerAtSpawn(null, false, 2, -1, 0), index: 2);

        List<GrenadeRow> rows = GrenadeWalker.Assemble(GrenadeWalker.Plan([early, silent], []),
            GrenadeEventIndex.FromDetonations([]), NoFlights,
            [], [], 64, 0);

        using (Assert.Multiple())
        {
            await Assert.That(rows[0].JumpThrow).IsFalse();
            await Assert.That(rows[0].JumpThrowSource).IsEqualTo(JumpThrowSource.Inputs);
            await Assert.That(rows[0].JumpPressTick).IsNull();
            await Assert.That(rows[1].JumpThrow).IsTrue();
            await Assert.That(rows[1].JumpThrowSource).IsEqualTo(JumpThrowSource.GroundFlag);
        }
    }

    [Test]
    public async Task Assemble_WithoutAReleaseOrAThrower_SaysSoRatherThanGuessing()
    {
        GrenadeEventIndex events = GrenadeEventIndex.FromDetonations([]);
        ProjectileFacts landing = Facts(2, GrenadeProjectileClasses.Decoy, -1, 500, 900, new Vector3(5, 5, 5));

        List<GrenadeRow> rows =
            GrenadeWalker.Assemble(GrenadeWalker.Plan([He(), landing], []), events, NoFlights, [], [], 64, 0);
        GrenadeRow he = rows[0], decoy = rows[1];

        using (Assert.Multiple())
        {
            await Assert.That(he.ReleaseSource).IsEqualTo(ReleaseSource.SpawnOffset);
            await Assert.That(he.ReleasePosition).IsNull();
            await Assert.That(he.ReleaseEyeYaw).IsNull();
            await Assert.That(he.Movement).IsEqualTo(MovementClass.Unknown);
            await Assert.That(he.ThrowStrengthClass).IsEqualTo(ThrowStrengthClass.Unknown);
            await Assert.That(he.JumpThrowSource).IsEqualTo(JumpThrowSource.None);
            await Assert.That(he.ThrowerTeam).IsEqualTo(0);
            await Assert.That(he.RoundNumber).IsEqualTo(0);
            await Assert.That(he.Trajectory).IsEmpty();
            await Assert.That(GrenadeConsole.Format(he)).IsNull();

            await Assert.That(decoy.ThrowerSlot).IsEqualTo(-1);
            await Assert.That(decoy.ThrowerSource).IsEqualTo(ThrowerSource.None);
            await Assert.That(decoy.ThrowerSteamId64).IsNull();
            await Assert.That(decoy.SpawnPosition).IsNull()
                .Because("a landing row carries no release point, and no flight gave one");
            await Assert.That(decoy.EndKind).IsEqualTo(GrenadeEndKind.Removed);
            await Assert.That(decoy.AirTimeTicks).IsEqualTo(400)
                .Because("the landing says nothing about when it came to rest");
        }
    }

    [Test]
    public async Task Assemble_AJoinedThrowerWithoutAProjectileRow_TakesItsSpawnFieldsFromTheFlight()
    {
        // A smoke whose chain never resolved: the table has only its landing, the observer saw its flight.
        ProjectileFacts landing = Facts(Index, GrenadeProjectileClasses.Smoke, -1, 200, 1400, new Vector3(800, 0, 0));
        ReleaseFacts fire = Release(6, 193, "weapon_smokegrenade",
            Read(193, new Vector3(10, 0, 0), -5f, 0f, true, false, 3, 1f, true),
            new Vector3(10, 0, 0));
        GrenadeTrajectories flights =
            new(
                new Dictionary<(int, int), List<TrajectoryPoint>>
                {
                    [(Index, Serial)] =
                        [new TrajectoryPoint(200, 12, 0, 64, 0), new TrajectoryPoint(260, 800, 0, 0, 1)]
                }, 4)
            {
                Facts = new Dictionary<(int, int), FlightFacts>
                {
                    [(Index, Serial)] = new(GrenadeProjectileClasses.Smoke, 200, new Vector3(12, 0, 64),
                        new Vector3(700, 0, 200), null, 1400, true,
                        new Vector3(800, 0, 0), 260, 1)
                }
            };

        GrenadeWalk walk = GrenadeWalker.Assemble(
            new GrenadeWalkInputs(new GrenadeTableRows([landing], [fire], GrenadeEventIndex.FromDetonations([])),
                flights),
            [], [new PlayerInfo(6, "joined", 76561198000000006UL, 12, 3, false)], 64, 0);
        GrenadeRow row = walk.Rows.Single();

        using (Assert.Multiple())
        {
            await Assert.That(row.ThrowerSource).IsEqualTo(ThrowerSource.WeaponFire);
            await Assert.That(row.ThrowerSteamId64).IsEqualTo("76561198000000006");
            await Assert.That(row.ThrowerName).IsEqualTo("joined");
            await Assert.That(row.ReleasePosition).IsEqualTo(new WorldPoint(10, 0, 0));
            await Assert.That(row.SpawnPosition).IsEqualTo(new WorldPoint(12, 0, 64));
            await Assert.That(row.SpawnVelocity).IsEqualTo(new WorldPoint(700, 0, 200));
            await Assert.That(row.AirTimeTicks).IsEqualTo(60).Because("the flight says when it came to rest");
            await Assert.That(row.ThrowerPositionAtSpawn).IsNull()
                .Because("nothing read the joined thrower at the spawn");
            await Assert.That(row.JumpThrowSource).IsEqualTo(JumpThrowSource.None);
            await Assert.That(walk.InputCoverage).IsEqualTo(0).Because("no throw was read at the spawn");
        }
    }

    [Test]
    public async Task Assemble_AFlightWithNoTableRow_IsAGrenadeStillLiveWhenTheRunEnded()
    {
        GrenadeTrajectories flights =
            new(
                new Dictionary<(int, int), List<TrajectoryPoint>>
                {
                    [(Index, Serial)] = [new TrajectoryPoint(100, 0, 0, 64, 0)],
                    [(140, 2)] = [new TrajectoryPoint(950, 5, 5, 64, 0), new TrajectoryPoint(990, 50, 5, 64, 0)]
                }, 4)
            {
                Facts = new Dictionary<(int, int), FlightFacts>
                {
                    [(Index, Serial)] =
                        new(GrenadeProjectileClasses.HEGrenade, 100, new Vector3(0, 0, 64), null, null, 204, true,
                            new Vector3(460, 0, 64), 190, 2),
                    [(140, 2)] = new(GrenadeProjectileClasses.Flashbang, 950, new Vector3(5, 5, 64),
                        new Vector3(600, 0, 0), null, 1000, false,
                        new Vector3(50, 5, 64), 990, 0)
                }
            };

        List<ProjectileFacts> all = GrenadeWalker.WithFlights([He()], flights);
        List<GrenadeRow> rows = GrenadeWalker.Assemble(GrenadeWalker.Plan(all, []),
            GrenadeEventIndex.FromDetonations([]), flights, [], [], 64, 0);

        using (Assert.Multiple())
        {
            await Assert.That(rows.Select(r => r.Id)).IsEquivalentTo(["g120-7", "g140-2"], CollectionOrdering.Matching);
            await Assert.That(all[0]).IsEqualTo(He()).Because("a resolved row keeps its own fields");
            await Assert.That(rows[1].EndKind).IsEqualTo(GrenadeEndKind.DemoEnded);
            await Assert.That(rows[1].EndTick).IsEqualTo(1000);
            await Assert.That(rows[1].SpawnPosition).IsEqualTo(new WorldPoint(5, 5, 64));
            await Assert.That(rows[1].ThrowerSource).IsEqualTo(ThrowerSource.None);
            await Assert.That(rows[1].Trajectory.Count).IsEqualTo(2);
        }
    }
}
