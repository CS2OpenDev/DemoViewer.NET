#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>Where the lurker stood at a moment: world units, the floor picked by Z.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
public readonly record struct LurkSpot(int X, int Y, int Z);

/// <summary>One round's lurk, beyond the bucket the Opening Tendencies count it in.</summary>
/// <param name="Name">The lurker.</param>
/// <param name="SplitSeconds">Seconds after freeze end that the lurker first stood alone.</param>
/// <param name="Clip">The Review clip around the split.</param>
/// <param name="SplitPlace">The lurker's place at the split, or null when unplaced.</param>
/// <param name="Via">The distinct places the lurker passes from the split on, in order, split place first.</param>
/// <param name="At30">The lurker's position at <see cref="LurkRoutes.SnapshotSeconds" />, null when dead or not sampled.</param>
/// <param name="SteamId">The lurker's SteamID64, or 0 when the positions file has none (a bot).</param>
public sealed record LurkDetail(
    string Name,
    double SplitSeconds,
    TendencyRound Clip,
    string? SplitPlace,
    IReadOnlyList<string> Via,
    LurkSpot? At30,
    ulong SteamId = 0);

/// <summary>One T round whose positions could be read: its buy, its opening clip and the lurk, null when nobody lurked.</summary>
/// <param name="Opening">The round clipped from freeze end.</param>
/// <param name="Buy">The side's buy that round.</param>
/// <param name="Lurk">The lurk, or null.</param>
public sealed record LurkSample(TendencyRound Opening, BuyType Buy, LurkDetail? Lurk);

/// <summary>Reads the lurk's route and its position at the snapshot from the round positions file.</summary>
public static class LurkRoutes
{
    /// <summary>Seconds after freeze end at which the lurker is drawn on the map.</summary>
    public const int SnapshotSeconds = 30;

    /// <summary>Seconds after the split the route is followed.</summary>
    public const int RouteSeconds = 20;

    /// <summary>Most places a route lists.</summary>
    public const int MaxVia = 4;

    /// <summary>The lurk of a round with the lurker's places and snapshot position.</summary>
    /// <param name="positions">The positions file.</param>
    /// <param name="round">The round.</param>
    /// <param name="lurk">The lurker's slot and the tick of the split, from <see cref="OpeningTendenciesService.Lurk" />.</param>
    /// <param name="name">The lurker's name.</param>
    /// <param name="clip">The clip around the split.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    public static LurkDetail Describe(RoundPositionsDocument positions, RoundFacts round, (int Slot, int Tick) lurk,
        string name, TendencyRound clip, int tickRate)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(clip);
        RoundPositionsRound? stored = positions.Round(round.Number);
        List<string> via = [];
        LurkSpot? at30 = null;
        string? splitPlace = null;
        if (stored is not null && positions.CadenceTicks > 0)
        {
            int end = round.PlantTick ?? round.EndTick ?? int.MaxValue;
            int from = Math.Max(0, positions.StepFor(stored, lurk.Tick));
            int to = Math.Min(stored.Pos.Count - 1, positions.StepFor(stored, lurk.Tick + RouteSeconds * tickRate));
            for (int step = from; step <= to; step++)
            {
                if (stored.FreezeEndTick + step * positions.CadenceTicks >= end)
                {
                    break;
                }

                foreach (RoundPosition p in stored.At(step).Where(p => p.Slot == lurk.Slot))
                {
                    string? place = positions.PlaceOf(p.PlaceId);
                    if (step == from)
                    {
                        splitPlace = place;
                    }

                    if (place is not null && !via.Contains(place, StringComparer.Ordinal) && via.Count < MaxVia)
                    {
                        via.Add(place);
                    }
                }
            }

            int tick30 = round.FreezeEndTick + SnapshotSeconds * tickRate;
            if (tick30 < end)
            {
                foreach (RoundPosition p in stored.At(positions.StepFor(stored, tick30)).Where(p => p.Slot == lurk.Slot))
                {
                    at30 = new LurkSpot(p.X, p.Y, p.Z);
                }
            }
        }

        return new LurkDetail(name, (lurk.Tick - round.FreezeEndTick) / (double)tickRate, clip, splitPlace, via, at30,
            positions.PlayerOf(lurk.Slot)?.SteamId64 ?? 0);
    }
}
