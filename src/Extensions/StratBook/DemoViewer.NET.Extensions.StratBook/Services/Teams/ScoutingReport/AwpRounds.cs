#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>How far an AWP trail runs: to 0:15, to 0:30, or to the round's first kill.</summary>
public enum AwpWindow
{
    To15,
    To30,
    Contact
}

/// <summary>One second of the AWP holder's path: where they were and the place there.</summary>
/// <param name="Second">Seconds from freeze end.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="Place">The place, or null when unplaced.</param>
public sealed record AwpPoint(int Second, int X, int Y, int Z, string? Place);

/// <summary>
///     One round of the team on one side, read for the AWP layer: who held the AWP, their path from freeze end at
///     the positions' 1 Hz, and the per-round answers the stat rows count. A round read without positions has no
///     path; one read without collected captures has no buy, opening, first-shot or save answer.
/// </summary>
public sealed class AwpRound
{
    public required TeamRound Round { get; init; }

    /// <summary>The demo's positions were read for the round.</summary>
    public bool PositionsRead { get; init; }

    /// <summary>The demo's captures were read and collected for the round.</summary>
    public bool CapturesRead { get; init; }

    /// <summary>The main holder's slot, or null when the side had no AWP in use.</summary>
    public int? HolderSlot { get; init; }

    /// <summary>The main holder's name, "" when the positions name none.</summary>
    public string HolderName { get; init; } = "";

    /// <summary>The main holder's SteamID64, or 0 when unknown (a bot, no positions).</summary>
    public ulong HolderSteamId { get; init; }

    /// <summary>Players seen using an AWP on the side this round: 2 is a double-AWP round.</summary>
    public int Holders { get; init; }

    /// <summary>The holder came from the positions' weapon samples alone, not the captures' evidence.</summary>
    public bool FromSamples { get; init; }

    /// <summary>The side bought an AWP this round; null without captures.</summary>
    public bool? Bought { get; init; }

    /// <summary>The holder's path, one point a second from freeze end while alive; gaps take the last point.</summary>
    public IReadOnlyList<AwpPoint> Path { get; init; } = [];

    /// <summary>Seconds from freeze end to the round's first kill, or null with none.</summary>
    public double? ContactSeconds { get; init; }

    /// <summary>The opening duel was the AWP's: true won (an AWP kill), false lost (the holder died); null otherwise.</summary>
    public bool? OpeningWon { get; init; }

    /// <summary>The holder moved more than <see cref="AwpRules.MovedAfterShotUnits" /> after their first shot; null unread.</summary>
    public bool? MovedAfterShot { get; init; }

    /// <summary>In a lost round, a holder kept the AWP alive; null when the round was not lost or is unread.</summary>
    public bool? SavedOnLoss { get; init; }

    public bool HasAwp => HolderSlot is not null;

    /// <summary>Where the trail stops for a window, in seconds from freeze end.</summary>
    /// <param name="window">The window.</param>
    public double EndOf(AwpWindow window) => window switch
    {
        AwpWindow.To15 => 15,
        AwpWindow.To30 => 30,
        _ => Math.Min(ContactSeconds ?? AwpRules.NoContactSeconds, AwpRules.MaxContactSeconds)
    };

    /// <summary>The path up to a window's end.</summary>
    /// <param name="window">The window.</param>
    public IReadOnlyList<AwpPoint> Trail(AwpWindow window)
    {
        double end = EndOf(window);
        return [.. Path.Where(p => p.Second <= end)];
    }

    /// <summary>T: the holder is in a mid place at or before <see cref="AwpRules.PeekMidSeconds" />; null without a path.</summary>
    public bool? PeeksMid => Path.Count == 0
        ? null
        : Path.Any(p => p.Second <= AwpRules.PeekMidSeconds && AwpRules.IsMid(p.Place));

    /// <summary>
    ///     CT: from <see cref="AwpRules.HoldFromSeconds" /> to the first kill the holder stays within
    ///     <see cref="AwpRules.HoldRadius" /> of where they stood then; null when the contact came first or the holder
    ///     was not sampled then.
    /// </summary>
    public bool? HoldsStartSpot
    {
        get
        {
            double contact = Math.Min(ContactSeconds ?? AwpRules.NoContactSeconds, AwpRules.MaxContactSeconds);
            if (contact <= AwpRules.HoldFromSeconds
                || Path.FirstOrDefault(p => p.Second == AwpRules.HoldFromSeconds) is not { } spot)
            {
                return null;
            }

            return Path.Where(p => p.Second >= AwpRules.HoldFromSeconds && p.Second <= contact)
                .All(p => AwpRules.FlatDistance(p.X, p.Y, spot.X, spot.Y) <= AwpRules.HoldRadius);
        }
    }
}

