#region

using System.Numerics;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Events;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>One slot's end-of-frame state, what the fold reads every frame.</summary>
/// <param name="Slot">The controller slot.</param>
/// <param name="Team">2 T, 3 CT; anything else is not playing.</param>
/// <param name="Alive">The pawn is alive.</param>
/// <param name="WeaponClass">The class of the weapon in hand; a dead slot keeps its last.</param>
/// <param name="CashSpent">Cash spent this round, or -1 when it did not read.</param>
/// <param name="Flags">Helmet and kit as <see cref="RoundPosition.Flags" />, or -1 when neither read.</param>
public readonly record struct CaptureSlotState(
    int Slot,
    int Team,
    bool Alive,
    string? WeaponClass,
    int CashSpent,
    int Flags);

/// <summary>A slot's economy at a freeze end.</summary>
/// <param name="Armor">Armour, or -1.</param>
/// <param name="Money">Money after the freeze-time buys, or -1.</param>
/// <param name="StartAccount">Money at the round's start, or -1.</param>
public readonly record struct CaptureEconomy(int Armor, int Money, int StartAccount);

/// <summary>The gamerules timeout state, the same for every slot.</summary>
public readonly record struct CaptureMatchState(
    bool CtActive,
    bool TActive,
    bool Technical,
    int CtLeft,
    int TLeft);

/// <summary>What the fold reads off one frame, lazily where a read is not needed every frame.</summary>
public interface ICaptureFrameReads
{
    /// <summary>The slots seated on a playing side, with their state.</summary>
    IReadOnlyList<CaptureSlotState> Slots { get; }

    /// <summary>A slot's position and place, or null when it did not read.</summary>
    (Vector3 Position, string? Place)? Position(int slot);

    /// <summary>A slot's economy.</summary>
    CaptureEconomy Economy(int slot);

    /// <summary>The timeout state, or null when no slot carried it.</summary>
    CaptureMatchState? Match { get; }
}

/// <summary>
///     The fold behind <see cref="RoundCapturesCollector" />, kept free of the frame view so a test can drive it:
///     purchases (an <c>item_pickup</c> on the frame the buyer's cash spent rose), AWP ownership and in-hand spans,
///     each freeze end's loadout, alive-with-AWP at each round end, the positions 0 to 3 s after each slot's first
///     AWP shot of a round, the tactical timeouts and each slot's helmet and kit over time. Every tick is the frame
///     clock. Rounds are not known here: the pass assigns ticks to Round Facts rounds.
/// </summary>
public sealed class RoundCapturesFold
{
    /// <summary>The trail's offsets from the shot, in seconds.</summary>
    public const int TrailSeconds = 3;

    private const string AwpClass = "CWeaponAWP";

    // Every primary but the AWP, by item name.
    private static readonly HashSet<string> _primaries = new(StringComparer.Ordinal)
    {
        "ak47", "m4a1", "m4a1_silencer", "famas", "galilar", "aug", "sg556", "ssg08", "g3sg1", "scar20", "mac10",
        "mp9", "mp7", "mp5sd", "ump45", "p90", "bizon", "nova", "xm1014", "sawedoff", "mag7", "m249", "negev"
    };

    private readonly Dictionary<int, SlotTrack> _slots = [];
    private readonly SlotTrack?[] _bySlot = new SlotTrack?[64];
    private readonly List<(int Slot, string Item)> _framePickups = [];
    private readonly List<int> _frameAwpShooters = [];
    private readonly List<int> _frameAwpGainers = [];
    private readonly List<int> _frameAwpLosers = [];
    private readonly List<OpenTrail> _openTrails = [];
    private readonly int _ticksPerSecond;
    private readonly RoundCaptureFrames _out = new();
    private bool _frameFreezeEnd;
    private bool _frameRoundEnd;
    private CaptureMatchState? _match;
    private CapturedTimeout? _openCt, _openT, _openTechnical;

    /// <param name="ticksPerSecond">The frame clock's rate; GOTV is 64.</param>
    public RoundCapturesFold(int ticksPerSecond = 64)
    {
        _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : 64;
    }

