#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>How sure a round's AWP holder is.</summary>
public enum AwpConfidence
{
    /// <summary>
    ///     Owned (carried over, bought or picked up) and never seen in hand, shooting or killing: often an AWP bought
    ///     for a teammate, since GOTV does not say when one is dropped.
    /// </summary>
    Low,

    /// <summary>Seen with it in hand, shooting or killing, with no buy, pickup or carry seen.</summary>
    Medium,

    /// <summary>Owned and seen with it in the round.</summary>
    High
}

/// <summary>A round's AWP holder on one side.</summary>
/// <param name="Slot">The slot.</param>
/// <param name="Side">2 T, 3 CT.</param>
/// <param name="Confidence">How sure.</param>
/// <param name="Evidence">What it rests on.</param>
public sealed record AwpHolder(int Slot, int Side, AwpConfidence Confidence, AwpHolding Evidence)
{
    /// <summary>Kept alive with the AWP at the round's end, by a holder seen using it.</summary>
    public bool Saved => Evidence.AliveWithAwpAtEnd && Confidence >= AwpConfidence.Medium;
}

/// <summary>One player's pistol-round loadout, from the round's purchases and the freeze-end kit.</summary>
/// <param name="Slot">The slot.</param>
/// <param name="Side">2 T, 3 CT.</param>
/// <param name="Armor">Kevlar at the freeze end; null when unknown.</param>
/// <param name="Helmet">A helmet; null when unknown.</param>
/// <param name="Defuser">A kit; null when unknown.</param>
/// <param name="Utility">The grenades bought, in buy order.</param>
/// <param name="Pistol">The pistol bought over the default one, or null.</param>
/// <param name="Taser">A zeus was bought.</param>
/// <param name="Spent">What the purchases cost.</param>
public sealed record PistolLoadout(
    int Slot,
    int Side,
    bool? Armor,
    bool? Helmet,
    bool? Defuser,
    IReadOnlyList<string> Utility,
    string? Pistol,
    bool Taser,
    int Spent);

/// <summary>One round of a demo's captures, with the reads a layer wants.</summary>
public sealed class RoundCaptureView
{
    private static readonly HashSet<string> _utility = new(StringComparer.Ordinal)
    {
        "smokegrenade", "flashbang", "hegrenade", "molotov", "incgrenade", "decoy"
    };

    private static readonly HashSet<string> _pistols = new(StringComparer.Ordinal)
    {
        "p250", "fiveseven", "tec9", "cz75a", "deagle", "elite", "revolver", "glock", "hkp2000", "usp_silencer"
    };

    internal RoundCaptureView(CapturedRound round, bool collected)
    {
        Round = round;
        Collected = collected;
    }

    /// <summary>The stored round.</summary>
    public CapturedRound Round { get; }

    public int Number => Round.Number;

    /// <summary>False when the collector did not run on the demo: only kills and the plant are in.</summary>
    public bool Collected { get; }

    /// <summary>Every kill with both players' spots, in tick order.</summary>
    public IReadOnlyList<CapturedKill> Kills => Round.Kills;

    /// <summary>The round's first kill, or null.</summary>
    public CapturedKill? Opening => Round.Kills.FirstOrDefault(k => k.Opening);

    /// <summary>The plant with the planter's spot, or null.</summary>
    public CapturedPlant? Plant => Round.Plant;

    /// <summary>Every purchase, in tick order.</summary>
    public IReadOnlyList<CapturedPurchase> Purchases => Round.Purchases;

    /// <summary>The freeze-end kit of every seated slot.</summary>
    public IReadOnlyList<RoundLoadout> Loadouts => Round.Loadouts;

    /// <summary>Each first AWP shot and the trail 0 to 3 s after it.</summary>
    public IReadOnlyList<FirstShotTrail> FirstShots => Round.FirstShots;

    /// <summary>Whether this was a pistol round by Round Facts.</summary>
    public bool IsPistol => Round.Pistol;

    /// <summary>
    ///     The side's AWP holders at <paramref name="minimum" /> confidence or above, the surest first. Two entries is
    ///     a double-AWP round. Empty when the collector did not run.
    /// </summary>
    /// <param name="side">2 T, 3 CT.</param>
    /// <param name="minimum">The least confidence listed; Medium leaves out AWPs owned and never used.</param>
    public IReadOnlyList<AwpHolder> AwpHolders(int side, AwpConfidence minimum = AwpConfidence.Medium) =>
    [
        .. Round.Awp.Where(a => a.Side == side)
            .Select(a => (Holding: a, Confidence: ConfidenceOf(a)))
            .Where(h => h.Confidence >= minimum)
            .Select(h => new AwpHolder(h.Holding.Slot, h.Holding.Side, h.Confidence!.Value, h.Holding))
            .OrderByDescending(h => h.Confidence)
            .ThenByDescending(h => h.Evidence.InHandFrames)
            .ThenBy(h => h.Slot)
    ];

    /// <summary>True when a side bought an AWP this round.</summary>
    /// <param name="side">2 T, 3 CT.</param>
    public bool AwpBought(int side) => Round.Awp.Any(a => a.Side == side && a.Bought);

