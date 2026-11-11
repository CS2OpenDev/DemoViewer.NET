#region

using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Zones;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The whole Scouting Report shell over a realistic synthetic team, in the light and the dark theme at 1280 and
///     1440 wide: the CT stage with its default layer on de_nuke (two floors), the AWP view on de_mirage, the page
///     while a team loads, and the empty page before one is picked. The positions sit on the maps' own places, read
///     from the shipped zone data, so the marks land on the floors. The PNGs land in
///     <see cref="HeadlessSession.ArtifactDir" /> as <c>p4-scouting-*.png</c> for a person to look at.
/// </summary>
[NotInParallel]
[Category("Render")]
public class ScoutingReportShellRenderTests
{
    public static IEnumerable<(string Theme, int Width)> Cases() =>
        from theme in (string[])["light", "dark"]
        from width in (int[])[1280, 1440]
        select (theme, width);

    private static async Task<T> OnUi<T>(Func<Task<T>> body)
    {
        T result = default!;
        await HeadlessSession.RunOnUi(async () => result = await body());
        return result;
    }

    private static async Task<T> InTheme<T>(string theme, Func<Task<T>> body)
    {
        ThemeVariant? original = Application.Current?.RequestedThemeVariant;
        try
        {
            if (Application.Current is { } app)
            {
                app.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            }

            return await body();
        }
        finally
        {
            if (Application.Current is { } app)
            {
                app.RequestedThemeVariant = original;
            }
        }
    }

