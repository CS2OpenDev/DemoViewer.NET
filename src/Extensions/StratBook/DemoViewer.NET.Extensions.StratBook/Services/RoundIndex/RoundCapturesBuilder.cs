namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>
///     Folds the event tables and the capture collector's frames into the captures part, round by round over the
///     Round Facts windows the index samples in. Pure: no I/O.
///     <para>
///         A live round runs from its freeze end to its end (the next round's freeze end, else one past the last
///         frame, when it has none). A tick before a freeze end and after the previous round's end is that round's
///         buy time: a purchase there is a freeze-time buy of the round it leads into.
///     </para>
/// </summary>
public static class RoundCapturesBuilder
{
    // A freeze-end snapshot and a Round Facts freeze end are the same event; allow a frame or two of drift.
    private const int FreezeEndSlack = 8;

    // How far before the first live round's freeze end a buy still counts as its own.
    private const int FirstBuySeconds = 60;

    /// <summary>Builds the part.</summary>
    /// <param name="facts">The demo's Round Facts rows.</param>
    /// <param name="lastFrameTick">The last frame's clock.</param>
    /// <param name="tickRate">The demo's tick rate.</param>
    /// <param name="events">The kill, death and plant rows.</param>
    /// <param name="frames">The collector's captures, or null when it did not run.</param>
    /// <param name="fingerprint">The index fingerprint the part is written under.</param>
    public static RoundCapturesDocument Build(RoundFactsRows facts, int lastFrameTick, int tickRate,
        RoundEventRows events, RoundCaptureFrames? frames, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(events);
        int rate = tickRate > 0 ? tickRate : 64;
        List<Window> windows = Windows(facts, lastFrameTick, rate);
        RoundCapturesDocument document = new()
        {
            Fingerprint = fingerprint,
            TickRate = rate,
            Collected = frames is not null,
            Rounds = [.. windows.Select(w => w.Capture)]
        };

        FoldKills(windows, events);
        foreach (PlantEventRow plant in events.Plants)
        {
            if (Live(windows, plant.Tick) is { } w && w.Capture.Plant is null)
            {
                w.Capture.Plant = new CapturedPlant(plant.Tick, plant.PlanterSlot, plant.SiteEntity, w.Facts.PlantSite,
                    CaptureSpot.Of(plant.Spot));
            }
        }

        if (frames is null)
        {
            return document;
        }

        foreach (CapturedPickup pickup in frames.Pickups.Where(p => p.Bought))
        {
            if (Owner(windows, pickup.Tick) is ({ } w, bool freezeTime))
            {
                w.Capture.Purchases.Add(new CapturedPurchase(pickup.Tick, pickup.Slot, pickup.Item, pickup.Cost,
                    freezeTime));
            }
        }

        foreach (Window w in windows)
        {
            FoldLoadouts(w, frames);
            FoldAwp(w, frames);
            foreach (CapturedTrail trail in frames.Trails.Where(t => t.ShotTick >= w.FreezeEnd && t.ShotTick < w.End))
            {
                w.Capture.FirstShots.Add(new FirstShotTrail(trail.Slot, trail.ShotTick,
                [
                    .. trail.Points.Select(p => new FirstShotPoint(p.Tick, CaptureSpot.Of(p.Position, p.Place), p.Alive))
                ]));
            }
        }

        foreach (CapturedTimeout timeout in frames.Timeouts)
        {
            int? before = windows.FirstOrDefault(w => w.FreezeEnd > timeout.StartTick)?.Capture.Number;
            document.Timeouts.Add(new RoundTimeout(timeout.StartTick, timeout.EndTick, timeout.Side, timeout.Technical,
                timeout.CtLeftBefore, timeout.TLeftBefore, timeout.CtLeftAfter, timeout.TLeftAfter, before));
        }

        return document;
    }