    /// <summary>Folds one game event of the frame being read. Any event the fold does not read is ignored.</summary>
    /// <param name="payload">The decoded event's payload.</param>
    public void OnEvent(object payload)
    {
        switch (payload)
        {
            case ItemPickupEvent pickup:
                _framePickups.Add((pickup.UserId, NormalizeItem(pickup.Item)));
                break;
            case ItemRemoveEvent remove when IsAwpItem(remove.Item):
                _frameAwpLosers.Add(remove.UserId);
                break;
            case WeaponFireEvent fire when IsAwpItem(fire.Weapon):
                _frameAwpShooters.Add(fire.UserId);
                break;
            case RoundFreezeEndEvent:
                _frameFreezeEnd = true;
                break;
            case RoundEndEvent:
                _frameRoundEnd = true;
                break;
        }
    }

    /// <summary>Closes the frame: the end-of-frame state of every seated slot, after its events.</summary>
    /// <param name="tick">The frame clock.</param>
    /// <param name="reads">The frame's reads.</param>
    public void EndFrame(int tick, ICaptureFrameReads reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        IReadOnlyList<CaptureSlotState> slots = reads.Slots;

        // Purchases first: the cash rise is this frame's end against the last frame's.
        int lastPurchaseSlot = -1;
        foreach ((int slot, string item) in _framePickups)
        {
            SlotTrack track = Track(slot);
            int now = Find(slots, slot) is { } s ? s.CashSpent : -1;
            int rise = now >= 0 && track.CashSpent >= 0 ? now - track.CashSpent : 0;
            bool bought = rise > 0;
            // A second pickup of one slot on one frame shares the first's rise: it is a buy, its cost is on the first.
            int cost = bought && lastPurchaseSlot != slot ? rise : 0;
            bought |= lastPurchaseSlot == slot;
            _out.Pickups.Add(new CapturedPickup(tick, slot, item, bought, cost));
            if (bought)
            {
                lastPurchaseSlot = slot;
            }

            if (item == "awp")
            {
                _frameAwpGainers.Add(slot);
            }
            else if (_primaries.Contains(item))
            {
                // One primary at a time: a slot that takes another one has let its AWP go. GOTV carries no
                // item_remove, so this and a death are what end a span (a drop to a teammate is not seen).
                _frameAwpLosers.Add(slot);
            }
        }

        foreach (int slot in _frameAwpLosers)
        {
            Track(slot).LoseAwp(tick, AwpSpanEnd.Removed);
        }

        foreach (int slot in _frameAwpGainers)
        {
            Track(slot).GainAwp(tick);
        }

        foreach (int slot in _frameAwpShooters)
        {
            SlotTrack track = Track(slot);
            track.GainAwp(tick);
            _out.AwpShots.Add(new CapturedShot(tick, slot));
            if (!track.ShotThisRound)
            {
                track.ShotThisRound = true;
                if (reads.Position(slot) is { } at)
                {
                    OpenTrail trail = new(slot, tick);
                    trail.Samples.Add(new CapturedTrailPoint(tick, at.Position, at.Place, true));
                    _openTrails.Add(trail);
                }
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            CaptureSlotState state = slots[i];
            SlotTrack track = Track(state.Slot);
            if (track.Team is 2 or 3 && state.Team is 2 or 3 && track.Team != state.Team)
            {
                // A side swap resets every inventory.
                track.LoseAwp(tick, AwpSpanEnd.SideSwap);
            }

            if (track.Alive && !state.Alive)
            {
                track.LoseAwp(tick, AwpSpanEnd.Died);
            }

            if (state.Alive && string.Equals(state.WeaponClass, AwpClass, StringComparison.Ordinal))
            {
                track.GainAwp(tick);
                track.InHand(tick);
            }
            else
            {
                track.CloseInHand();
            }

            if (state.Flags >= 0 && state.Flags != track.Flags)
            {
                _out.Flags.Add(state.Slot, tick, state.Flags);
                track.Flags = state.Flags;
            }

            track.Team = state.Team;
            track.Alive = state.Alive;
            if (state.CashSpent >= 0)
            {
                track.CashSpent = state.CashSpent;
            }
        }

        if (_openTrails.Count > 0)
        {
            AdvanceTrails(tick, reads);
        }

        if (_frameFreezeEnd)
        {
            List<CapturedLoadout> loadouts = [];
            foreach (CaptureSlotState state in reads.Slots)
            {
                SlotTrack track = Track(state.Slot);
                track.ShotThisRound = false;
                CaptureEconomy economy = reads.Economy(state.Slot);
                loadouts.Add(new CapturedLoadout(state.Slot, state.Team, state.Alive, track.HasAwp,
                    state.Flags, economy.Armor, economy.Money, economy.StartAccount, state.CashSpent));
            }

            loadouts.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            _out.FreezeEnds.Add(new CapturedFreezeEnd(tick, loadouts));
            // A trail never runs into the next round: one still open keeps the points it has.
            foreach (OpenTrail trail in _openTrails)
            {
                _out.Trails.Add(new CapturedTrail(trail.Slot, trail.ShotTick, [.. trail.Samples]));
            }

            _openTrails.Clear();
        }

        if (_frameRoundEnd)
        {
            _out.RoundEnds.Add(new CapturedRoundEnd(tick,
            [
                .. reads.Slots.Where(s => s.Alive && Track(s.Slot).HasAwp).Select(s => s.Slot).Order()
            ]));
        }

        if (reads.Match is { } match)
        {
            FoldTimeouts(tick, match);
        }

        _framePickups.Clear();
        _frameAwpShooters.Clear();
        _frameAwpGainers.Clear();
        _frameAwpLosers.Clear();
        _frameFreezeEnd = false;
        _frameRoundEnd = false;
    }

    /// <summary>After the last frame: closes what is open and hands over the result.</summary>
    /// <param name="lastTick">The last frame's clock.</param>
    public RoundCaptureFrames Finish(int lastTick)
    {
        foreach (SlotTrack track in _slots.Values)
        {
            track.CloseInHand();
            track.LoseAwp(lastTick, AwpSpanEnd.DemoEnd);
        }

        foreach (OpenTrail trail in _openTrails)
        {
            _out.Trails.Add(new CapturedTrail(trail.Slot, trail.ShotTick, [.. trail.Samples]));
        }

        _openTrails.Clear();
        foreach (CapturedTimeout? open in new[] { _openCt, _openT, _openTechnical })
        {
            if (open is not null)
            {
                _out.Timeouts.Add(open with
                {
                    CtLeftAfter = _match?.CtLeft ?? open.CtLeftBefore, TLeftAfter = _match?.TLeft ?? open.TLeftBefore
                });
            }
        }

        _openCt = _openT = _openTechnical = null;
        _out.AwpSpans.Sort((a, b) => a.FromTick != b.FromTick ? a.FromTick.CompareTo(b.FromTick) : a.Slot.CompareTo(b.Slot));
        _out.InHand.Sort((a, b) => a.FromTick != b.FromTick ? a.FromTick.CompareTo(b.FromTick) : a.Slot.CompareTo(b.Slot));
        _out.Trails.Sort((a, b) => a.ShotTick != b.ShotTick ? a.ShotTick.CompareTo(b.ShotTick) : a.Slot.CompareTo(b.Slot));
        _out.Timeouts.Sort((a, b) => a.StartTick.CompareTo(b.StartTick));
        return _out;
    }

    private void AdvanceTrails(int tick, ICaptureFrameReads reads)
    {
        for (int i = _openTrails.Count - 1; i >= 0; i--)
        {
            OpenTrail trail = _openTrails[i];
            int next = trail.Samples.Count; // the offset in seconds the next point is due at
            if (next > TrailSeconds)
            {
                continue;
            }

            if (tick < trail.ShotTick + next * _ticksPerSecond)
            {
                continue;
            }

            bool alive = Find(reads.Slots, trail.Slot) is { Alive: true };
            if (reads.Position(trail.Slot) is { } at)
            {
                trail.Samples.Add(new CapturedTrailPoint(tick, at.Position, at.Place, alive));
            }
            else
            {
                trail.Samples.Add(new CapturedTrailPoint(tick, null, null, alive));
            }

            if (trail.Samples.Count > TrailSeconds)
            {
                _out.Trails.Add(new CapturedTrail(trail.Slot, trail.ShotTick, [.. trail.Samples]));
                _openTrails.RemoveAt(i);
            }
        }
    }

    private void FoldTimeouts(int tick, CaptureMatchState now)
    {
        CaptureMatchState before = _match ?? now with { CtActive = false, TActive = false, Technical = false };
        _openCt = Edge(before.CtActive, now.CtActive, 3, false, _openCt);
        _openT = Edge(before.TActive, now.TActive, 2, false, _openT);
        _openTechnical = Edge(before.Technical, now.Technical, 0, true, _openTechnical);
        _match = now;
        return;

        CapturedTimeout? Edge(bool was, bool isNow, int side, bool technical, CapturedTimeout? open)
        {
            if (!was && isNow)
            {
                return new CapturedTimeout(tick, null, side, technical, before.CtLeft, before.TLeft, now.CtLeft,
                    now.TLeft);
            }

            if (was && !isNow && open is not null)
            {
                _out.Timeouts.Add(open with { EndTick = tick, CtLeftAfter = now.CtLeft, TLeftAfter = now.TLeft });
                return null;
            }

            return open;
        }
    }

    private static CaptureSlotState? Find(IReadOnlyList<CaptureSlotState> slots, int slot)
    {
        for (int i = slots.Count - 1; i >= 0; i--)
        {
            if (slots[i].Slot == slot)
            {
                return slots[i];
            }
        }

        return null;
    }

    private SlotTrack Track(int slot)
    {
        if ((uint)slot < (uint)_bySlot.Length)
        {
            return _bySlot[slot] ??= NewTrack(slot);
        }

        if (!_slots.TryGetValue(slot, out SlotTrack? track))
        {
            track = NewTrack(slot);
        }

        return track;
    }

    private SlotTrack NewTrack(int slot)
    {
        SlotTrack track = new(slot, _out);
        _slots[slot] = track;
        return track;
    }

    /// <summary>An item name without its <c>weapon_</c> or <c>item_</c> prefix, lower case.</summary>
    /// <param name="item">The event's item.</param>
    public static string NormalizeItem(string? item)
    {
        string name = (item ?? "").Trim().ToLowerInvariant();
        if (name.StartsWith("weapon_", StringComparison.Ordinal))
        {
            name = name["weapon_".Length..];
        }
        else if (name.StartsWith("item_", StringComparison.Ordinal))
        {
            name = name["item_".Length..];
        }

        return name;
    }

    private static bool IsAwpItem(string? item) => NormalizeItem(item) == "awp";

    private sealed class OpenTrail(int slot, int shotTick)
    {
        public int Slot { get; } = slot;

        public int ShotTick { get; } = shotTick;

        public List<CapturedTrailPoint> Samples { get; } = [];
    }

    private sealed class SlotTrack(int slot, RoundCaptureFrames output)
    {
        private int _awpFrom = -1;
        private int _inHandFrom = -1, _inHandTo = -1, _inHandFrames;

        public int Team { get; set; } = -1;

        public bool Alive { get; set; }

        public int CashSpent { get; set; } = -1;

        public int Flags { get; set; } = -1;

        public bool ShotThisRound { get; set; }

        public bool HasAwp => _awpFrom >= 0;

        public void GainAwp(int tick)
        {
            if (_awpFrom < 0)
            {
                _awpFrom = tick;
            }
        }

        public void LoseAwp(int tick, AwpSpanEnd reason)
        {
            if (_awpFrom < 0)
            {
                return;
            }

            output.AwpSpans.Add(new CapturedAwpSpan(slot, _awpFrom, tick, reason));
            _awpFrom = -1;
            CloseInHand();
        }

        public void InHand(int tick)
        {
            if (_inHandFrom < 0)
            {
                _inHandFrom = tick;
            }

            _inHandTo = tick;
            _inHandFrames++;
        }

        public void CloseInHand()
        {
            if (_inHandFrom >= 0)
            {
                output.InHand.Add(new CapturedInHand(slot, _inHandFrom, _inHandTo, _inHandFrames));
            }

            _inHandFrom = _inHandTo = -1;
            _inHandFrames = 0;
        }
    }
}

/// <summary>
///     The Scouting Report's capture collector on the round index pass: <see cref="RoundCapturesFold" /> over the
///     visit's rules run, reading <c>item_pickup</c>, <c>item_remove</c>, <c>weapon_fire</c> and the round events off
///     the frames, and every seated slot's liveness, weapon in hand, cash spent, helmet and kit each frame.
/// </summary>
public sealed class RoundCapturesCollector : IFrameCollector
{
    /// <summary>The collector's id.</summary>
    public const string CollectorId = "net.demoviewer.pack.stratbook.roundindex.captures";

