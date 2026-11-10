#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>One CT position sample: integer world units and the place the positions file named, or null.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="Place">The place name, or null for an unplaced tuple.</param>
public readonly record struct SetupSample(int X, int Y, int Z, string? Place);

/// <summary>A sample after first contact, with its seconds past contact.</summary>
/// <param name="Seconds">Seconds after first contact, 0 or more.</param>
/// <param name="Sample">The position.</param>
public readonly record struct SetupTimedSample(double Seconds, SetupSample Sample);

/// <summary>One CT player of one round: who, where they stood in the setup window, and where they went after contact.</summary>
/// <param name="Key">The SteamID64 as text, or <c>name:</c> and the name for a player the file carried no id for.</param>
/// <param name="Name">The name the demo's roster had.</param>
/// <param name="Setup">The samples inside the setup window.</param>
/// <param name="AfterContact">The samples from first contact on, in a planted round only.</param>
public sealed record SetupRoundPlayer(
    string Key,
    string Name,
    IReadOnlyList<SetupSample> Setup,
    IReadOnlyList<SetupTimedSample> AfterContact);

/// <summary>
///     The per-player view of one CT round the Setups layer reads: each CT's setup-window samples, the round's
///     result, and for a round the opponents planted after contact, every CT's positions from contact until
///     <see cref="SetupRoundCapture.AfterContactSeconds" /> later or the round's end.
/// </summary>
/// <param name="Won">The team (on CT) won the round.</param>
/// <param name="PlantSite">Where the bomb went down; <see cref="BombSite.Unknown" /> with no plant.</param>
/// <param name="PlantAfterContactSeconds">Seconds from first contact to the plant, or null without both.</param>
/// <param name="Players">The CT players with at least one sample.</param>
public sealed record SetupRoundDetail(
    bool Won,
    BombSite PlantSite,
    double? PlantAfterContactSeconds,
    IReadOnlyList<SetupRoundPlayer> Players);

/// <summary>Reads a <see cref="SetupRoundDetail" /> out of a round positions file while the heatmap reads its points.</summary>
public static class SetupRoundCapture
{
    /// <summary>How long after first contact a rotation is followed.</summary>
    public const int AfterContactSeconds = 40;

    /// <summary>The detail of one round, or null when no CT tuple fell in either window.</summary>
    /// <param name="positions">The demo's positions file.</param>
    /// <param name="stored">The round in it.</param>
    /// <param name="round">The round's Round Facts row.</param>
    /// <param name="rate">The demo's tick rate.</param>
    /// <param name="from">The setup window's first tick.</param>
    /// <param name="to">The setup window's last tick; before <paramref name="from" /> when it is empty.</param>
    public static SetupRoundDetail? Capture(RoundPositionsDocument positions, RoundPositionsRound stored,
        RoundFacts round, int rate, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(round);
        HashSet<int> ct = [.. stored.Ct];
        Dictionary<int, (List<SetupSample> Setup, List<SetupTimedSample> After)> bySlot = [];

        if (to >= from)
        {
            int last = positions.StepFor(stored, to);
            for (int step = Math.Max(0, positions.StepFor(stored, from)); step <= last; step++)
            {
                foreach (RoundPosition tuple in stored.At(step))
                {
                    if (ct.Contains(tuple.Slot))
                    {
                        Slot(bySlot, tuple.Slot).Setup.Add(Sample(positions, tuple));
                    }
                }
            }
        }

        double? plantAfter = null;
        if (round.FirstContactTick is { } contact && round.PlantTick is { } plant && plant >= contact
            && round.PlantSite is BombSite.A or BombSite.B && rate > 0 && positions.CadenceTicks > 0)
        {
            plantAfter = (plant - contact) / (double)rate;
            int end = contact + AfterContactSeconds * rate;
            if (round.EndTick is { } roundEnd)
            {
                end = Math.Min(end, roundEnd);
            }

            int last = positions.StepFor(stored, end);
            for (int step = Math.Max(0, positions.StepFor(stored, contact)); step <= last; step++)
            {
                int tick = stored.FreezeEndTick + step * positions.CadenceTicks;
                if (tick < contact)
                {
                    continue;
                }

                double seconds = (tick - contact) / (double)rate;
                foreach (RoundPosition tuple in stored.At(step))
                {
                    if (ct.Contains(tuple.Slot))
                    {
                        Slot(bySlot, tuple.Slot).After.Add(new SetupTimedSample(seconds, Sample(positions, tuple)));
                    }
                }
            }
        }

        if (bySlot.Count == 0)
        {
            return null;
        }

        List<SetupRoundPlayer> players =
        [
            .. bySlot.OrderBy(s => s.Key).Select(s =>
            {
                RoundPositionsPlayer? who = positions.PlayerOf(s.Key);
                string name = who?.Name ?? $"slot {s.Key.ToString(CultureInfo.InvariantCulture)}";
                return new SetupRoundPlayer(KeyOf(who?.SteamId64 ?? 0, name), name, s.Value.Setup, s.Value.After);
            })
        ];
        return new SetupRoundDetail(round.WinnerSide == 3, round.PlantSite, plantAfter, players);
    }

    /// <summary>The key a player is joined on: the SteamID64 as text, or <c>name:</c> and the name for a bot.</summary>
    /// <param name="steamId">The SteamID64, or 0.</param>
    /// <param name="name">The name.</param>
    public static string KeyOf(ulong steamId, string name) =>
        steamId != 0 ? steamId.ToString(CultureInfo.InvariantCulture) : "name:" + name;

    private static (List<SetupSample> Setup, List<SetupTimedSample> After) Slot(
        Dictionary<int, (List<SetupSample>, List<SetupTimedSample>)> bySlot, int slot)
    {
        if (!bySlot.TryGetValue(slot, out (List<SetupSample>, List<SetupTimedSample>) lists))
        {
            lists = ([], []);
            bySlot[slot] = lists;
        }

        return lists;
    }

    private static SetupSample Sample(RoundPositionsDocument positions, RoundPosition tuple) =>
        new(tuple.X, tuple.Y, tuple.Z, positions.PlaceOf(tuple.PlaceId));
}
