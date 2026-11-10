#region

using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

// ── What the collector puts: ticks only, no rounds ───────────────────────────────────────────────

/// <summary>Why an AWP span closed.</summary>
public enum AwpSpanEnd
{
    /// <summary>The holder died.</summary>
    Died,

    /// <summary>An <c>item_remove</c> took it: a drop, a swap, a pass to a teammate.</summary>
    Removed,

    /// <summary>The sides swapped and every inventory reset.</summary>
    SideSwap,

    /// <summary>The demo ended with it held.</summary>
    DemoEnd
}

/// <summary>An <c>item_pickup</c>, bought or not; a purchase on the same tick and slot says which.</summary>
/// <param name="Tick">Frame clock.</param>
/// <param name="Slot">The picker.</param>
/// <param name="Item">The item, without its <c>weapon_</c> prefix (<c>awp</c>, <c>vesthelm</c>, <c>defuser</c>).</param>
public sealed record CapturedPickup(int Tick, int Slot, string Item);

/// <summary>A span a slot owned an AWP: from the first evidence (a pickup, a shot, one in hand) to its loss.</summary>
public sealed record CapturedAwpSpan(int Slot, int FromTick, int ToTick, AwpSpanEnd End);

/// <summary>A run of consecutive frames a slot had the AWP in hand, alive.</summary>
public sealed record CapturedInHand(int Slot, int FromTick, int ToTick, int Frames);

/// <summary>An AWP <c>weapon_fire</c>.</summary>
public sealed record CapturedShot(int Tick, int Slot);

/// <summary>One slot at a freeze end.</summary>
/// <param name="Slot">The slot.</param>
/// <param name="Team">2 T, 3 CT.</param>
/// <param name="Alive">Alive at the freeze end.</param>
/// <param name="HasAwp">Owned an AWP by the fold's spans.</param>
/// <param name="Flags">Helmet and kit as <see cref="RoundPosition.Flags" />, or -1.</param>
/// <param name="Armor">Armour, or -1.</param>
/// <param name="Money">Money after the freeze-time buys, or -1.</param>
/// <param name="StartAccount">Money at the round's start, or -1.</param>
/// <param name="CashSpent">Spent so far this round, or -1.</param>
public sealed record CapturedLoadout(
    int Slot,
    int Team,
    bool Alive,
    bool HasAwp,
    int Flags,
    int Armor,
    int Money,
    int StartAccount,
    int CashSpent);

/// <summary>Every seated slot at one <c>round_freeze_end</c>.</summary>
public sealed record CapturedFreezeEnd(int Tick, IReadOnlyList<CapturedLoadout> Slots);

/// <summary>The slots alive with an AWP at one <c>round_end</c>.</summary>
public sealed record CapturedRoundEnd(int Tick, IReadOnlyList<int> AliveWithAwp);

/// <summary>A point of a first-shot trail; the position is null when it did not read.</summary>
public sealed record CapturedTrailPoint(int Tick, Vector3? Position, string? Place, bool Alive);

/// <summary>A slot's first AWP shot of a round and where it was then and 1, 2 and 3 seconds on.</summary>
public sealed record CapturedTrail(int Slot, int ShotTick, IReadOnlyList<CapturedTrailPoint> Points);

/// <summary>
///     One tactical or technical timeout, from the gamerules flag's rise to its fall.
/// </summary>
/// <param name="StartTick">Frame clock of the rise.</param>
/// <param name="EndTick">Frame clock of the fall, or null when the demo ended inside it.</param>
/// <param name="Side">3 CT, 2 T, 0 for a technical timeout.</param>
/// <param name="Technical">A technical timeout.</param>
/// <param name="CtLeftBefore">The CT side's tactical timeouts left on the frame before.</param>
/// <param name="TLeftBefore">The T side's, before.</param>
/// <param name="CtLeftAfter">The CT side's at the fall.</param>
/// <param name="TLeftAfter">The T side's at the fall.</param>
public sealed record CapturedTimeout(
    int StartTick,
    int? EndTick,
    int Side,
    bool Technical,
    int CtLeftBefore,
    int TLeftBefore,
    int CtLeftAfter,
    int TLeftAfter);

/// <summary>Each slot's helmet and kit over time: the flags from each tick they changed.</summary>
public sealed class LoadoutFlagsTimeline
{
    private readonly Dictionary<int, List<(int Tick, int Flags)>> _bySlot = [];

    /// <summary>Records the slot's flags from <paramref name="tick" /> on; ticks arrive in order.</summary>
    public void Add(int slot, int tick, int flags)
    {
        if (!_bySlot.TryGetValue(slot, out List<(int Tick, int Flags)>? changes))
        {
            changes = [];
            _bySlot[slot] = changes;
        }

        changes.Add((tick, flags));
    }