/// <summary>The AWP layer's per-round rules and thresholds.</summary>
public static class AwpRules
{
    /// <summary>The AWP's weapon class in the positions' weapon column.</summary>
    public const string AwpClass = "CWeaponAWP";

    /// <summary>Seconds from freeze end over which the positions' weapon samples name a holder when captures are missing.</summary>
    public const int HolderWindowSeconds = 20;

    /// <summary>
    ///     Samples in hand in that window that make a holder. More than one: a player carries the knife or a pistol
    ///     half the time (probes.md b), and one sample is also what a teammate picking it up for a second shows.
    /// </summary>
    public const int MinHolderSamples = 2;

    /// <summary>World units (XY) the holder must move within <see cref="MovedAfterShotSeconds" /> of their first shot.</summary>
    public const int MovedAfterShotUnits = 200;

    /// <summary>Seconds after the first shot that count for <see cref="MovedAfterShotUnits" />.</summary>
    public const int MovedAfterShotSeconds = 2;

    /// <summary>The T AWP peeks mid when in a mid place by this second.</summary>
    public const int PeekMidSeconds = 10;

    /// <summary>The CT AWP's start spot is where it stands at this second.</summary>
    public const int HoldFromSeconds = 10;

    /// <summary>World units (XY) from the start spot the CT AWP may drift and still hold it.</summary>
    public const int HoldRadius = 400;

    /// <summary>Where the "first contact" trail stops in a round with no kill before it.</summary>
    public const int NoContactSeconds = 40;

    /// <summary>The longest a "first contact" trail runs.</summary>
    public const int MaxContactSeconds = 60;

    /// <summary>The longest path kept per round.</summary>
    public const int MaxPathSeconds = 60;