    /// <summary>The pistol-round loadout of each of the side's players; empty when this was no pistol round.</summary>
    /// <param name="side">2 T, 3 CT.</param>
    public IReadOnlyList<PistolLoadout> PistolLoadouts(int side)
    {
        if (!Round.Pistol || !Collected)
        {
            return [];
        }

        List<PistolLoadout> loadouts = [];
        foreach (RoundLoadout kit in Round.Loadouts.Where(l => l.Side == side).OrderBy(l => l.Slot))
        {
            List<CapturedPurchase> bought = [.. Round.Purchases.Where(p => p.Slot == kit.Slot)];
            loadouts.Add(new PistolLoadout(kit.Slot, side,
                kit.Armor < 0 ? bought.Any(p => p.Item is "vest" or "vesthelm") : kit.Armor > 0,
                kit.Helmet ?? (bought.Any(p => p.Item == "vesthelm") ? true : null),
                kit.Defuser ?? (bought.Any(p => p.Item == "defuser") ? true : null),
                [.. bought.Where(p => _utility.Contains(p.Item)).Select(p => p.Item)],
                bought.LastOrDefault(p => _pistols.Contains(p.Item))?.Item,
                bought.Any(p => p.Item == "taser"),
                bought.Sum(p => p.Cost)));
        }

        return loadouts;
    }

    /// <summary>How sure a holding is, or null when it rests on nothing.</summary>
    public static AwpConfidence? ConfidenceOf(AwpHolding holding)
    {
        ArgumentNullException.ThrowIfNull(holding);
        bool owned = holding.AtFreezeEnd || holding.Bought || holding.PickedUp;
        bool used = holding.InHandFrames > 0 || holding.Shots > 0 || holding.Kills > 0;
        return (owned, used) switch
        {
            (true, true) => AwpConfidence.High,
            (false, true) => AwpConfidence.Medium,
            (true, false) => AwpConfidence.Low,
            _ => null
        };
    }
}

/// <summary>One demo's captures as a layer reads them.</summary>
public sealed class DemoCaptures
{
    private readonly Dictionary<int, RoundCaptureView> _rounds;

    internal DemoCaptures(RoundCapturesDocument document)
    {
        Document = document;
        _rounds = document.Rounds.ToDictionary(r => r.Number, r => new RoundCaptureView(r, document.Collected));
    }

    /// <summary>The stored part.</summary>
    public RoundCapturesDocument Document { get; }

    /// <summary>False when the collector did not run on this demo (see <see cref="RoundCapturesDocument.Collected" />).</summary>
    public bool Collected => Document.Collected;

    /// <summary>The demo's live rounds, in order.</summary>
    public IEnumerable<RoundCaptureView> Rounds => Document.Rounds.Select(r => _rounds[r.Number]);

    /// <summary>The round with this Round Facts number, or null.</summary>
    public RoundCaptureView? Round(int number) => _rounds.GetValueOrDefault(number);

    /// <summary>Every tactical and technical timeout, in tick order.</summary>
    public IReadOnlyList<RoundTimeout> Timeouts => Document.Timeouts;
}

/// <summary>
///     The one way a Scouting Report layer reads a demo's round captures (kills and plants with positions, buys,
///     AWP holders, pistol loadouts, first-shot trails, timeouts): through the round index, under the fingerprint in
///     force for the demo's map, so a part built under another one reads as absent with the reason, the way
///     <see cref="PositionsReader" /> treats positions. Counts the demos it could not read. Safe off the UI thread.
/// </summary>
public sealed class RoundCapturesReader
{
    private readonly Func<string?, string>? _fingerprintFor;
    private readonly RoundIndexStore? _store;
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <param name="store">The round index; null reads every demo as without captures.</param>
    /// <param name="fingerprintFor">The fingerprint current parts carry per map; null accepts any (a test host).</param>
    public RoundCapturesReader(RoundIndexStore? store, Func<string?, string>? fingerprintFor)
    {
        _store = store;
        _fingerprintFor = fingerprintFor;
    }

    /// <summary>Demos read without captures so far, stale ones included.</summary>
    public int WithoutCount
    {
        get
        {
            lock (_gate)
            {
                return _missing.Count + _stale.Count;
            }
        }
    }

    /// <summary>Demos whose captures were built under another fingerprint.</summary>
    public int StaleCount
    {
        get
        {
            lock (_gate)
            {
                return _stale.Count;
            }
        }
    }

    /// <summary>A demo's captures, or null when there are none or they are stale.</summary>
    public DemoCaptures? Read(string demoPath, string? map, string? sha256) => Read(demoPath, map, sha256, out _);

    /// <summary>A demo's captures, or null with the reason.</summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="map">Its map, for the fingerprint.</param>
    /// <param name="sha256">Its hash, or null.</param>
    /// <param name="miss">Why none were read.</param>
    public DemoCaptures? Read(string demoPath, string? map, string? sha256, out PositionsMiss miss)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        if (_store is null)
        {
            miss = PositionsMiss.NoIndex;
            Note(demoPath, miss);
            return null;
        }

        string? fingerprint = _fingerprintFor?.Invoke(map);
        if (_store.TryReadCaptures(demoPath, fingerprint, sha256) is { } captures)
        {
            miss = PositionsMiss.None;
            return new DemoCaptures(captures);
        }

        miss = fingerprint is not null && _store.TryReadCaptures(demoPath, null, sha256) is not null
            ? PositionsMiss.Stale
            : PositionsMiss.Missing;
        Note(demoPath, miss);
        return null;
    }

    private void Note(string demoPath, PositionsMiss miss)
    {
        lock (_gate)
        {
            (miss == PositionsMiss.Stale ? _stale : _missing).Add(demoPath);
        }
    }
}