    /// <summary>The slot's flags at <paramref name="tick" />, or -1 before its first read.</summary>
    public int FlagsAt(int slot, int tick)
    {
        if (!_bySlot.TryGetValue(slot, out List<(int Tick, int Flags)>? changes))
        {
            return -1;
        }

        int lo = 0, hi = changes.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (changes[mid].Tick <= tick)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found < 0 ? -1 : changes[found].Flags;
    }
}

/// <summary>What <see cref="RoundCapturesCollector" /> puts: every capture by frame-clock tick.</summary>
public sealed class RoundCaptureFrames
{
    public List<CapturedPickup> Pickups { get; } = [];

    public List<CapturedAwpSpan> AwpSpans { get; } = [];

    public List<CapturedInHand> InHand { get; } = [];

    public List<CapturedShot> AwpShots { get; } = [];

    public List<CapturedFreezeEnd> FreezeEnds { get; } = [];

    public List<CapturedRoundEnd> RoundEnds { get; } = [];

    public List<CapturedTrail> Trails { get; } = [];

    public List<CapturedTimeout> Timeouts { get; } = [];

    public LoadoutFlagsTimeline Flags { get; } = new();
}

// ── What the store keeps: per round ──────────────────────────────────────────────────────────────

/// <summary>A player's spot in integer world units and its place, or null place when unplaced.</summary>
public sealed record CaptureSpot(int X, int Y, int Z, string? Place)
{
    public static CaptureSpot? Of(EventSpot? spot) =>
        spot is { } s
            ? new CaptureSpot(RoundIndexBuild.Quantize(s.X), RoundIndexBuild.Quantize(s.Y),
                RoundIndexBuild.Quantize(s.Z), s.Place)
            : null;

    public static CaptureSpot? Of(Vector3? position, string? place) =>
        position is { } p
            ? new CaptureSpot(RoundIndexBuild.Quantize(p.X), RoundIndexBuild.Quantize(p.Y),
                RoundIndexBuild.Quantize(p.Z), place)
            : null;
}

/// <summary>One kill with both players' spots at the frame before it.</summary>
/// <param name="Tick">Frame clock.</param>
/// <param name="AttackerSlot">The killer.</param>
/// <param name="VictimSlot">The victim.</param>
/// <param name="AttackerSide">The killer's side (2 T, 3 CT).</param>
/// <param name="Weapon">The event's weapon (<c>awp</c>, <c>ak47</c> ...).</param>
/// <param name="WeaponClass">The class in the killer's hand.</param>
/// <param name="Headshot">A headshot.</param>
/// <param name="Trade">The engine's trade enrichment.</param>
/// <param name="TradedSlot">Whose death it traded, or -1.</param>
/// <param name="Opening">The round's first death.</param>
/// <param name="Attacker">The killer's spot.</param>
/// <param name="Victim">The victim's spot.</param>
public sealed record CapturedKill(
    int Tick,
    int AttackerSlot,
    int VictimSlot,
    int AttackerSide,
    string? Weapon,
    string? WeaponClass,
    bool Headshot,
    bool Trade,
    int TradedSlot,
    bool Opening,
    CaptureSpot? Attacker,
    CaptureSpot? Victim);

/// <summary>The round's plant: who, where and the site.</summary>
/// <param name="Tick">Frame clock.</param>
/// <param name="Slot">The planter.</param>
/// <param name="SiteEntity">The event's site entity.</param>
/// <param name="Site">The site Round Facts named.</param>
/// <param name="At">The planter's spot.</param>
public sealed record CapturedPlant(int Tick, int Slot, int SiteEntity, BombSite Site, CaptureSpot? At);

/// <summary>One purchase.</summary>
/// <param name="Tick">Frame clock.</param>
/// <param name="Slot">The buyer.</param>
/// <param name="Item">The item (<c>awp</c>, <c>vesthelm</c>, <c>smokegrenade</c> ...).</param>
/// <param name="Cost">What it cost; 0 for the second and later items of a buy the engine could not split.</param>
/// <param name="FreezeTime">Bought before the round's freeze end.</param>
public sealed record CapturedPurchase(int Tick, int Slot, string Item, int Cost, bool FreezeTime);