    private static void FoldKills(List<Window> windows, RoundEventRows events)
    {
        Dictionary<(int Tick, int Attacker, int Victim), DeathEventRow> deaths = [];
        foreach (DeathEventRow death in events.Deaths)
        {
            deaths.TryAdd((death.Tick, death.AttackerSlot, death.VictimSlot), death);
        }

        foreach (KillEventRow kill in events.Kills)
        {
            if (Live(windows, kill.Tick) is not { } w)
            {
                continue;
            }

            deaths.TryGetValue((kill.Tick, kill.AttackerSlot, kill.VictimSlot), out DeathEventRow? death);
            w.Capture.Kills.Add(new CapturedKill(kill.Tick, kill.AttackerSlot, kill.VictimSlot, kill.AttackerSide,
                kill.Weapon, kill.WeaponClass, kill.Headshot, kill.Trade, kill.TradedSlot, false,
                CaptureSpot.Of(kill.Attacker), CaptureSpot.Of(death?.Victim)));
        }

        foreach (Window w in windows)
        {
            List<CapturedKill> kills = w.Capture.Kills;
            if (kills.Count == 0)
            {
                continue;
            }

            // The opening is the round's first death; a death no one is credited with (the world, a fall) still
            // takes it, so a kill after one is no opening.
            int firstDeath = events.Deaths.Where(d => d.Tick >= w.FreezeEnd && d.Tick < w.End)
                .Select(d => d.Tick).DefaultIfEmpty(int.MaxValue).Min();
            int first = kills.FindIndex(k => k.AttackerSlot != k.VictimSlot);
            if (first >= 0 && kills[first].Tick <= firstDeath)
            {
                kills[first] = kills[first] with { Opening = true };
            }
        }
    }

    private static void FoldLoadouts(Window w, RoundCaptureFrames frames)
    {
        if (FreezeEndOf(w, frames) is not { } snapshot)
        {
            return;
        }

        foreach (CapturedLoadout slot in snapshot.Slots)
        {
            w.Capture.Loadouts.Add(new RoundLoadout(slot.Slot, w.SideOf(slot.Slot, slot.Team), slot.Alive, slot.Armor,
                slot.Flags < 0 ? null : (slot.Flags & RoundPosition.HelmetFlag) != 0,
                slot.Flags < 0 ? null : (slot.Flags & RoundPosition.DefuserFlag) != 0,
                slot.Money, slot.StartAccount, slot.CashSpent));
        }
    }

    private static void FoldAwp(Window w, RoundCaptureFrames frames)
    {
        Dictionary<int, Evidence> bySlot = [];

        Evidence Of(int slot)
        {
            if (!bySlot.TryGetValue(slot, out Evidence? evidence))
            {
                evidence = new Evidence();
                bySlot[slot] = evidence;
            }

            return evidence;
        }

        if (FreezeEndOf(w, frames) is { } snapshot)
        {
            foreach (CapturedLoadout slot in snapshot.Slots.Where(s => s.HasAwp && s.Alive))
            {
                Of(slot.Slot).AtFreezeEnd = true;
            }
        }
        else
        {
            foreach (CapturedAwpSpan span in frames.AwpSpans.Where(s => s.FromTick <= w.FreezeEnd && s.ToTick > w.FreezeEnd))
            {
                Of(span.Slot).AtFreezeEnd = true;
            }
        }

        foreach (CapturedPickup pickup in frames.Pickups.Where(p => p.Item == "awp" && p.Tick >= w.BuyStart && p.Tick < w.End))
        {
            if (pickup.Bought)
            {
                Of(pickup.Slot).Bought = true;
            }
            else
            {
                Of(pickup.Slot).PickedUp = true;
            }
        }

        foreach (CapturedInHand run in frames.InHand.Where(r => r.ToTick >= w.FreezeEnd && r.FromTick < w.End))
        {
            Evidence e = Of(run.Slot);
            e.InHandFrames += run.Frames;
            int from = Math.Max(run.FromTick, w.FreezeEnd);
            e.FirstInHand = e.FirstInHand is { } seen ? Math.Min(seen, from) : from;
        }

        foreach (CapturedShot shot in frames.AwpShots.Where(s => s.Tick >= w.FreezeEnd && s.Tick < w.End))
        {
            Of(shot.Slot).Shots++;
        }

        foreach (CapturedKill kill in w.Capture.Kills.Where(IsAwpKill))
        {
            Of(kill.AttackerSlot).Kills++;
        }

        // The round_end of this round fires between its freeze end and the next one. Pro GOTV carries none, so
        // without it a span over the round's end tick says the same: a span ends at its holder's death.
        if (frames.RoundEnds.FirstOrDefault(e => e.Tick >= w.FreezeEnd && e.Tick < w.NextFreezeEnd) is { } end)
        {
            foreach (int slot in end.AliveWithAwp)
            {
                Of(slot).AliveAtEnd = true;
            }
        }
        else
        {
            int endTick = w.Facts.EndTick ?? w.End - 1;
            foreach (CapturedAwpSpan span in frames.AwpSpans.Where(s => s.FromTick <= endTick && s.ToTick > endTick))
            {
                Of(span.Slot).AliveAtEnd = true;
            }
        }

        foreach ((int slot, Evidence e) in bySlot.OrderBy(kv => kv.Key))
        {
            w.Capture.Awp.Add(new AwpHolding(slot, w.SideOf(slot, 0), e.AtFreezeEnd, e.Bought, e.PickedUp,
                e.InHandFrames, e.FirstInHand, e.Shots, e.Kills, e.AliveAtEnd));
        }
    }