    /// <summary>Bumped when what the collector puts changes; part of the round index fingerprint.</summary>
    public const int Version = 1;

    // The seated slots are found again this often; a slot that joins mid-match is read from the next scan.
    private const int RescanFrames = 32;
    private const int MaxSlots = 64;

    /// <summary>The events read off the frames.</summary>
    public static IReadOnlyList<string> Events { get; } =
        ["item_pickup", "item_remove", "weapon_fire", "round_freeze_end", "round_end"];

    /// <summary>The providers read.</summary>
    public static IReadOnlyList<string> ProviderNames { get; } =
    [
        Providers.IsAlive, Providers.WeaponClass, Providers.PosX, Providers.PosY, Providers.PosZ, Providers.Place,
        Providers.Armor, Providers.Money,
        HostProviders.CashSpentRound, HostProviders.StartAccount, HostProviders.HasHelmet, HostProviders.HasDefuser,
        HostProviders.CtTimeoutActive, HostProviders.TTimeoutActive, HostProviders.TechnicalTimeout,
        HostProviders.CtTimeoutsLeft, HostProviders.TTimeoutsLeft
    ];

    private readonly RoundCapturesFold _fold = new();
    private readonly List<int> _seated = [];
    private Reads? _reads;
    private int _lastTick;

    /// <summary>The registration: the providers and the events, no projectile classes.</summary>
    public static CollectorContribution Contribution { get; } =
        new(CollectorId, RoundIndexEvaluator.EvaluatorId, Version, ProviderNames, [],
            () => new RoundCapturesCollector()) { GameEvents = Events };

