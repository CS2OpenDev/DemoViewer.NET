#region

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Economy and pistols (strip, map-wide): what the team buys in the two rounds after a pistol and how those
///     go, its force rate per side, forces won, the full save after a lost full buy, what its own eco looks like
///     at 0:20 (rush, stack or spread, from positions), and pistols won per side (<see cref="EconomyDerivation" />
///     over Round Facts). Under those, the side in view's pistol follow-ups, low-buy opponents, man advantage and
///     buy after a loss (Situational Behaviour). Every number opens its rounds. The buy filter does not apply: the
///     layer is about buys.
/// </summary>
public sealed class EconomyLayer() : ScoutingLayerBase("econ", "Economy", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "E/D")
{
    // One positions read per map per team load: the eco rounds' postures.
    private static readonly ConditionalWeakTable<ScoutingReportData, ConcurrentDictionary<string, Task<EcoPostures>>>
        _postures = [];

    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Economy and pistols";

    /// <inheritdoc />
    public override async Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        TeamRoundSet? set = await StripData.RoundsFor(context.Data).ConfigureAwait(false);
        SituationalBehaviourBlock? b = await LayerData.Situational(context).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TeamRound> rounds = set?.On(context.Map) ?? [];
        if (rounds.Count == 0 && b is null)
        {
            return context.Data.Sources.RoundFacts is null
                ? await LayerData.SituationalMissing(context).ConfigureAwait(false)
                : StripData.Missing(context, set);
        }

        List<ScoutingRead> reads = [];
        List<ScoutingInspectorSection> sections = [];
        ScoutingMiniChart? chart = null;
        int pistolCount = 0;
        if (rounds.Count > 0)
        {
            IReadOnlyList<AfterPistolRow> after = EconomyDerivation.AfterPistol(rounds);
            sections.Add(new ScoutingInspectorSection("Their buy in the rounds after a pistol · both sides",
                [.. after.Where(r => r.Rounds.Count > 0).Select(r => AfterPistolRow(context, r))]));
            EcoPostures postures = await PosturesFor(context).WaitAsync(cancellationToken).ConfigureAwait(false);
            sections.Add(new ScoutingInspectorSection("", KeyRows(context, rounds, postures)));
            chart = Chart(after);
            if (Read(after) is { } read)
            {
                reads.Add(new ScoutingRead(read, Suggested: StripData.SuggestOn(context)));
            }

            pistolCount = rounds.Count(r => r.Buy == BuyType.Pistol);
            IReadOnlyDictionary<string, DemoCaptures>? captures =
                await EconCaptureReads.For(context).WaitAsync(cancellationToken).ConfigureAwait(false);
            List<CapturedTeamRound> paired =
            [
                .. rounds.Select(r => (Round: r, View: captures?.GetValueOrDefault(r.DemoPath)?.Round(r.Number)))
                    .Where(p => p.View is not null).Select(p => new CapturedTeamRound(p.Round, p.View!))
            ];
            sections.AddRange(PistolSections(context, paired, captures is { Count: > 0 }));
            sections.Add(LowBuySection(context, paired, captures is { Count: > 0 }));
            if (PistolRead(context, paired) is { } pistolRead)
            {
                reads.Add(new ScoutingRead(pistolRead, "read pistol loadout"));
            }
        }

        if (b is not null)
        {
            int sideRounds = b.SideRounds.Count;
            int pistols = b.PistolRounds.Count;
            int pistolsWon = b.PistolOutcomes.Where(o => o.Label.StartsWith("won", StringComparison.Ordinal))
                .Sum(o => o.Count);
            if (pistols > 0)
            {
                reads.Add(new ScoutingRead(
                    $"They win {pistolsWon} of {pistols} {context.Side.Label()} pistols on {context.Map}.",
                    "read pistols"));
            }

            string side = context.Side.Label();
            sections.AddRange(
            [
                new ScoutingInspectorSection($"Pistols · {side}", [
                    .. Situation(context, b.PistolRounds, sideRounds, "pistols", "pistol rounds"),
                    .. Rows(context, b.PistolOutcomes, pistols, "pistol"),
                    .. Rows(context, b.PistolFollowUp, pistols, "")
                ]),
                new ScoutingInspectorSection($"Against a low buy · {side}", [
                    .. Situation(context, b.AntiEcoRounds, sideRounds, "vs low buy"),
                    .. Rows(context, b.AntiEcoBuyTypes, b.AntiEcoRounds.Count, "vs"),
                    .. Rows(context, b.AntiEcoOutcomes, b.AntiEcoRounds.Count, "vs low buy"),
                    .. Rows(context, b.AntiEcoContactClock, b.AntiEcoRounds.Count, "vs low buy, first contact")
                ]),
                new ScoutingInspectorSection($"With a man advantage · {side}", [
                    .. Situation(context, b.ManAdvantageRounds, sideRounds, "man advantage"),
                    .. Rows(context, b.ManAdvantage, b.ManAdvantageRounds.Count, "led"),
                    .. Rows(context, b.ManAdvantageOutcomes, b.ManAdvantageRounds.Count, "man advantage")
                ]),
                new ScoutingInspectorSection($"After a loss · {side}", [
                    .. Situation(context, b.RoundsLost, sideRounds, "losses"),
                    .. Rows(context, b.SaveDiscipline, b.RoundsLost.Count, "after a loss,")
                ])
            ]);
            if (rounds.Count == 0)
            {
                pistolCount = pistols;
            }
        }