    /// <summary>True when a kill was made with an AWP, by the event's weapon or the class in hand.</summary>
    public static bool IsAwpKill(CapturedKill kill) =>
        RoundCapturesFold.NormalizeItem(kill.Weapon) == "awp"
        || string.Equals(kill.WeaponClass, "CWeaponAWP", StringComparison.Ordinal);

    private static CapturedFreezeEnd? FreezeEndOf(Window w, RoundCaptureFrames frames)
    {
        CapturedFreezeEnd? best = null;
        int bestDistance = int.MaxValue;
        foreach (CapturedFreezeEnd snapshot in frames.FreezeEnds)
        {
            int distance = Math.Abs(snapshot.Tick - w.FreezeEnd);
            if (distance <= FreezeEndSlack && distance < bestDistance)
            {
                best = snapshot;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static Window? Live(List<Window> windows, int tick)
    {
        foreach (Window w in windows)
        {
            if (tick >= w.FreezeEnd && tick < w.End)
            {
                return w;
            }
        }

        return null;
    }

    // The round a tick's buy belongs to: the live round it falls in, else the round its buy time leads into.
    private static (Window Round, bool FreezeTime)? Owner(List<Window> windows, int tick)
    {
        foreach (Window w in windows)
        {
            if (tick >= w.FreezeEnd && tick < w.End)
            {
                return (w, false);
            }

            if (tick >= w.BuyStart && tick < w.FreezeEnd)
            {
                return (w, true);
            }
        }

        return null;
    }

    private static List<Window> Windows(RoundFactsRows facts, int lastFrameTick, int tickRate)
    {
        List<RoundFacts> rounds = [.. facts.Rounds.OrderBy(r => r.Number)];
        List<Window> windows = [];
        int previousEnd = int.MinValue;
        for (int i = 0; i < rounds.Count; i++)
        {
            RoundFacts round = rounds[i];
            int nextFreezeEnd = i + 1 < rounds.Count ? rounds[i + 1].FreezeEndTick : lastFrameTick + 1;
            int end = round.EndTick ?? nextFreezeEnd;
            if (!round.IsLive || end <= round.FreezeEndTick)
            {
                previousEnd = Math.Max(previousEnd, end);
                continue;
            }

            Dictionary<int, int> sides = [];
            foreach (int slot in round.Ct.Slots)
            {
                sides[slot] = 3;
            }

            foreach (int slot in round.T.Slots)
            {
                sides[slot] = 2;
            }

            // With no round before it (the first live round after warmup), buy time reaches back a minute at most,
            // so a warmup buy is no pistol-round buy.
            int buyStart = i > 0
                ? Math.Max(previousEnd, rounds[i - 1].FreezeEndTick + 1)
                : round.FreezeEndTick - FirstBuySeconds * tickRate;
            windows.Add(new Window(round, buyStart, end, Math.Max(nextFreezeEnd, end), sides)
            {
                Capture = new CapturedRound
                {
                    Number = round.Number,
                    FreezeEndTick = round.FreezeEndTick,
                    EndTick = end,
                    Pistol = round.Ct.BuyType == BuyType.Pistol || round.T.BuyType == BuyType.Pistol
                }
            });
            previousEnd = end;
        }

        return windows;
    }

    private sealed class Evidence
    {
        public bool AtFreezeEnd { get; set; }

        public bool Bought { get; set; }

        public bool PickedUp { get; set; }

        public int InHandFrames { get; set; }

        public int? FirstInHand { get; set; }

        public int Shots { get; set; }

        public int Kills { get; set; }

        public bool AliveAtEnd { get; set; }
    }

    private sealed record Window(
        RoundFacts Facts,
        int BuyStart,
        int End,
        int NextFreezeEnd,
        Dictionary<int, int> Sides)
    {
        public required CapturedRound Capture { get; init; }

        public int FreezeEnd => Facts.FreezeEndTick;

        public int SideOf(int slot, int fallback) => Sides.TryGetValue(slot, out int side) ? side : fallback;
    }
}