    /// <inheritdoc />
    public void OnFrame(IFrameReadView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _reads ??= new Reads(view);
        _reads.Bind(view);
        if (view.FrameIndex % RescanFrames == 0 || _seated.Count == 0)
        {
            _seated.Clear();
            for (int slot = 0; slot < MaxSlots; slot++)
            {
                if (view.Team(slot) is 2 or 3)
                {
                    _seated.Add(slot);
                }
            }
        }

        foreach (NetMessage message in view.Frame.DecodedMessages)
        {
            if (message is GameEventMessage { DecodedEvent: { Payload: { } payload } })
            {
                _fold.OnEvent(payload);
            }
        }

        _reads.Load(_seated);
        _fold.EndFrame(view.Tick, _reads);
        _lastTick = view.Tick;
    }

    /// <inheritdoc />
    public void Finish(ICollectorSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        sink.Put(_fold.Finish(_lastTick));
    }

    /// <summary>The catalog names the collector reads.</summary>
    public static class Providers
    {
        public const string IsAlive = "entity.pawn.is_alive";
        public const string WeaponClass = "entity.pawn.active_weapon_class";
        public const string PosX = "entity.pawn.pos_x";
        public const string PosY = "entity.pawn.pos_y";
        public const string PosZ = "entity.pawn.pos_z";
        public const string Place = "entity.pawn.place";
        public const string Armor = "entity.pawn.armor";
        public const string Money = "entity.controller.money";
    }