        return new ScoutingLayerResult
        {
            CountLabel = Plural(pistolCount, "pistol"),
            Reads = reads,
            Sections = [.. sections.Where(s => s.Rows.Count > 0)],
            MiniChart = chart,
            Mini = b is null
                ? []
                :
                [
                    .. Situation(context, b.PistolRounds, b.SideRounds.Count, "pistols", "pistol rounds"),
                    .. Rows(context, b.PistolFollowUp, b.PistolRounds.Count, "").Take(2)
                ]
        };
    }

    /// <summary>
    ///     The mini: lost pistol → R2 over won pistol → R2, the mock's two rows, each a 100% stack of the buy on
    ///     the one-hue ramp with the rounds won at the end. Null when neither has a round with a known buy.
    /// </summary>
    /// <param name="after">The four after-pistol rows.</param>
    public static ScoutingStackChart? Chart(IReadOnlyList<AfterPistolRow> after)
    {
        ArgumentNullException.ThrowIfNull(after);
        List<ScoutingStackRow> rows =
        [
            .. after.Where(r => r.Label.EndsWith("R2", StringComparison.Ordinal) && r.Known > 0)
                .OrderBy(r => r.Label.StartsWith("Won", StringComparison.Ordinal))
                .Select(StackRow)
        ];
        return rows.Count == 0
            ? null
            : new ScoutingStackChart(rows) { Caption = "buy after the pistol, eco to full; rounds won at the end" };
    }

    /// <summary>The read: what they do after a lost pistol, else after a won one.</summary>
    /// <param name="after">The four after-pistol rows.</param>
    public static string? Read(IReadOnlyList<AfterPistolRow> after)
    {
        ArgumentNullException.ThrowIfNull(after);
        AfterPistolRow? lostR2 = after.FirstOrDefault(r => r.Label == "Lost pistol → R2");
        AfterPistolRow? lostR3 = after.FirstOrDefault(r => r.Label == "Lost pistol → R3");
        AfterPistolRow? wonR2 = after.FirstOrDefault(r => r.Label == "Won pistol → R2");
        if (lostR2 is { Known: > 0 })
        {
            string r2 = TopBuy(lostR2);
            string r3 = lostR3 is { Known: > 0 } ? $" and {TopBuy(lostR3)} R3" : "";
            return $"After a lost pistol they {r2} R2{r3}.";
        }

        return wonR2 is { Known: > 0 } ? $"After a won pistol they {TopBuy(wonR2)} R2 and win {Percent((double)wonR2.Won.Count / wonR2.Rounds.Count)} of them." : null;
    }

    private static string TopBuy(AfterPistolRow row)
    {
        TendencyBucket top = row.Buys.MaxBy(b => b.Count)!;
        string verb = top.Label switch
        {
            "Eco" => "save",
            "Semi" => "half-buy",
            "Force" => "force",
            _ => "full-buy"
        };
        return $"{verb} {Percent((double)top.Count / row.Known)} of the time in";
    }

    private static ScoutingStackRow StackRow(AfterPistolRow row) =>
        new(row.Label,
            [
                .. row.Buys.Select((b, i) => new ScoutingStackSegment(b.Label, (double)b.Count / row.Known, i))
                    .Where(s => s.Share > 0)
            ],
            row.Rounds.Count > 0 ? Percent((double)row.Won.Count / row.Rounds.Count) : "");

    private static ScoutingInspectorRow AfterPistolRow(ScoutingLayerContext context, AfterPistolRow row)
    {
        string mix = row.Known > 0
            ? string.Join(" · ", row.Buys.Where(b => b.Count > 0)
                .Select(b => $"{b.Label.ToLowerInvariant()} {Percent((double)b.Count / row.Known)}"))
            : "buy unknown";
        return new ScoutingInspectorRow
        {
            Label = row.Label,
            Value = $"won {CountText(row.Won.Count, row.Rounds.Count)}",
            StarText = row.Label.ToLowerInvariant(),
            Note = mix,
            Rounds = row.Rounds.Rounds,
            OpenTitle = StripData.MapTitle(context, row.Label.ToLowerInvariant())
        };
    }

    private static List<ScoutingInspectorRow> KeyRows(ScoutingLayerContext context, IReadOnlyList<TeamRound> rounds,
        EcoPostures postures)
    {
        List<ScoutingInspectorRow> rows = [];
        foreach (int side in (int[])[3, 2])
        {
            List<TeamRound> own = [.. rounds.Where(r => r.Side == side)];
            IReadOnlyList<TeamRound> buying = EconomyDerivation.Buying(own);
            if (buying.Count > 0)
            {
                string s = StripData.SideLabel(side);
                rows.Add(StripData.Share($"Force-buy rate · {s}",
                    [.. EconomyDerivation.Forces(buying).Select(r => r.Clip)], buying.Count, $"force rate {s}",
                    StripData.MapTitle(context, $"{s} force buys"), StripData.SideColor(side)));
            }
        }

        IReadOnlyList<TeamRound> forces = EconomyDerivation.Forces(rounds);
        if (forces.Count > 0)
        {
            rows.Add(StripData.Share("Forces that won", [.. forces.Where(r => r.Won).Select(r => r.Clip)],
                forces.Count, "forces won", StripData.MapTitle(context, "forces won")));
        }

        (IReadOnlyList<TeamRound> lostFull, IReadOnlyList<TeamRound> saved) = EconomyDerivation.FullSaves(rounds);
        if (lostFull.Count > 0)
        {
            rows.Add(StripData.Share("Full save after a lost full buy", [.. saved.Select(r => r.Clip)],
                lostFull.Count, "full save after a lost full buy",
                StripData.MapTitle(context, "full save after a lost full buy"),
                note: "an eco straight after"));
        }

        foreach (int side in (int[])[2, 3])
        {
            if (postures.Read.GetValueOrDefault(side) is not { Count: > 0 } read)
            {
                continue;
            }

            string s = StripData.SideLabel(side);
            EcoPosture shape = side == 3 ? EcoPosture.Stack : EcoPosture.Rush;
            string word = side == 3 ? "stack" : "rush";
            int ecos = postures.Ecos.GetValueOrDefault(side);
            rows.Add(StripData.Share($"On their own eco · {s} · {word}",
                [.. read.Where(p => p.Posture == shape).Select(p => p.Clip)], read.Count, $"own eco {word} {s}",
                StripData.MapTitle(context, $"{s} eco {word}"), StripData.SideColor(side),
                $"positions at 0:{EconomyDerivation.PostureSeconds:00}, read for {read.Count} of {Plural(ecos, "eco")}"));
            rows.Add(StripData.Share($"On their own eco · {s} · spread",
                [.. read.Where(p => p.Posture == EcoPosture.Spread).Select(p => p.Clip)], read.Count,
                $"own eco spread {s}", StripData.MapTitle(context, $"{s} eco spread"), StripData.SideColor(side),
                "saving or playing for picks"));
        }

        if (postures.Read.Count == 0 && rounds.Any(r => r.Buy == BuyType.Eco))
        {
            rows.Add(new ScoutingInspectorRow
            {
                Label = "On their own eco",
                StarText = "own eco",
                Note = postures.HasPositions
                    ? "no eco round with three or more players read at 0:20"
                    : "needs round positions, which these demos do not have yet"
            });
        }

        foreach (int side in (int[])[3, 2])
        {
            List<TeamRound> pistols = [.. rounds.Where(r => r.Side == side && r.Buy == BuyType.Pistol)];
            if (pistols.Count > 0)
            {
                string s = StripData.SideLabel(side);
                rows.Add(StripData.Share($"Pistols won · {s}", [.. pistols.Where(r => r.Won).Select(r => r.Clip)],
                    pistols.Count, $"pistols won {s}", StripData.MapTitle(context, $"{s} pistols won"),
                    StripData.SideColor(side)));
            }
        }

        return rows;
    }

    /// <summary>One section per side: what the team's players buy in the pistol round.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="rounds">The team's rounds on the map that have captures.</param>
    /// <param name="hasCaptures">Any demo of the map had captures.</param>
    public static IEnumerable<ScoutingInspectorSection> PistolSections(ScoutingLayerContext context,
        IReadOnlyList<CapturedTeamRound> rounds, bool hasCaptures)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rounds);
        if (!hasCaptures)
        {
            yield return new ScoutingInspectorSection("Pistol loadout", [
                new ScoutingInspectorRow
                {
                    Label = "Pistol loadout",
                    StarText = "pistol loadout",
                    Note = context.Data.Sources.RoundIndex is null
                        ? ScoutingLayerResult.NotOnThisHost
                        : "needs round captures, which these demos do not have yet"
                }
            ]);
            yield break;
        }

        foreach (int side in (int[])[3, 2])
        {
            if (EconomyLoadouts.PistolKitOf(rounds, side) is not { } kit)
            {
                continue;
            }

            string s = StripData.SideLabel(side);
            ScoutingColor color = StripData.SideColor(side);
            List<ScoutingInspectorRow> rows =
            [
                new()
                {
                    Label = $"Pistol rounds · {s}",
                    Value = Plural(kit.Rounds.Count, "round"),
                    StarText = $"pistol rounds {s}",
                    Note = $"{Plural(kit.Players, "player loadout")} read",
                    Rounds = [.. kit.Rounds.Select(r => r.Clip)],
                    OpenTitle = StripData.MapTitle(context, $"{s} pistol rounds")
                },
                PistolRow(context, $"Kevlar · {s}", kit.Armor, kit.Players, $"{s} pistol kevlar", color),
                PistolRow(context, $"Helmet · {s}", kit.Helmet, kit.Players, $"{s} pistol helmet", color),
                PistolRow(context, $"Utility bought · {s}", kit.Utility, kit.Players, $"{s} pistol utility", color,
                    Items(kit.UtilityItems, 4)),
                PistolRow(context, $"Pistol upgrade · {s}", kit.Upgrade, kit.Players, $"{s} pistol upgrade", color,
                    Items(kit.UpgradeNames, 4))
            ];
            if (side == 3)
            {
                rows.Add(PistolRow(context, "Defuse kit · CT", kit.Defuser, kit.Players, "CT pistol defuse kit", color));
            }

            yield return new ScoutingInspectorSection($"Pistol loadout · {s}", rows);
        }
    }

    /// <summary>The weapons the team bought on eco, semi and force rounds, one row per buy type.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="rounds">The team's rounds on the map that have captures.</param>
    /// <param name="hasCaptures">Any demo of the map had captures.</param>
    public static ScoutingInspectorSection LowBuySection(ScoutingLayerContext context,
        IReadOnlyList<CapturedTeamRound> rounds, bool hasCaptures)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rounds);
        const string title = "Weapons bought on low buys";
        if (!hasCaptures)
        {
            return new ScoutingInspectorSection(title, []);
        }

        List<ScoutingInspectorRow> rows = [];
        foreach (LowBuyWeapons low in EconomyLoadouts.LowBuys(rounds))
        {
            string buy = EconomyDerivation.ShortLabel(low.Buy);
            rows.Add(new ScoutingInspectorRow
            {
                Label = $"{buy} · {Plural(low.Rounds.Count, "round")}",
                Value = low.Weapons.Count == 0 ? "no weapon bought" : Items(low.Weapons, 5),
                StarText = $"{buy.ToLowerInvariant()} weapons",
                Rounds = [.. low.Rounds.Select(r => r.Clip)],
                OpenTitle = StripData.MapTitle(context, $"{buy.ToLowerInvariant()} rounds")
            });
        }

        if (rows.Count > 0)
        {
            rows.Add(new ScoutingInspectorRow
            {
                Label = "About these counts", StarText = "low buy weapons note", Note = EconomyLoadouts.BuyCaveat
            });
        }

        return new ScoutingInspectorSection(title, rows);
    }

    /// <summary>The read: what the side in view buys in the pistol round.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="rounds">The team's rounds on the map that have captures.</param>
    public static string? PistolRead(ScoutingLayerContext context, IReadOnlyList<CapturedTeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rounds);
        if (EconomyLoadouts.PistolKitOf(rounds, (int)context.Side) is not { } kit)
        {
            return null;
        }

        List<string> parts = [$"kevlar {Percent((double)kit.Armor.Players / kit.Players)}"];
        if (kit.Utility.Players > 0)
        {
            parts.Add($"utility {Percent((double)kit.Utility.Players / kit.Players)}");
        }

        if (kit.UpgradeNames.Count > 0)
        {
            parts.Add($"a {kit.UpgradeNames[0].Name} {Percent((double)kit.UpgradeNames[0].Count / kit.Players)}");
        }

        return $"On {context.Side.Label()} pistols they buy {string.Join(", ", parts)} of the time.";
    }

    private static ScoutingInspectorRow PistolRow(ScoutingLayerContext context, string label, PistolShare share,
        int players, string star, ScoutingColor color, string? note = null) =>
        new()
        {
            Label = label,
            Value = $"{share.Players} of {Plural(players, "player")}",
            StarText = star,
            BarPercent = players > 0 ? 100.0 * share.Players / players : null,
            Swatch = color,
            Note = string.IsNullOrEmpty(note) ? Percent(players > 0 ? (double)share.Players / players : 0) : $"{Percent(players > 0 ? (double)share.Players / players : 0)} · {note}",
            Rounds = [.. share.Rounds.Select(r => r.Clip)],
            OpenTitle = StripData.MapTitle(context, star)
        };

    private static string Items(IReadOnlyList<BoughtCount> items, int take) =>
        string.Join(", ", items.Take(take).Select(i => $"{i.Name} ×{i.Count}"));

    private static Task<EcoPostures> PosturesFor(ScoutingLayerContext context) =>
        _postures.GetValue(context.Data, _ => new ConcurrentDictionary<string, Task<EcoPostures>>(
                StringComparer.OrdinalIgnoreCase))
            .GetOrAdd(context.Map, map => ReadPosturesAsync(context.Data, map));

    private static async Task<EcoPostures> ReadPosturesAsync(ScoutingReportData data, string map)
    {
        TeamRoundSet? set = await StripData.RoundsFor(data).ConfigureAwait(false);
        SetupAreaClassifier areas = await StripData.AreasFor(data, map).ConfigureAwait(false);
        List<TeamRound> ecos = [.. set?.On(map).Where(r => r.Buy == BuyType.Eco) ?? []];
        return await ScoutingLayerContext.Offload(() => EcoPostures.Load(data, map, ecos, areas))
            .ConfigureAwait(false);
    }

    // A situation's own sample row ("vs low buy: 4 of 12 rounds"), titled the way the Situational section was.
    private static IEnumerable<ScoutingInspectorRow> Situation(ScoutingLayerContext context, TendencyBucket bucket,
        int rounds, string word, string? label = null)
    {
        if (bucket.Count > 0)
        {
            yield return Row(context, bucket, rounds, "", label: label ?? word) with
            {
                StarText = word, OpenTitle = $"{context.Prefix} · {word}"
            };
        }
    }

    private sealed record Posed(EcoPosture Posture, TendencyRound Clip);

    // The eco rounds of a map with a posture read, per side, and how many ecos each side had.
    private sealed record EcoPostures(
        IReadOnlyDictionary<int, List<Posed>> Read,
        IReadOnlyDictionary<int, int> Ecos,
        bool HasPositions)
    {
        public static EcoPostures Load(ScoutingReportData data, string map, IReadOnlyList<TeamRound> ecos,
            SetupAreaClassifier areas)
        {
            Dictionary<int, List<Posed>> read = [];
            Dictionary<int, int> count = [];
            Dictionary<string, RoundPositionsDocument?> files = new(StringComparer.Ordinal);
            bool any = false;
            foreach (TeamRound round in ecos)
            {
                count[round.Side] = count.GetValueOrDefault(round.Side) + 1;
                if (!files.TryGetValue(round.DemoPath, out RoundPositionsDocument? file))
                {
                    try
                    {
                        file = data.Positions.Read(round.DemoPath, map, round.Sha256);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or InvalidOperationException)
                    {
                        file = null;
                    }

                    files[round.DemoPath] = file;
                }

                if (file is null)
                {
                    continue;
                }

                any = true;
                IReadOnlyList<SetupSample> samples =
                    EconomyDerivation.OwnAt(file, round, EconomyDerivation.PostureSeconds);
                if (EconomyDerivation.Posture([.. samples.Select(areas.Of)], round.Side) is not { } posture)
                {
                    continue;
                }

                if (!read.TryGetValue(round.Side, out List<Posed>? list))
                {
                    list = [];
                    read[round.Side] = list;
                }

                int at = round.Facts.FreezeEndTick + EconomyDerivation.PostureSeconds * round.TickRate;
                list.Add(new Posed(posture, round.Around(at)));
            }

            return new EcoPostures(read, count, any);
        }
    }
}