    /// <summary>True for a place in mid: "Middle", "TopofMid", "MidDoors".</summary>
    /// <param name="place">The place.</param>
    public static bool IsMid(string? place) =>
        place is not null && place.Contains("mid", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an AWP item or weapon name: <c>awp</c>, <c>weapon_awp</c>.</summary>
    /// <param name="weapon">The name.</param>
    public static bool IsAwp(string? weapon) => RoundCapturesFold.NormalizeItem(weapon) == "awp";

    public static double FlatDistance(int x1, int y1, int x2, int y2)
    {
        double dx = x1 - x2;
        double dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    ///     One round's AWP read. The holder is the captures' surest one seen using it (<see cref="AwpConfidence.Medium" />
    ///     or above: in hand over the round, shooting or killing); without collected captures, the side's slot with the
    ///     most AWP samples in the first <see cref="HolderWindowSeconds" />, at least <see cref="MinHolderSamples" />.
    ///     Never one sample.
    /// </summary>
    /// <param name="round">The team's round.</param>
    /// <param name="positions">The demo's positions, or null.</param>
    /// <param name="captures">The round's captures, or null.</param>
    public static AwpRound Read(TeamRound round, RoundPositionsDocument? positions, RoundCaptureView? captures)
    {
        ArgumentNullException.ThrowIfNull(round);
        RoundPositionsRound? stored = positions?.CadenceTicks > 0 ? positions.Round(round.Number) : null;
        bool collected = captures is { Collected: true };
        HashSet<int> sideSlots = SideSlots(round, stored);

        int? holder = null;
        int count = 0;
        bool fromSamples = false;
        IReadOnlyList<AwpHolder> holders = collected ? captures!.AwpHolders(round.Side) : [];
        if (holders.Count > 0)
        {
            holder = holders[0].Slot;
            count = holders.Count;
        }
        else if (!collected && stored is not null && positions!.Weapons is { } weapons
                 && weapons.IndexOf(AwpClass) is var awpId and >= 0)
        {
            int lastStep = (int)Math.Ceiling(HolderWindowSeconds * round.TickRate / (double)positions.CadenceTicks);
            List<(int Slot, int Samples)> seen =
            [
                .. stored.Pos.Take(lastStep + 1).SelectMany(s => s)
                    .Where(p => p.WeaponId == awpId && sideSlots.Contains(p.Slot))
                    .GroupBy(p => p.Slot)
                    .Select(g => (g.Key, g.Count()))
                    .Where(g => g.Item2 >= MinHolderSamples)
                    .OrderByDescending(g => g.Item2).ThenBy(g => g.Key)
            ];
            if (seen.Count > 0)
            {
                holder = seen[0].Slot;
                count = seen.Count;
                fromSamples = true;
            }
        }

        RoundPositionsPlayer? player = holder is { } h ? positions?.PlayerOf(h) : null;
        IReadOnlyList<AwpPoint> path = holder is { } slot && stored is not null
            ? PathOf(positions!, stored, slot, round.TickRate)
            : [];
        double? contact = round.Facts.Kills.Count > 0 ? round.SecondsTo(round.Facts.Kills.Min(k => k.Tick)) : null;

        return new AwpRound
        {
            Round = round,
            PositionsRead = stored is not null,
            CapturesRead = collected,
            HolderSlot = holder,
            HolderName = player?.Name ?? "",
            HolderSteamId = player?.SteamId64 ?? 0,
            Holders = count,
            FromSamples = fromSamples,
            Bought = collected ? captures!.AwpBought(round.Side) : null,
            Path = path,
            ContactSeconds = contact,
            OpeningWon = collected && holder is not null ? OpeningOf(captures!, round.Side, holders) : null,
            MovedAfterShot = collected && holder is { } shooter ? Moved(captures!, shooter, round.TickRate) : null,
            SavedOnLoss = collected && holder is not null && round.Lost ? holders.Any(x => x.Saved) : null
        };
    }

    // The side's slots: the positions' CT list for the round, else Round Facts' seating.
    private static HashSet<int> SideSlots(TeamRound round, RoundPositionsRound? stored)
    {
        if (stored is not null)
        {
            HashSet<int> ct = [.. stored.Ct];
            return round.Side == 3
                ? ct
                : [.. stored.Pos.SelectMany(s => s).Select(p => p.Slot).Where(s => !ct.Contains(s))];
        }

        return [.. round.Own.Slots];
    }

    // One point a second while alive, a gap filled with the last point, up to the holder's last sample.
    private static List<AwpPoint> PathOf(RoundPositionsDocument positions, RoundPositionsRound stored, int slot,
        int tickRate)
    {
        List<AwpPoint> path = [];
        AwpPoint? last = null;
        int lastSecond = -1;
        for (int step = 0; step < stored.Pos.Count; step++)
        {
            int second = (int)Math.Round(step * positions.CadenceTicks / (double)tickRate);
            if (second > MaxPathSeconds)
            {
                break;
            }

            int at = stored.Pos[step].FindIndex(t => t.Slot == slot);
            if (at < 0)
            {
                continue;
            }

            RoundPosition p = stored.Pos[step][at];
            AwpPoint point = new(second, p.X, p.Y, p.Z, positions.PlaceOf(p.PlaceId));
            for (int fill = lastSecond + 1; last is not null && fill < second; fill++)
            {
                path.Add(last with { Second = fill });
            }

            if (second > lastSecond)
            {
                path.Add(point);
                lastSecond = second;
            }

            last = point;
        }

        return path;
    }

    private static bool? OpeningOf(RoundCaptureView captures, int side, IReadOnlyList<AwpHolder> holders)
    {
        if (captures.Opening is not { } opening)
        {
            return null;
        }

        if (opening.AttackerSide == side && IsAwp(opening.Weapon))
        {
            return true;
        }

        return opening.AttackerSide != side && holders.Any(h => h.Slot == opening.VictimSlot) ? false : null;
    }

    // The holder's first shot: moved when a point within the window, alive, is past the threshold from the shot.
    private static bool? Moved(RoundCaptureView captures, int slot, int tickRate)
    {
        if (captures.FirstShots.Where(s => s.Slot == slot).MinBy(s => s.Tick) is not { } shot
            || shot.Points.Count == 0 || shot.Points[0] is not { At: { } from })
        {
            return null;
        }

        List<CaptureSpot> after =
        [
            .. shot.Points.Skip(1)
                .Where(p => p.Alive && p.At is not null && p.Tick - shot.Tick <= MovedAfterShotSeconds * tickRate)
                .Select(p => p.At!)
        ];
        return after.Count == 0
            ? null
            : after.Any(p => FlatDistance(p.X, p.Y, from.X, from.Y) > MovedAfterShotUnits);
    }
}