    private sealed class Reads(IFrameReadView first) : ICaptureFrameReads
    {
        private readonly ObservedRead _alive = first.Provider(Providers.IsAlive);
        private readonly ObservedRead _weapon = first.Provider(Providers.WeaponClass);
        private readonly ObservedRead _x = first.Provider(Providers.PosX);
        private readonly ObservedRead _y = first.Provider(Providers.PosY);
        private readonly ObservedRead _z = first.Provider(Providers.PosZ);
        private readonly ObservedRead _place = first.Provider(Providers.Place);
        private readonly ObservedRead _armor = first.Provider(Providers.Armor);
        private readonly ObservedRead _money = first.Provider(Providers.Money);
        private readonly ObservedRead _cash = first.Provider(HostProviders.CashSpentRound);
        private readonly ObservedRead _start = first.Provider(HostProviders.StartAccount);
        private readonly ObservedRead _helmet = first.Provider(HostProviders.HasHelmet);
        private readonly ObservedRead _defuser = first.Provider(HostProviders.HasDefuser);
        private readonly ObservedRead _ctActive = first.Provider(HostProviders.CtTimeoutActive);
        private readonly ObservedRead _tActive = first.Provider(HostProviders.TTimeoutActive);
        private readonly ObservedRead _technical = first.Provider(HostProviders.TechnicalTimeout);
        private readonly ObservedRead _ctLeft = first.Provider(HostProviders.CtTimeoutsLeft);
        private readonly ObservedRead _tLeft = first.Provider(HostProviders.TTimeoutsLeft);
        private readonly List<CaptureSlotState> _slots = [];
        private IFrameReadView _view = first;