/// <summary>A slot's AWP evidence in one round; a slot with none is not listed.</summary>
/// <param name="Slot">The slot.</param>
/// <param name="Side">2 T, 3 CT, by Round Facts' seating; 0 when unseated.</param>
/// <param name="AtFreezeEnd">Owned one at the freeze end: carried over or bought in freeze time.</param>
/// <param name="Bought">Bought one this round.</param>
/// <param name="PickedUp">Picked one up this round without buying it.</param>
/// <param name="InHandFrames">Frames alive with it in hand in the live round.</param>
/// <param name="FirstInHandTick">The first of those, or null.</param>
/// <param name="Shots">AWP shots in the live round.</param>
/// <param name="Kills">AWP kills in the live round.</param>
/// <param name="AliveWithAwpAtEnd">Alive and still owning one at the round end: kept for the next.</param>
public sealed record AwpHolding(
    int Slot,
    int Side,
    bool AtFreezeEnd,
    bool Bought,
    bool PickedUp,
    int InHandFrames,
    int? FirstInHandTick,
    int Shots,
    int Kills,
    bool AliveWithAwpAtEnd);

/// <summary>One slot's kit at the freeze end.</summary>
public sealed record RoundLoadout(
    int Slot,
    int Side,
    bool Alive,
    int Armor,
    bool? Helmet,
    bool? Defuser,
    int Money,
    int StartAccount,
    int CashSpent);

/// <summary>A first AWP shot and the trail after it: the points at 0, 1, 2 and 3 s.</summary>
public sealed record FirstShotTrail(int Slot, int Tick, IReadOnlyList<FirstShotPoint> Points);

/// <summary>One point of a <see cref="FirstShotTrail" />.</summary>
public sealed record FirstShotPoint(int Tick, CaptureSpot? At, bool Alive);

/// <summary>One live round's captures.</summary>
public sealed class CapturedRound
{
    /// <summary>== <c>RoundFacts.Number</c>.</summary>
    public int Number { get; init; }

    public int FreezeEndTick { get; init; }

    public int EndTick { get; init; }

    /// <summary>Round Facts called either side's buy a pistol round.</summary>
    public bool Pistol { get; init; }

    public List<CapturedKill> Kills { get; init; } = [];

    public CapturedPlant? Plant { get; set; }

    public List<CapturedPurchase> Purchases { get; init; } = [];

    public List<AwpHolding> Awp { get; init; } = [];

    public List<RoundLoadout> Loadouts { get; init; } = [];

    public List<FirstShotTrail> FirstShots { get; init; } = [];
}

/// <summary>A timeout, placed before the round it fell ahead of.</summary>
/// <param name="StartTick">Frame clock.</param>
/// <param name="EndTick">Frame clock, or null.</param>
/// <param name="Side">3 CT, 2 T, 0 technical.</param>
/// <param name="Technical">Technical.</param>
/// <param name="CtLeftBefore">CT timeouts left before.</param>
/// <param name="TLeftBefore">T timeouts left before.</param>
/// <param name="CtLeftAfter">CT timeouts left after.</param>
/// <param name="TLeftAfter">T timeouts left after.</param>
/// <param name="BeforeRound">The first live round whose freeze end is after it, or null.</param>
public sealed record RoundTimeout(
    int StartTick,
    int? EndTick,
    int Side,
    bool Technical,
    int CtLeftBefore,
    int TLeftBefore,
    int CtLeftAfter,
    int TLeftAfter,
    int? BeforeRound);

/// <summary>
///     The round index's captures part: per live round the kills with both spots, the plant spot, purchases, AWP
///     evidence per slot, the freeze-end loadouts and the first-shot trails; per match the timeouts. Stored under the
///     same stamp and fingerprint as the positions, so a reader with the current fingerprint never sees a stale one.
/// </summary>
public sealed class RoundCapturesDocument
{
    /// <summary>The file shape version.</summary>
    public const int CurrentSchemaVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Identical to the positions and the index this part belongs to.</summary>
    public string Fingerprint { get; set; } = "";

    public RoundPositionsDemo Demo { get; set; } = new();

    public int TickRate { get; init; }

    /// <summary>
    ///     False when the collector did not run (a held parse outside a visit): only the event tables are in, and
    ///     purchases, AWP evidence, loadouts, trails and timeouts are empty for want of data, not for want of events.
    /// </summary>
    public bool Collected { get; init; }

    public List<CapturedRound> Rounds { get; init; } = [];

    public List<RoundTimeout> Timeouts { get; init; } = [];

    /// <summary>The round with this number, or null.</summary>
    public CapturedRound? Round(int number) => Rounds.FirstOrDefault(r => r.Number == number);

    public byte[] SerializeUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The document UTF-8 JSON describes, or null when it does not parse.</summary>
    public static RoundCapturesDocument? TryDeserialize(byte[] utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<RoundCapturesDocument>(utf8, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
