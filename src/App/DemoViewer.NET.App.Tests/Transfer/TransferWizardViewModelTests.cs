#region

using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Transfer;
using DemoViewer.NET.ViewModels.Transfer;
using static DemoViewer.NET.AppTests.Transfer.TransferWizardFakes;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The export and import wizards' steps, validation and the requests they hand the services, over fakes.
///     No UI: progress posts run inline.
/// </summary>
public class TransferWizardViewModelTests
{
    private static readonly IReadOnlyList<DemoEntry> _library =
    [
        Entry("navi-faze.dem", "de_mirage", "s1mple,b1t"),
        Entry("g2-vit.dem", "de_mirage", "m0NESY,ZywOo"),
        Entry("spirit-mouz.dem", "de_nuke", "donk,torzsi")
    ];

    private static ExportWizardViewModel NewExport(FakeExporter exporter, Func<int>? inFlight = null) =>
        new(exporter, () => _library, Inline, inFlight, _ => { },
            () => new DateTime(2026, 11, 11, 15, 30, 0));

    private static ImportWizardViewModel NewImport(FakeImporter importer, IReadOnlyList<string>? roots = null,
        Func<int>? inFlight = null) =>
        new(importer, () => roots ?? [], Inline, inFlight);

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvtransferwiz_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Export_WalksDemosOptionsDestinationReview_AndPlansOnLeavingDestination()
    {
        FakeExporter exporter = new();
        ExportWizardViewModel vm = NewExport(exporter);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsDemosStep).IsTrue();
            await Assert.That(vm.StepIndicatorText).IsEqualTo("Step 1 of 6");
            await Assert.That(vm.CanGoBack).IsFalse();
            await Assert.That(vm.ExportAll).IsTrue();
            await Assert.That(vm.IncludeUserData).IsTrue().Because("annotations and user data are on by default");
            await Assert.That(vm.IncludeDemFiles).IsFalse();
            await Assert.That(vm.CanGoNext).IsTrue();
        }

        await vm.NextCommand.ExecuteAsync(null);
        await Assert.That(vm.IsOptionsStep).IsTrue();
        vm.IncludeDemFiles = true;
        await vm.NextCommand.ExecuteAsync(null);
        await Assert.That(vm.IsDestinationStep).IsTrue();
        await Assert.That(vm.CanGoNext).IsFalse().Because("there is no destination yet");

        vm.DestinationFolder = "/media/usb";
        bool canGoNext = vm.CanGoNext;
        string preview = vm.BundleFolderPreview;
        int plansBeforeLeaving = exporter.Requests.Count;
        await vm.NextCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(canGoNext).IsTrue();
            await Assert.That(preview).IsEqualTo(Path.Combine("/media/usb", "DemoViewer Transfer 2026-11-11 1530"));
            await Assert.That(plansBeforeLeaving).IsEqualTo(0);
            await Assert.That(vm.IsReviewStep).IsTrue();
            await Assert.That(vm.ShowStart).IsTrue();
            await Assert.That(vm.ShowNext).IsFalse();
            await Assert.That(vm.CanStart).IsTrue();
            await Assert.That(exporter.Requests).Count().IsEqualTo(1);
            await Assert.That(exporter.Requests[0].AllDemos).IsTrue();
            await Assert.That(exporter.Requests[0].IncludeDemFiles).IsTrue();
            await Assert.That(exporter.Requests[0].IncludeUserData).IsTrue();
            await Assert.That(exporter.Requests[0].DestinationFolder).IsEqualTo("/media/usb");
            await Assert.That(vm.ReviewHeadline).IsEqualTo("1 demo with analysis data, 1 .dem file.");
        }

        vm.BackCommand.Execute(null);
        await Assert.That(vm.IsDestinationStep).IsTrue();
    }

    [Test]
    public async Task Export_Choose_NeedsAtLeastOneTickedDemo_AndSendsTheirPaths()
    {
        FakeExporter exporter = new();
        ExportWizardViewModel vm = NewExport(exporter);

        vm.ChooseDemos = true;
        bool blockedWithNone = !vm.CanGoNext;
        vm.Rows[2].IsChecked = true;
        vm.Rows[0].IsChecked = true;

        using (Assert.Multiple())
        {
            await Assert.That(vm.ExportAll).IsFalse();
            await Assert.That(blockedWithNone).IsTrue();
            await Assert.That(vm.CanGoNext).IsTrue();
            await Assert.That(vm.SelectedCount).IsEqualTo(2);
            await Assert.That(vm.SelectionSummary).IsEqualTo("2 of 3 demos selected");
        }

        vm.IncludeUserData = false;
        vm.DestinationFolder = " /media/usb ";
        ExportRequest request = vm.BuildRequest();

        using (Assert.Multiple())
        {
            await Assert.That(request.AllDemos).IsFalse();
            await Assert.That(request.DemoKeys).IsEquivalentTo(["/demos/navi-faze.dem", "/demos/spirit-mouz.dem"]);
            await Assert.That(request.IncludeUserData).IsFalse();
            await Assert.That(request.DestinationFolder).IsEqualTo("/media/usb");
        }
    }

    [Test]
    public async Task Export_SearchAndMapChips_FilterTheList_AndSelectAllTicksOnlyWhatIsShown()
    {
        ExportWizardViewModel vm = NewExport(new FakeExporter());
        vm.ChooseDemos = true;

        await Assert.That(vm.MapFilters.Select(m => m.Display)).IsEquivalentTo(["Mirage", "Nuke"]);

        vm.MapFilters.Single(m => m.Display == "Mirage").IsSelected = true;
        int mirageRows = vm.FilteredRows.Count;
        vm.SearchText = "zywoo";
        string onlyRow = vm.FilteredRows.Single().DisplayName;
        vm.SelectAllFilteredCommand.Execute(null);
        int ticked = vm.SelectedCount;

        vm.ClearMapFiltersCommand.Execute(null);
        vm.SearchText = "";
        int allRows = vm.FilteredRows.Count;
        vm.ClearSelectionCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(mirageRows).IsEqualTo(2);
            await Assert.That(onlyRow).IsEqualTo("g2-vit.dem");
            await Assert.That(ticked).IsEqualTo(1);
            await Assert.That(allRows).IsEqualTo(3);
            await Assert.That(vm.SelectedCount).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Export_Review_ListsNotHashedDemos_AndWarnsAboutDemosInFlight()
    {
        FakeExporter exporter = new()
        {
            PlanFor = r => FakeExporter.PlanOf(r,
            [
                new ExportPlanEntry(Sha('a'), "/demos/a.dem", "a.dem", "de_dust2", ExportEntryStatus.Included, 10, 0),
                new ExportPlanEntry(Sha('b'), "/demos/b.dem", "b.dem", "de_dust2", ExportEntryStatus.InFlight, 10, 0),
                new ExportPlanEntry(null, "/demos/c.dem", "c.dem", null, ExportEntryStatus.NotHashed, 0, 0),
                new ExportPlanEntry(null, "/demos/d.dem", "d.dem", null, ExportEntryStatus.Missing, 0, 0)
            ])
        };
        ExportWizardViewModel vm = NewExport(exporter);
        vm.CurrentStep = 2;
        vm.DestinationFolder = "/media/usb";
        await vm.NextCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IncludedCount).IsEqualTo(2).Because("an in-flight demo is still exported, best effort");
            await Assert.That(vm.InFlightCount).IsEqualTo(1);
            await Assert.That(vm.NotHashedCount).IsEqualTo(1);
            await Assert.That(vm.NotHashedNames).IsEquivalentTo(["c.dem"]);
            await Assert.That(vm.HasMissing).IsTrue();
            await Assert.That(vm.ShowInFlightWarning).IsTrue();
            await Assert.That(TransferWizardViewModel.InFlightWarningText).Contains("MAY not be included");
            await Assert.That(vm.CanStart).IsTrue();
        }
    }

    [Test]
    public async Task Export_Review_WarnsWhenTheQueueIsBusy_EvenIfThePlanFoundNothingInFlight()
    {
        int queued = 0;
        ExportWizardViewModel vm = NewExport(new FakeExporter(), () => queued);
        vm.CurrentStep = 2;
        vm.DestinationFolder = "/media/usb";
        await vm.NextCommand.ExecuteAsync(null);
        bool quiet = vm.ShowInFlightWarning;

        queued = 3;
        vm.BackCommand.Execute(null);
        await vm.NextCommand.ExecuteAsync(null);

        await Assert.That(quiet).IsFalse();
        await Assert.That(vm.ShowInFlightWarning).IsTrue();
    }

    [Test]
    public async Task Export_ABlockingTargetFinding_DisablesExport_AndIsShown()
    {
        FakeExporter exporter = new()
        {
            PlanFor = r => FakeExporter.PlanOf(r,
                [new ExportPlanEntry(Sha('a'), "/demos/a.dem", "a.dem", null, ExportEntryStatus.Included, 10, 0)],
                new TransferWarning(TransferWarningKind.SpaceUnknown, "Free space unknown."),
                new TransferWarning(TransferWarningKind.InsufficientSpace, "Not enough space.", Blocking: true))
        };
        ExportWizardViewModel vm = NewExport(exporter);
        vm.CurrentStep = 2;
        vm.DestinationFolder = "/media/usb";
        await vm.NextCommand.ExecuteAsync(null);
        await vm.StartCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsReviewStep).IsTrue();
            await Assert.That(vm.CanStart).IsFalse();
            await Assert.That(vm.PlanWarnings[0].Message).IsEqualTo("Not enough space.");
            await Assert.That(exporter.Runs).IsEmpty();
        }
    }

    [Test]
    public async Task Export_APlanThatThrows_StaysOnDestination_WithTheReason()
    {
        FakeExporter exporter = new() { PlanThrows = new IOException("drive went away") };
        ExportWizardViewModel vm = NewExport(exporter);
        vm.CurrentStep = 2;
        vm.DestinationFolder = "/media/usb";
        await vm.NextCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(vm.IsDestinationStep).IsTrue();
            await Assert.That(vm.ErrorText).Contains("drive went away");
            await Assert.That(vm.IsPlanning).IsFalse();
        }
    }

    [Test]
    public async Task Export_Start_RunsThePlan_ShowsProgress_ThenTheSummary()
    {
        FakeExporter exporter = new()
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReportBeforeGate = new TransferProgress(1, 4, 50, 100, "a.dem")
        };
        ExportWizardViewModel vm = NewExport(exporter);
        vm.CurrentStep = 2;
        vm.DestinationFolder = "/media/usb";
        await vm.NextCommand.ExecuteAsync(null);

        Task run = vm.StartCommand.ExecuteAsync(null);
        await WaitFor(() => vm.CurrentDemo is not null);
        bool runningOnProgress = vm.IsProgressStep && vm.IsRunning && !vm.CanGoBack && !vm.ShowClose;
        double fraction = vm.ProgressFraction;
        string progressText = vm.ProgressText;

        exporter.Gate.SetResult();
        await run;

        using (Assert.Multiple())
        {
            await Assert.That(runningOnProgress).IsTrue();
            await Assert.That(fraction).IsEqualTo(0.5);
            await Assert.That(progressText).StartsWith("1 of 4 demos");
            await Assert.That(vm.IsDoneStep).IsTrue();
            await Assert.That(vm.IsRunning).IsFalse();
            await Assert.That(vm.CanGoBack).IsFalse();
            await Assert.That(vm.SummaryHeadline).IsEqualTo("1 demo exported.");
            await Assert.That(exporter.Runs).Count().IsEqualTo(1);
        }
    }

    [Test]
    public async Task Export_ClosingMidRun_AsksFirst_ThenCancelsAndCloses()
    {
        FakeExporter exporter = new() { Gate = new TaskCompletionSource() };
        ExportWizardViewModel vm = NewExport(exporter);
        int closes = 0;
        vm.CloseRequested += (_, _) => closes++;
        vm.CurrentStep = 2;
        vm.DestinationFolder = "/media/usb";
        await vm.NextCommand.ExecuteAsync(null);

        Task run = vm.StartCommand.ExecuteAsync(null);
        await WaitFor(() => exporter.Runs.Count == 1);

        bool mayClose = vm.TryClose();
        bool prompted = vm.IsCancelPromptOpen;
        vm.DismissCancelPromptCommand.Execute(null);
        bool stillRunning = vm.IsRunning && !vm.IsCancelPromptOpen;

        vm.CloseCommand.Execute(null);
        vm.ConfirmCancelAndCloseCommand.Execute(null);
        await run;

        using (Assert.Multiple())
        {
            await Assert.That(mayClose).IsFalse();
            await Assert.That(prompted).IsTrue();
            await Assert.That(stillRunning).IsTrue();
            await Assert.That(closes).IsEqualTo(1);
            await Assert.That(vm.Summary!.Cancelled).IsTrue();
            await Assert.That(vm.TryClose()).IsTrue();
        }
    }

    [Test]
    public async Task Export_ClosingWhenIdle_ClosesStraightAway()
    {
        ExportWizardViewModel vm = NewExport(new FakeExporter());
        int closes = 0;
        vm.CloseRequested += (_, _) => closes++;
        vm.CloseCommand.Execute(null);

        await Assert.That(closes).IsEqualTo(1);
        await Assert.That(vm.IsCancelPromptOpen).IsFalse();
    }

    // ── Import ───────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Import_ABundleTheImporterRefuses_IsReportedOnTheBundleStep()
    {
        string dir = NewTempDir();
        try
        {
            FakeImporter importer = new()
            {
                Refusal = "This export was made by DemoViewer.NET 0.10.0. Import it with that version."
            };
            ImportWizardViewModel vm = NewImport(importer);
            await vm.LoadBundleAsync(dir);

            using (Assert.Multiple())
            {
                await Assert.That(vm.IsBundleStep).IsTrue();
                await Assert.That(vm.BundleError).IsEqualTo(importer.Refusal);
                await Assert.That(vm.HasPreview).IsFalse();
                await Assert.That(vm.CanGoNext).IsFalse();
            }

            await vm.LoadBundleAsync(Path.Combine(dir, "nope"));
            await Assert.That(vm.BundleError).IsEqualTo("That folder does not exist.");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Import_WithoutDemFiles_HasFourSteps_AndKeepMineIsTheDefault()
    {
        string dir = NewTempDir();
        try
        {
            FakeImporter importer = new()
            {
                PreviewFor = f => Preview(f,
                [
                    Entry(Demo('a', "a.dem", "de_dust2", false)),
                    Entry(Demo('b', "b.dem", "de_nuke", false), ImportEntryStatus.UserDataConflict, stale: true)
                ])
            };
            ImportWizardViewModel vm = NewImport(importer);
            vm.BundleFolder = dir;
            await vm.CheckBundleCommand.ExecuteAsync(null);
            bool canLeaveBundle = vm.CanGoNext;
            vm.NextCommand.Execute(null);

            using (Assert.Multiple())
            {
                await Assert.That(canLeaveBundle).IsTrue();
                await Assert.That(vm.IsPreviewStep).IsTrue();
                await Assert.That(vm.StepIndicatorText).IsEqualTo("Step 2 of 4");
                await Assert.That(vm.HasDemFilesStep).IsFalse();
                await Assert.That(vm.ShowStart).IsTrue();
                await Assert.That(vm.ShowNext).IsFalse();
                await Assert.That(vm.SelectedPolicy.Value).IsEqualTo(ConflictPolicy.KeepMine);
                await Assert.That(vm.Policies.Select(p => p.Value))
                    .IsEquivalentTo([ConflictPolicy.KeepMine, ConflictPolicy.TakeImported, ConflictPolicy.KeepBoth]);
                await Assert.That(vm.HasConflicts).IsTrue();
                await Assert.That(vm.ShowStaleBanner).IsTrue();
                await Assert.That(vm.PreviewRows.Select(r => r.Status ?? ""))
                    .IsEquivalentTo(["New", "Your notes differ · stale"]);
            }

            await vm.StartCommand.ExecuteAsync(null);

            using (Assert.Multiple())
            {
                await Assert.That(vm.IsDoneStep).IsTrue();
                await Assert.That(importer.Runs).Count().IsEqualTo(1);
                await Assert.That(importer.Runs[0].Policy).IsEqualTo(ConflictPolicy.KeepMine);
                await Assert.That(importer.Runs[0].DemFilesToCopy).IsEmpty();
                await Assert.That(importer.Runs[0].DemDestination).IsNull();
                await Assert.That(vm.SummaryHeadline).IsEqualTo("2 demos imported.");
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Import_WithDemFiles_AddsTheDemoFilesStep_AndNeedsADestination()
    {
        string dir = NewTempDir();
        try
        {
            FakeImporter importer = new()
            {
                PreviewFor = f => Preview(f,
                [
                    Entry(Demo('a', "a.dem", "de_dust2", true)),
                    Entry(Demo('b', "b.dem", "de_nuke", true), demHere: true),
                    Entry(Demo('c', "c.dem", "de_nuke", true))
                ])
            };
            ImportWizardViewModel vm = NewImport(importer);
            await vm.LoadBundleAsync(dir);
            vm.NextCommand.Execute(null);
            bool previewHasNext = vm.ShowNext && !vm.ShowStart;
            vm.SelectedPolicy = vm.Policies[1];
            vm.NextCommand.Execute(null);

            using (Assert.Multiple())
            {
                await Assert.That(previewHasNext).IsTrue();
                await Assert.That(vm.IsDemFilesStep).IsTrue();
                await Assert.That(vm.StepIndicatorText).IsEqualTo("Step 3 of 5");
                await Assert.That(vm.DemRows.Select(r => r.Key)).IsEquivalentTo([Sha('a'), Sha('c')])
                    .Because("a .dem the library already has is not offered");
                await Assert.That(vm.DemRows.All(r => r.IsChecked)).IsTrue();
                await Assert.That(vm.UseNewFolder).IsTrue().Because("the library has no folders");
                await Assert.That(vm.CanStart).IsFalse().Because("the new folder is not named yet");
            }

            vm.DemRows[0].IsChecked = false;
            vm.NewDemFolder = "/home/me/demos/imported";
            await vm.StartCommand.ExecuteAsync(null);

            ImportRequest request = importer.Runs.Single();
            using (Assert.Multiple())
            {
                await Assert.That(request.Policy).IsEqualTo(ConflictPolicy.TakeImported);
                await Assert.That(request.DemFilesToCopy).IsEquivalentTo([Sha('c')]);
                await Assert.That(request.DemDestination).IsEqualTo("/home/me/demos/imported");
                await Assert.That(request.DemDestinationIsNewFolder).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Import_DemFilesIntoALibraryFolder_AndNoneTicked_IsDataOnly()
    {
        string dir = NewTempDir();
        try
        {
            FakeImporter importer = new()
            {
                PreviewFor = f => Preview(f, [Entry(Demo('a', "a.dem", "de_dust2", true))])
            };
            ImportWizardViewModel vm = NewImport(importer, ["/lib/one", "/lib/two"]);
            await vm.LoadBundleAsync(dir);
            vm.CurrentStep = 2;

            bool usesLibrary = vm.UseLibraryRoot && vm.SelectedLibraryRoot == "/lib/one";
            vm.SelectedLibraryRoot = "/lib/two";
            ImportRequest toLibrary = vm.BuildRequest();
            vm.ClearDemsCommand.Execute(null);
            ImportRequest none = vm.BuildRequest();

            using (Assert.Multiple())
            {
                await Assert.That(usesLibrary).IsTrue();
                await Assert.That(toLibrary.DemDestination).IsEqualTo("/lib/two");
                await Assert.That(toLibrary.DemDestinationIsNewFolder).IsFalse();
                await Assert.That(none.DemFilesToCopy).IsEmpty();
                await Assert.That(none.DemDestination).IsNull();
                await Assert.That(vm.CanStart).IsTrue();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Import_Preview_WarnsAboutDemosInFlight()
    {
        string dir = NewTempDir();
        try
        {
            FakeImporter importer = new();
            ImportWizardViewModel vm = NewImport(importer, inFlight: () => 2);
            await vm.LoadBundleAsync(dir);
            bool onBundle = vm.ShowInFlightWarning;
            vm.NextCommand.Execute(null);

            await Assert.That(onBundle).IsFalse();
            await Assert.That(vm.ShowInFlightWarning).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Import_ChangingTheFolder_DropsThePreview()
    {
        string dir = NewTempDir();
        try
        {
            ImportWizardViewModel vm = NewImport(new FakeImporter());
            await vm.LoadBundleAsync(dir);
            bool loaded = vm.CanGoNext;
            vm.BundleFolder = dir + "-other";

            await Assert.That(loaded).IsTrue();
            await Assert.That(vm.HasPreview).IsFalse();
            await Assert.That(vm.CanGoNext).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task Import_ChangingTheFolderDuringALoad_DiscardsThatLoad()
    {
        string dir = NewTempDir();
        try
        {
            FakeImporter importer = new() { PreviewGate = new TaskCompletionSource() };
            ImportWizardViewModel vm = NewImport(importer);
            vm.BundleFolder = dir;
            Task load = vm.LoadBundleAsync(dir);
            await WaitFor(() => vm.IsLoadingBundle);
            vm.BundleFolder = dir + "-other";
            importer.PreviewGate.SetResult();
            await load;

            using (Assert.Multiple())
            {
                await Assert.That(vm.HasPreview).IsFalse().Because("the loaded bundle is no longer the one named");
                await Assert.That(vm.CanGoNext).IsFalse();
                await Assert.That(vm.IsLoadingBundle).IsFalse();
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new TimeoutException("condition never held");
        }
    }
}