        public IReadOnlyList<CaptureSlotState> Slots => _slots;

        public CaptureMatchState? Match { get; private set; }

        public void Bind(IFrameReadView view) => _view = view;

        public void Load(List<int> seated)
        {
            _slots.Clear();
            Match = null;
            foreach (int slot in seated)
            {
                int team = _view.Team(slot);
                if (team is not (2 or 3))
                {
                    continue;
                }

                bool alive = _view.TryReadInt(_alive, slot, out int a) && a != 0;
                _view.TryReadString(_weapon, slot, out string? weapon);
                int cash = _view.TryReadInt(_cash, slot, out int c) ? c : -1;
                bool hasHelmet = _view.TryReadInt(_helmet, slot, out int h);
                bool hasKit = _view.TryReadInt(_defuser, slot, out int d);
                int flags = hasHelmet || hasKit
                    ? (hasHelmet && h != 0 ? RoundPosition.HelmetFlag : 0) | (hasKit && d != 0 ? RoundPosition.DefuserFlag : 0)
                    : -1;
                _slots.Add(new CaptureSlotState(slot, team, alive, weapon, cash, flags));

                if (Match is null && _view.TryReadInt(_ctLeft, slot, out int ctLeft)
                                  && _view.TryReadInt(_tLeft, slot, out int tLeft))
                {
                    Match = new CaptureMatchState(
                        _view.TryReadInt(_ctActive, slot, out int ca) && ca != 0,
                        _view.TryReadInt(_tActive, slot, out int ta) && ta != 0,
                        _view.TryReadInt(_technical, slot, out int te) && te != 0,
                        ctLeft, tLeft);
                }
            }
        }

        public (Vector3 Position, string? Place)? Position(int slot)
        {
            if (!_view.TryReadFloat(_x, slot, out float x) || !_view.TryReadFloat(_y, slot, out float y)
                                                            || !_view.TryReadFloat(_z, slot, out float z))
            {
                return null;
            }

            _view.TryReadString(_place, slot, out string? place);
            return (new Vector3(x, y, z), string.IsNullOrEmpty(place) ? null : place);
        }

        public CaptureEconomy Economy(int slot) => new(
            _view.TryReadInt(_armor, slot, out int armor) ? armor : -1,
            _view.TryReadInt(_money, slot, out int money) ? money : -1,
            _view.TryReadInt(_start, slot, out int start) ? start : -1);
    }
}