    private static Window Show(ScoutingReportTabViewModel vm, int width, int height, string theme)
    {
        Window window = new()
        {
            Width = width,
            Height = height,
            RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light,
            Content = new ScoutingReportTabView { DataContext = vm }
        };
        window.Show();
        UxConsistencyRenderTests.Settle();
        UxConsistencyRenderTests.Settle();
        return window;
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task TheShell_RendersARealisticTeam_InEachThemeAndWidth(string theme, int width)
    {
        (double nukeStage, int nukeFloors, string inspector, bool mono, List<string> shown, double inspectorHeight,
            double stagePanelHeight, bool scrolls) = await OnUi(
            () => InTheme(theme, async () =>
            {
                using SyntheticTeam team = await SyntheticTeam.Create();
                QueuedPost posted = new();
                using ScoutingReportTabViewModel vm = team.Tab(posted);
                vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
                await ScoutingReportTestKit.Settle(vm, posted);
                vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
                vm.Side = ScoutingSide.Ct;
                await ScoutingReportTestKit.Settle(vm, posted);

                Window window = Show(vm, width, 2600, theme);
                ScoutingMapHost host = window.GetVisualDescendants().OfType<ScoutingMapHost>().Single();
                double stage = host.Bounds.Height;
                int floors = host.Map.Space?.Levels.Count ?? 0;
                UxConsistencyRenderTests.Save(window, $"p4-scouting-ct-nuke-{theme}-{width}.png");
                string title = vm.Layers.Inspector.Title;
                ScoutingReportTabView view = (ScoutingReportTabView)window.Content!;
                double inspectorHeight = view.FindControl<Border>("InspectorPanel")!.Bounds.Height;
                double stagePanelHeight = view.FindControl<Border>("StagePanel")!.Bounds.Height;
                bool scrolls = view.FindControl<ScrollViewer>("InspectorScroll") is { IsEffectivelyVisible: true };
                Button export = window.GetVisualDescendants().OfType<Button>()
                    .First(b => b.Classes.Contains("primary"));
                bool monospace = export.FontFamily.ToString().Contains("mono", StringComparison.OrdinalIgnoreCase)
                                 || export.FontFamily.ToString().Contains("Consolas", StringComparison.OrdinalIgnoreCase);
                List<string> text = [.. window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "")];

                // The AWP view on the single-floor map, at the same size.
                vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_mirage"));
                await ScoutingReportTestKit.Settle(vm, posted);
                vm.Layers.Focus(vm.Layers.Find("awp")!);
                await ScoutingReportTestKit.Settle(vm, posted);
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"p4-scouting-awp-mirage-{theme}-{width}.png");

                // The T side's default layer on the single-floor map.
                vm.Side = ScoutingSide.T;
                await ScoutingReportTestKit.Settle(vm, posted);
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"p4-scouting-t-mirage-{theme}-{width}.png");

                // And the T side's default layer back on de_nuke.
                vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
                vm.Side = ScoutingSide.T;
                await ScoutingReportTestKit.Settle(vm, posted);
                UxConsistencyRenderTests.Settle();
                UxConsistencyRenderTests.Save(window, $"p4-scouting-t-nuke-{theme}-{width}.png");
                window.Close();
                return (stage, floors, title, monospace, text, inspectorHeight, stagePanelHeight, scrolls);
            }));

        Console.WriteLine($"[p4-shell] {theme} {width}: stage {nukeStage:0} px for {nukeFloors} floors");
        using (Assert.Multiple())
        {
            await Assert.That(inspector).IsEqualTo("Setups and rotations");
            await Assert.That(inspectorHeight).IsLessThanOrEqualTo(Math.Max(560, stagePanelHeight) + 1)
                .Because("a long layer scrolls inside the inspector instead of stretching the page");
            await Assert.That(scrolls).IsTrue();
            await Assert.That(nukeFloors).IsEqualTo(2);
            await Assert.That(nukeStage).IsGreaterThanOrEqualTo(nukeFloors * 400)
                .Because("each floor of a stacked map gets a full band, not half of one map's height");
            await Assert.That(mono).IsFalse().Because("the export label is a word, not code");
            await Assert.That(shown).Contains("Map pool");
            await Assert.That(shown).Contains("Two layouts this release. Tell us which you prefer");
            await Assert.That(shown).Contains("Give feedback");
            await Assert.That(shown.Any(t => t.StartsWith("Nuke · CT · all buys", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task WhileATeamLoads_TheStageInspectorStripAndPoolDrawSkeletons(string theme, int width)
    {
        (bool inspectorBuilding, string stageNote, bool stripBuilding, int skeletons, bool poolReading) =
            await OnUi(() => InTheme(theme, async () =>
            {
                using SyntheticTeam team = await SyntheticTeam.Create();
                List<TaskCompletionSource> held = [];
                using ScoutingReportTabViewModel vm = team.Tab(new QueuedPost(), _ => _ =>
                {
                    TaskCompletionSource never = new();
                    held.Add(never);
                    return never.Task;
                });
                vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
                Window window = Show(vm, width, 1800, theme);
                UxConsistencyRenderTests.Save(window, $"p4-scouting-loading-{theme}-{width}.png");
                int skel = window.GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Classes.Contains("skel") && b.IsEffectivelyVisible);
                (bool, string, bool, int, bool) seen = (vm.Layers.Inspector.IsBuilding, vm.Layers.StageNote,
                    vm.Layers.StripMinis.All(m => m.IsBuilding), skel, vm.Maps.All(m => m.IsReading));
                window.Close();
                return seen;
            }));

        using (Assert.Multiple())
        {
            await Assert.That(inspectorBuilding).IsTrue();
            await Assert.That(stageNote).StartsWith("Reading the rounds for");
            await Assert.That(stripBuilding).IsTrue();
            await Assert.That(poolReading).IsTrue();
            await Assert.That(skeletons).IsGreaterThan(20);
        }
    }

    [Test]
    public async Task BeforeATeamIsPicked_ThePageSaysWhatPickingOneShows_InBothThemes()
    {
        List<bool> awaiting = [];
        foreach (string theme in (string[])["light", "dark"])
        {
            awaiting.Add(await OnUi(() => InTheme(theme, async () =>
            {
                using SyntheticTeam team = await SyntheticTeam.Create();
                using ScoutingReportTabViewModel vm = team.Tab(new QueuedPost());
                Window window = Show(vm, 1280, 800, theme);
                UxConsistencyRenderTests.Save(window, $"p4-scouting-pick-a-team-{theme}.png");
                bool shown = UxConsistencyRenderTests.Shows(window, "Pick a team to open its report");
                window.Close();
                return shown && vm.IsAwaitingTeam;
            })));
        }

        await Assert.That(awaiting).IsEquivalentTo([true, true]);
    }

    [Test]
    public async Task TabWalksTheStageThenTheInspectorThenTheStrip_AndArrowsMoveTheMapTabsAndTheSide()
    {
        List<string> order = [];
        string mapAfterRight = "", mapAfterLeft = "";
        ScoutingSide sideAfterRight = ScoutingSide.Ct;
        List<string> starNames = [];
        await HeadlessSession.RunOnUi(async () =>
        {
            using SyntheticTeam team = await SyntheticTeam.Create();
            QueuedPost posted = new();
            using ScoutingReportTabViewModel vm = team.Tab(posted);
            vm.SelectedTeam = vm.Teams.Single(t => t.Id == team.TeamA);
            await ScoutingReportTestKit.Settle(vm, posted);
            vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == "de_nuke"));
            await ScoutingReportTestKit.Settle(vm, posted);
            Window window = Show(vm, 1440, 2600, "light");

            // From the layer chips' first checkbox, Tab to the end of the work area, naming where focus lands.
            CheckBox first = window.GetVisualDescendants().OfType<CheckBox>()
                .First(c => (AutomationProperties.GetName(c) ?? "").StartsWith("Draw ", StringComparison.Ordinal));
            first.Focus(NavigationMethod.Tab);
            for (int i = 0; i < 80 && order.Count < 80; i++)
            {
                if (window.FocusManager?.GetFocusedElement() is Control focused)
                {
                    order.Add(Region(focused));
                }

                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                UxConsistencyRenderTests.Settle();
            }

            // The map tabs: Right opens the next map, Left the one before.
            Button tab = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("maptab") && b.Classes.Contains("on"));
            tab.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            UxConsistencyRenderTests.Settle();
            mapAfterRight = vm.SelectedMap?.Map ?? "";
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            UxConsistencyRenderTests.Settle();
            mapAfterLeft = vm.SelectedMap?.Map ?? "";

            RadioButton ct = window.GetVisualDescendants().OfType<RadioButton>()
                .First(r => AutomationProperties.GetName(r) == "CT side");
            ct.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            UxConsistencyRenderTests.Settle();
            sideAfterRight = vm.Side;
            await ScoutingReportTestKit.Settle(vm, posted);
            UxConsistencyRenderTests.Settle();
            starNames = [.. window.GetVisualDescendants().OfType<Button>()
                .Select(b => AutomationProperties.GetName(b) ?? "")
                .Where(n => n.StartsWith("Star ", StringComparison.Ordinal) || n.StartsWith("Unstar ", StringComparison.Ordinal))];
            window.Close();
        });

        List<string> regions = [.. order.Where(r => r.Length > 0).Distinct()];
        Console.WriteLine($"[p4-tab] {string.Join(" > ", order)}");
        using (Assert.Multiple())
        {
            await Assert.That(regions).Contains("chips");
            await Assert.That(regions).Contains("inspector");
            await Assert.That(regions).Contains("strip");
            await Assert.That(regions).IsEquivalentTo(
                    regions.OrderBy(r => Array.IndexOf((string[])["chips", "stage", "inspector", "strip"], r)),
                    TUnit.Assertions.Enums.CollectionOrdering.Matching)
                .Because("Tab goes chips, stage, inspector, then the strip under the map");
            await Assert.That(mapAfterRight).IsNotEqualTo("de_nuke");
            await Assert.That(mapAfterLeft).IsEqualTo("de_nuke");
            await Assert.That(sideAfterRight).IsEqualTo(ScoutingSide.T);
            await Assert.That(starNames).IsNotEmpty();
            await Assert.That(starNames.Distinct().Count()).IsEqualTo(starNames.Count)
                .Because("every star names its line and the scope it files under");
        }

        // Which work-area region a focused control sits in.
        static string Region(Control focused)
        {
            foreach (Visual v in focused.GetSelfAndVisualAncestors())
            {
                if (v is Border b && KeyboardNavigation.GetTabNavigation(b) == KeyboardNavigationMode.Local)
                {
                    return KeyboardNavigation.GetTabIndex(b) switch
                    {
                        2 => "stage",
                        3 => "inspector",
                        4 => "strip",
                        _ => ""
                    };
                }

                if (v is Grid w && KeyboardNavigation.GetTabNavigation(w) == KeyboardNavigationMode.Local
                                     && KeyboardNavigation.GetTabIndex(w) == 1)
                {
                    return "chips";
                }
            }

            return "";
        }
    }

    /// <summary>
    ///     Team A (slots 0..4) over eight demos: four on de_nuke, three on de_mirage and one on de_inferno (a thin
    ///     sample), against three opponents. Sixteen rounds a demo: A is CT for 1 to 8 and T for 9 to 16. Every demo
    ///     has Round Facts, positions on the map's own places (CT setups that vary by round, the AWP on slot 1) and
    ///     captures with purchases, AWP holdings, pistol loadouts and a timeout.
    /// </summary>
    internal sealed class SyntheticTeam : IDisposable
    {
        private const int Rate = 64;
        private const int Rounds = 16;

        private static readonly int[] _a = [0, 1, 2, 3, 4];
        private static readonly int[] _b = [5, 6, 7, 8, 9];
        private static readonly string[] _names = ["kyto", "vorn", "Ress", "malo", "tiq"];

        private static readonly Func<Action, Task> _inline = a =>
        {
            a();
            return Task.CompletedTask;
        };

        private static readonly (string Map, int Day, int Opponent)[] _demos =
        [
            ("de_nuke", 1, 1), ("de_nuke", 6, 2), ("de_nuke", 11, 3), ("de_nuke", 15, 1),
            ("de_mirage", 3, 2), ("de_mirage", 9, 3), ("de_mirage", 14, 1), ("de_inferno", 12, 2)
        ];

        private SyntheticTeam(DemoCacheStore cache, TeamIdentityService teams, RoundIndexStore store,
            RoundIndexPlaceSources sources, Guid teamA)
        {
            Cache = cache;
            Teams = teams;
            Store = store;
            Sources = sources;
            TeamA = teamA;
        }

        public DemoCacheStore Cache { get; }
        public TeamIdentityService Teams { get; }
        public RoundIndexStore Store { get; }
        public RoundIndexPlaceSources Sources { get; }
        public Guid TeamA { get; }

        public void Dispose() => Teams.Dispose();

        private static string Id(int n) => $"7656{n:D4}";

        private static int Freeze(int n) => n * 10000;

        /// <summary>The tab over the team, with every source a desktop host gives it but the grenade index.</summary>
        public ScoutingReportTabViewModel Tab(QueuedPost posted, Func<string, Func<Action, Task>>? runSection = null,
            ScoutingReportNotesStore? notes = null, Action<IReadOnlyList<string>>? requestDemos = null,
            Func<DateTime>? now = null, ScoutingReportLoads? loads = null, Func<string, bool>? openUri = null) =>
            new(Teams, Cache.Library(), new VetoHistoryStore(null), isBrowser: false,
                heatmaps: new SetupHeatmapService(Teams, Cache.Library(), Cache.RoundFacts(), Store, Sources.FingerprintFor),
                post: posted.Post,
                postPlant: new PostPlantService(Teams, Cache.Library(), Cache.RoundFacts(), Store, Sources.FingerprintFor),
                situational: new SituationalBehaviourService(Teams, Cache.Library(), Cache.RoundFacts()),
                notes: notes ?? new ScoutingReportNotesStore(null),
                runSection: runSection ?? (_ => _inline),
                roundIndex: Store,
                roundFacts: new RoundFactsSource(Cache),
                positionsFingerprint: Sources.FingerprintFor,
                now: now ?? (() => new DateTime(2026, 3, 18)),
                requestDemos: requestDemos,
                loads: loads,
                openUri: openUri ?? (_ => true));

        // Every place of the map at its largest area: (name, x, y, z), on the map's own floors.
        private static List<(string Place, int X, int Y, int Z)> Places(string map)
        {
            HostedMapAssets.Install();
            using IMapAssetLease? lease = MapAssets.Acquire(map);
            if (lease is not { Zones: { } zones })
            {
                return [("Spawn", 0, 0, 0)];
            }

            return
            [
                .. zones.Zones.Areas.GroupBy(a => a.PlaceId).Select(g => g.MaxBy(a => a.Area)!)
                    .Select(a => (zones.Zones.PlaceName(a.PlaceId) ?? $"place{a.PlaceId}", (int)a.CentroidX,
                        (int)a.CentroidY, (int)a.Z))
                    .OrderBy(p => p.Item1, StringComparer.Ordinal)
            ];
        }

        // Five CT spots spread over the map's floors: on a two-floor map three upstairs and two down.
        private static (string Place, int X, int Y, int Z)[] Spots(List<(string Place, int X, int Y, int Z)> places)
        {
            List<(string Place, int X, int Y, int Z)> bySite = [.. places
                .OrderByDescending(p => p.Place.Contains("Bombsite", StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Place, StringComparer.Ordinal)];
            int split = places.Count == 0 ? 0 : (int)places.Average(p => p.Z) - 1;
            List<(string, int, int, int)> up = [.. bySite.Where(p => p.Z >= split)];
            List<(string, int, int, int)> down = [.. bySite.Where(p => p.Z < split)];
            List<(string, int, int, int)> spots = [.. up.Take(down.Count >= 2 ? 3 : 5), .. down.Take(2)];
            while (spots.Count < 5)
            {
                spots.Add(bySite[spots.Count % bySite.Count]);
            }

            return [.. spots.Take(5)];
        }

        public static async Task<SyntheticTeam> Create()
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();
            RoundIndexStore store = new(cache.Data());
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            Dictionary<string, List<(string Place, int X, int Y, int Z)>> places = new(StringComparer.Ordinal);
            List<(string Path, string Map, RoundFactsRows Rows)> written = [];
            using (cache.BeginBatch())
            {
                for (int d = 0; d < _demos.Length; d++)
                {
                    (string map, int day, int opponent) = _demos[d];
                    string path = $"/d/{map}-{d}.dem";
                    Random random = new(1000 + d);
                    RoundFactsRows rows = Facts(random);
                    cache.Upsert(Record(path, map, day, opponent, rows));
                    written.Add((path, map, rows));
                }
            }

            await teams.Idle;
            foreach ((string path, string map, RoundFactsRows rows) in written)
            {
                if (!places.TryGetValue(map, out List<(string Place, int X, int Y, int Z)>? list))
                {
                    places[map] = list = Places(map);
                }

                string fingerprint = sources.FingerprintFor(map);
                Random random = new(path.GetHashCode(StringComparison.Ordinal));
                store.Write(path, RoundIndexTestData.Document(map, fingerprint),
                    Positions(path, rows, list, fingerprint, random), fingerprint, Captures(path, rows, fingerprint));
            }

            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Id(1)) == true)).Id;
            return new SyntheticTeam(cache, teams, store, sources, teamA);
        }

        private static RoundFactsRows Facts(Random random)
        {
            List<RoundFacts> rounds = [];
            int a = 0, b = 0;
            for (int n = 1; n <= Rounds; n++)
            {
                bool aCt = n <= Rounds / 2;
                int aSide = aCt ? 3 : 2;
                bool pistol = n is 1 or 9;
                BuyType buy = pistol ? BuyType.Pistol
                    : n is 2 or 10 ? (random.Next(2) == 0 ? BuyType.Eco : BuyType.Force)
                    : random.Next(6) == 0 ? BuyType.Semi : BuyType.Full;
                bool aWins = random.NextDouble() < (aCt ? 0.6 : 0.5);
                int winner = aWins ? aSide : 5 - aSide;
                bool planted = random.NextDouble() < (aCt ? 0.45 : 0.65);
                int freeze = Freeze(n);
                int contact = freeze + (14 + random.Next(20)) * Rate;
                int opener = random.Next(5);
                bool aOpens = random.NextDouble() < 0.55;
                SideFacts ours = new()
                {
                    Side = aSide, Slots = _a, PlayersAtFreezeEnd = 5, BuyType = buy, ScoreBefore = a
                };
                SideFacts theirs = new()
                {
                    Side = 5 - aSide, Slots = _b, PlayersAtFreezeEnd = 5, ScoreBefore = b,
                    BuyType = pistol ? BuyType.Pistol : BuyType.Full
                };
                rounds.Add(new RoundFacts
                {
                    Number = n,
                    IsLive = true,
                    FreezeEndTick = freeze,
                    FirstContactTick = contact,
                    OpeningKillTick = contact,
                    PlantTick = planted ? contact + (8 + random.Next(30)) * Rate : null,
                    PlantSite = planted ? (random.Next(3) == 0 ? BombSite.B : BombSite.A) : BombSite.Unknown,
                    EndTick = freeze + 95 * Rate,
                    WinnerSide = winner,
                    Ct = aCt ? ours : theirs,
                    T = aCt ? theirs : ours,
                    Kills =
                    [
                        new KillStep
                        {
                            Tick = contact,
                            AttackerSlot = aOpens ? _a[opener] : _b[opener],
                            VictimSlot = aOpens ? _b[(opener + 2) % 5] : _a[(opener + 1) % 5],
                            VictimSide = aOpens ? 5 - aSide : aSide,
                            CtAlive = 4, TAlive = 5
                        }
                    ]
                });
                if (aWins)
                {
                    a++;
                }
                else
                {
                    b++;
                }
            }

            return new RoundFactsRows
            {
                Schema = RoundFactsRecords.Schema, Clock = new RoundFactsClock { TickRate = Rate }, Rounds = rounds
            };
        }

        private static DemoCacheRecord Record(string path, string map, int day, int opponent, RoundFactsRows rows)
        {
            int aWins = rows.Rounds.Count(r => r.WinnerSide == (r.Number <= Rounds / 2 ? 3 : 2));
            DemoCacheRecord record = new()
            {
                Path = path,
                Size = 1000,
                ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
                Map = map,
                Sha256 = "sha-" + path,
                CtScore = aWins,
                TScore = Rounds - aWins,
                CtSideWins = rows.Rounds.Count(r => r.WinnerSide == 3),
                TSideWins = rows.Rounds.Count(r => r.WinnerSide == 2)
            };
            record.SetRoundFacts(rows);
            for (int slot = 0; slot < 10; slot++)
            {
                int steam = slot < 5 ? slot + 1 : opponent * 10 + slot - 4;
                record.Players.Add(new CachedPlayerInfo
                {
                    Slot = slot,
                    Name = slot < 5 ? _names[slot] : $"o{steam}",
                    SteamId64 = Id(steam),
                    Team = slot < 5 ? 3 : 2
                });
            }

            DemoCacheStore.StampParse(record);
            DemoCacheStore.StampAnalysis(record);
            return record;
        }

        // A's CT rounds hold the five spots (two of them swap on some rounds, one rotates late); the AWP is on
        // slot 1. On T the five walk from the first spot toward one of the others. The other team stands still.
        private static RoundPositionsDocument Positions(string path, RoundFactsRows rows,
            List<(string Place, int X, int Y, int Z)> places, string fingerprint, Random random)
        {
            (string Place, int X, int Y, int Z)[] spots = Spots(places);
            List<string> names = [.. places.Select(p => p.Place)];
            int PlaceOf(string place) => Math.Max(0, names.IndexOf(place));
            List<RoundPositionsRound> stored = [];
            foreach (RoundFacts round in rows.Rounds)
            {
                bool aCt = round.Number <= Rounds / 2;
                bool swap = random.Next(3) == 0;
                int target = 1 + random.Next(4);
                List<List<RoundPosition>> steps = [];
                for (int step = 0; step < 45; step++)
                {
                    List<RoundPosition> at = [];
                    foreach (int slot in _a)
                    {
                        int spot = swap && slot is 2 or 3 ? 5 - slot : slot;
                        (string place, int x, int y, int z) = aCt ? spots[spot] : spots[step < 10 ? 0 : target];
                        int jitter = 40 + slot * 12;
                        at.Add(new RoundPosition(slot, x + random.Next(-jitter, jitter), y + random.Next(-jitter, jitter), z,
                            PlaceOf(place), slot == 1 ? 1 : 0));
                    }

                    foreach (int slot in _b)
                    {
                        (string place, int x, int y, int z) = spots[(slot + 1) % 5];
                        at.Add(new RoundPosition(slot, x + 300, y - 300, z, PlaceOf(place), 2));
                    }

                    steps.Add(at);
                }

                stored.Add(new RoundPositionsRound
                {
                    Number = round.Number, FreezeEndTick = round.FreezeEndTick, Ct = aCt ? _a : _b, Pos = steps
                });
            }

            return new RoundPositionsDocument
            {
                Fingerprint = fingerprint,
                Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(path), Sha256 = "sha-" + path },
                CadenceTicks = Rate,
                Places = names,
                Weapons = ["CKnife", "CWeaponAWP", "CAK47"],
                Players =
                [
                    .. Enumerable.Range(0, 10).Select(s => new RoundPositionsPlayer(s,
                        ulong.Parse(Id(s + 1), CultureInfo.InvariantCulture), $"p{s}"))
                ],
                Rounds = stored
            };
        }

        private static RoundCapturesDocument Captures(string path, RoundFactsRows rows, string fingerprint) => new()
        {
            Fingerprint = fingerprint,
            Demo = new RoundPositionsDemo { StableKey = DemoCacheStore.StableKey(path), Sha256 = "sha-" + path },
            TickRate = Rate,
            Collected = true,
            Rounds =
            [
                .. rows.Rounds.Select(r =>
                {
                    bool aCt = r.Number <= Rounds / 2;
                    int side = aCt ? 3 : 2;
                    bool pistol = r.Number is 1 or 9;
                    return new CapturedRound
                    {
                        Number = r.Number,
                        FreezeEndTick = r.FreezeEndTick,
                        EndTick = r.EndTick ?? r.FreezeEndTick,
                        Pistol = pistol,
                        Purchases = pistol
                            ? [new CapturedPurchase(r.FreezeEndTick - 300, 0, "flashbang", 200, true),
                               new CapturedPurchase(r.FreezeEndTick - 280, 2, "p250", 300, true)]
                            : [new CapturedPurchase(r.FreezeEndTick - 200, 1, "awp", 4750, true),
                               new CapturedPurchase(r.FreezeEndTick - 190, 0, aCt ? "m4a1_silencer" : "ak47", 2900, true)],
                        Awp = pistol ? [] : [new AwpHolding(1, side, true, true, false, 500, r.FreezeEndTick + 64, 3, 1, false)],
                        Loadouts = pistol
                            ? [.. _a.Select(s => new RoundLoadout(s, side, true, s < 3 ? 100 : 0, s == 0, aCt && s < 2, 0, 800, 650))]
                            : []
                    };
                })
            ],
            Timeouts = [new RoundTimeout(Freeze(6) - 900, Freeze(6) - 200, 3, false, 3, 3, 2, 3, 6)]
        };
    }
}
