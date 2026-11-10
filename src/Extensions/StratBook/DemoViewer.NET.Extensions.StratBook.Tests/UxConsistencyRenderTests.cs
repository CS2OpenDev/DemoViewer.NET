#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Automation;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.Teams;
using DemoViewer.NET.Extensions.StratBook.Views.Teams;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Renders of the approved visible changes from the UI-thread audit: the Teams tab while its
///     teams are read and while a change is applied. PNGs land in <see cref="HeadlessSession.ArtifactDir" />.
/// </summary>
[NotInParallel(TestLanes.HeadlessUi)]
[Category("Integration")]
public class UxConsistencyRenderTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map = "de_nuke")
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "p" + id[^2..], SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = "o" + id[^2..], SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    internal static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Save(Window window, string name)
    {
        if (window.CaptureRenderedFrame() is { } frame)
        {
            string path = Path.Combine(HeadlessSession.ArtifactDir, name);
            frame.Save(path, new PngBitmapEncoderOptions());
            Console.WriteLine($"[ux-render] {path}");
        }
    }

    internal static bool Shows(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    private static async Task<(DemoCacheStore Cache, TeamIdentityService Teams)> TwoTeams()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record("/d/1.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
            cache.Upsert(Record("/d/2.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_mirage"));
        }

        await teams.Idle;
        return (cache, teams);
    }

    // A demo with Round Facts for the shell render: team 1..5 is T for rounds 1 to 3 and CT for 4 to 6.
    private static DemoCacheRecord FactsRecord(string path, int day, string map)
    {
        DemoCacheRecord record = Record(path, day, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), map);
        int[] a = [0, 1, 2, 3, 4];
        int[] b = [5, 6, 7, 8, 9];
        RoundFacts Round(int n, bool aOnT, int winner, BuyType buy, int? plant)
        {
            int freeze = n * 10000;
            return new RoundFacts
            {
                Number = n,
                IsLive = true,
                FreezeEndTick = freeze,
                EndTick = freeze + 90 * 64,
                WinnerSide = winner,
                PlantTick = plant is { } s ? freeze + s * 64 : null,
                Ct = new SideFacts { Side = 3, Slots = aOnT ? b : a, PlayersAtFreezeEnd = 5, BuyType = buy },
                T = new SideFacts { Side = 2, Slots = aOnT ? a : b, PlayersAtFreezeEnd = 5, BuyType = buy }
            };
        }

        record.CtScore = 6;
        record.TScore = 13;
        record.CtSideWins = 6;
        record.TSideWins = 13;
        record.SetRoundFacts(new RoundFactsRows
        {
            Schema = RoundFactsRecords.Schema,
            Clock = new RoundFactsClock { TickRate = 64 },
            Rounds =
            [
                Round(1, true, 2, BuyType.Pistol, 30), Round(2, true, 3, BuyType.Eco, null),
                Round(3, true, 2, BuyType.Full, 52), Round(4, false, 3, BuyType.Pistol, 40),
                Round(5, false, 2, BuyType.Full, 35), Round(6, false, 3, BuyType.Full, null)
            ]
        });
        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }

    [Test]
    public async Task TheScoutingReport_RendersTheShell_HeaderPoolStageInspectorAndPlayers()
    {
        List<string> shown = [];
        string inspector = "";
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService teams = new(null, cache.Library(), new RoundFactsSource(cache), run: _inline);
            await teams.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(FactsRecord("/d/1.dem", 1, "de_nuke"));
                cache.Upsert(FactsRecord("/d/2.dem", 3, "de_nuke"));
                cache.Upsert(FactsRecord("/d/3.dem", 5, "de_nuke"));
                cache.Upsert(FactsRecord("/d/4.dem", 7, "de_mirage"));
                cache.Upsert(Record("/d/5.dem", 9, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_inferno"));
            }

            await teams.Idle;
            RoundIndexPlaceSources sources = new(() => RoundIndexTokenSource.Pawn);
            RoundIndexStore index = new(cache.Data());
            using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.ScoutingReportTabViewModel vm = new(teams,
                cache.Library(), new VetoHistoryStore(null), isBrowser: false,
                notes: new ScoutingReportNotesStore(null),
                postPlant: new PostPlantService(teams, cache.Library(), cache.RoundFacts(), index, sources.FingerprintFor),
                situational: new SituationalBehaviourService(teams, cache.Library(), cache.RoundFacts()),
                runSection: _ => _inline,
                roundFacts: new RoundFactsSource(cache),
                now: () => new DateTime(2026, 3, 12));
            Guid teamA = teams.Teams.First(t => t.Rosters.Any(r => r.CoreLineup?.Contains(Ids(1)[0]) == true)).Id;
            vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);
            vm.Side = DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers.ScoutingSide.T;
            vm.Layers.Focus(vm.Layers.Find("plants")!);
            Window window = new()
            {
                Width = 1440,
                Height = 2100,
                Content = new DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport.ScoutingReportTabView { DataContext = vm }
            };
            window.Show();
            Settle();
            Save(window, "ux-scouting-report-shell.png");
            shown = [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text ?? "")];
            inspector = vm.Layers.Inspector.Title;
            window.Close();
        });

        using (Assert.Multiple())
        {
            await Assert.That(inspector).IsEqualTo("Plants and post-plant");
            await Assert.That(shown).Contains("Map pool");
            await Assert.That(shown).Contains("Plants and post-plant");
            await Assert.That(shown.Any(t => t.StartsWith("One-pager · ", StringComparison.Ordinal))).IsTrue();
            await Assert.That(shown.Any(t => t.StartsWith("Nuke · T · all buys", StringComparison.Ordinal))).IsTrue();
            await Assert.That(shown).Contains("Long form and notes");
        }
    }

    [Test]
    public async Task AnInlineEdit_KeepsItsFocusAndText_WhenItsRowScrollsOutAndBack()
    {
        bool focusedBefore = false, focusedAfter = false;
        string text = "";
        await HeadlessSession.RunOnUi(async () =>
        {
            (DemoCacheStore cache, TeamIdentityService teams) = await TwoTeams();
            using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.ScoutingReportTabViewModel vm = new(teams,
                cache.Library(), new VetoHistoryStore(null), isBrowser: false,
                notes: new ScoutingReportNotesStore(null));
            vm.SelectedTeam = vm.Teams[0];
            for (int i = 0; i < 200; i++)
            {
                vm.Editor.NewNoteText = $"note {i}";
                vm.Editor.AddNoteCommand.Execute(null);
            }

            vm.IsNotesOpen = true;
            Window window = new()
            {
                Width = 1280,
                Height = 800,
                Content = new DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport.ScoutingReportTabView { DataContext = vm }
            };
            window.Show();
            Settle();
            DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.ScoutingReportFindingViewModel row =
                vm.Editor.Findings.First(f => f.IsNote);
            row.BeginEditCommand.Execute(null);
            Settle();

            TextBox? Editing() => window.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => ReferenceEquals(t.DataContext, row) && t.IsEffectivelyVisible);

            // The long form sits below the stage: bring its top into view so its rows are realized.
            ScrollViewer page = window.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible)
                .MaxBy(v => v.Extent.Height)!;
            Button longForm = window.GetVisualDescendants().OfType<Button>()
                .First(b => AutomationProperties.GetName(b) == "Long form and notes");
            page.Offset = new Vector(0, longForm.TranslatePoint(default, page)!.Value.Y);
            Settle();
            double editorY = page.Offset.Y + Editing()!.TranslatePoint(default, page)!.Value.Y;
            page.Offset = new Vector(0, editorY - 100);
            Settle();
            Editing()!.Focus();
            Editing()!.Text = "draft";
            Settle();
            focusedBefore = Editing()?.IsFocused == true;
            page.Offset = new Vector(0, page.Extent.Height);
            Settle();
            page.Offset = default;
            Settle();
            page.Offset = new Vector(0, editorY - 100);
            Settle();
            focusedAfter = Editing()?.IsFocused == true;
            text = row.EditText;
            Save(window, "ux-scouting-report-inline-edit.png");
            window.Close();
        });

        using (Assert.Multiple())
        {
            await Assert.That(focusedBefore).IsTrue();
            await Assert.That(focusedAfter).IsTrue()
                .Because("the focused row stays realized while it is scrolled away");
            await Assert.That(text).IsEqualTo("draft");
        }
    }

    [Test]
    public async Task TheTeamsTab_SaysItIsReading_ThenShowsAChangeInProgress()
    {
        bool reading = false, busy = false, actionsDisabled = false;
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoCacheStore cache = new(null);
            TeamIdentityService seeded = new(null, cache.Library(), run: _inline);
            await seeded.StartAsync();
            using (cache.BeginBatch())
            {
                cache.Upsert(Record("/d/1.dem", 1, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
                cache.Upsert(Record("/d/2.dem", 2, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15)));
            }

            await seeded.Idle;

            // The same library before its teams are read.
            Action? read = null;
            TeamIdentityService loading = new(null, new DemoCacheStore(null).Library(), run: _inline, scheduleLoad: r =>
            {
                read = r;
                return Task.CompletedTask;
            });
            using TeamsTabViewModel before = new(loading, cache.Library(), isBrowser: false);
            Window window = new() { Width = 1280, Height = 800, Content = new TeamsTabView { DataContext = before } };
            window.Show();
            Settle();
            reading = Shows(window, "Reading teams…");
            Save(window, "ux-teams-reading.png");
            window.Close();

            TaskCompletionSource landed = new();
            using TeamsTabViewModel vm = new(seeded, cache.Library(), isBrowser: false, command: (_, change) =>
            {
                change();
                return landed.Task;
            });
            vm.SelectedTeam = vm.Teams[0];
            vm.RenameText = "Northside";
            Window second = new() { Width = 1280, Height = 800, Content = new TeamsTabView { DataContext = vm } };
            second.Show();
            vm.RenameCommand.Execute(null);
            Settle();
            busy = Shows(second, "Renaming the team…");
            actionsDisabled = second.GetVisualDescendants().OfType<Button>()
                .Any(b => b.Content as string == "Rename" && !b.IsEffectivelyEnabled);
            Save(second, "ux-teams-busy.png");
            landed.SetResult();
            second.Close();
            read?.Invoke();
        });

        using (Assert.Multiple())
        {
            await Assert.That(reading).IsTrue();
            await Assert.That(busy).IsTrue();
            await Assert.That(actionsDisabled).IsTrue().Because("a second change waits for the first");
        }
    }
}
