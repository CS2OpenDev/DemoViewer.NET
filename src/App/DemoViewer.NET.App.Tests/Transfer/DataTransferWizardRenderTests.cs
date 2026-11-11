#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Transfer;
using DemoViewer.NET.ViewModels.Transfer;
using DemoViewer.NET.Views.Transfer;
using static DemoViewer.NET.AppTests.Transfer.TransferWizardFakes;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The wizard window, opened through the real <see cref="DesktopWindowService" /> and walked end to end over
///     fake services: each step's panel is the one on screen, a second open reuses the window, and closing
///     mid-run is held for the cancel prompt.
/// </summary>
[NotInParallel]
[Category("Render")]
public class DataTransferWizardRenderTests
{
    private static readonly List<DataTransferWizardWindow> _opened = [];
    private static bool _trackingInstalled;

    private static void TrackWizardWindows()
    {
        _opened.Clear();
        if (_trackingInstalled)
        {
            return;
        }

        _trackingInstalled = true;
        Window.WindowOpenedEvent.AddClassHandler(typeof(DataTransferWizardWindow),
            (sender, _) => _opened.Add((DataTransferWizardWindow)sender!));
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool Shows(Window window, string text) => window.GetVisualDescendants().OfType<TextBlock>()
        .Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            Pump();
            await Task.Delay(10);
        }

        Pump();
    }

    [Test]
    public async Task TheExportWizard_WalksEveryStep_InOneReusedWindow()
    {
        string dest = Path.Combine(Path.GetTempPath(), "dvtransferrender_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                TrackWizardWindows();
                Window owner = new() { Width = 400, Height = 300 };
                owner.Show();
                DesktopWindowService service = new(() => owner);
                FakeExporter exporter = new();
                ExportWizardViewModel vm = new(exporter, () => [Entry("a.dem", "de_dust2")]);

                service.ShowExportWizard(() => vm);
                service.ShowExportWizard(() => throw new InvalidOperationException("a second wizard was built"));
                Pump();
                DataTransferWizardWindow window = _opened.Single();

                bool view = window.GetVisualDescendants().OfType<ExportWizardView>().Any();
                bool owned = ReferenceEquals(window.Owner, owner);
                bool demos = Shows(window, "Which demos?");
                string title = window.Title ?? "";
                await vm.NextCommand.ExecuteAsync(null);
                Pump();
                bool options = Shows(window, "What to include") && !Shows(window, "Which demos?");
                await vm.NextCommand.ExecuteAsync(null);
                vm.DestinationFolder = dest;
                Pump();
                bool destination = Shows(window, "Where to save it");
                await vm.NextCommand.ExecuteAsync(null);
                Pump();
                bool review = Shows(window, "Review") && Shows(window, "1 demo with analysis data.");
                await vm.StartCommand.ExecuteAsync(null);
                await Until(() => vm.IsDoneStep);
                bool done = Shows(window, "Done") && Shows(window, "1 demo exported.");

                vm.CloseCommand.Execute(null);
                Pump();

                using (Assert.Multiple())
                {
                    await Assert.That(view).IsTrue();
                    await Assert.That(title).IsEqualTo("Export library data");
                    await Assert.That(owned).IsTrue();
                    await Assert.That(demos).IsTrue();
                    await Assert.That(options).IsTrue();
                    await Assert.That(destination).IsTrue();
                    await Assert.That(review).IsTrue();
                    await Assert.That(done).IsTrue();
                    await Assert.That(window.IsVisible).IsFalse();
                }

                owner.Close();
            });
        }
        finally
        {
            Directory.Delete(dest, true);
        }
    }

    [Test]
    public async Task TheImportWizard_WalksBundlePreviewDemoFilesAndDone()
    {
        string bundle = Path.Combine(Path.GetTempPath(), "dvtransferrender_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                TrackWizardWindows();
                Window owner = new() { Width = 400, Height = 300 };
                owner.Show();
                DesktopWindowService service = new(() => owner);
                FakeImporter importer = new()
                {
                    PreviewFor = f => Preview(f,
                        [Entry(Demo('a', "a.dem", "de_dust2", true), ImportEntryStatus.New, stale: true)])
                };
                ImportWizardViewModel vm = new(importer, () => ["/lib"]);

                service.ShowImportWizard(() => vm);
                Pump();
                DataTransferWizardWindow window = _opened.Single();

                bool view = window.GetVisualDescendants().OfType<ImportWizardView>().Any();
                bool bundleStep = Shows(window, "Choose the export");
                await vm.LoadBundleAsync(bundle);
                vm.NextCommand.Execute(null);
                Pump();
                bool preview = Shows(window, "What will be imported") && Shows(window, vm.StaleBannerText);
                vm.NextCommand.Execute(null);
                Pump();
                bool demFiles = Shows(window, "Demo files");
                await vm.StartCommand.ExecuteAsync(null);
                await Until(() => vm.IsDoneStep);
                bool done = Shows(window, "1 demo imported.");

                window.Close();
                Pump();

                using (Assert.Multiple())
                {
                    await Assert.That(view).IsTrue();
                    await Assert.That(bundleStep).IsTrue();
                    await Assert.That(preview).IsTrue();
                    await Assert.That(demFiles).IsTrue();
                    await Assert.That(done).IsTrue();
                    await Assert.That(importer.Runs.Single().DemDestination).IsEqualTo("/lib");
                    await Assert.That(window.IsVisible).IsFalse();
                }

                owner.Close();
            });
        }
        finally
        {
            Directory.Delete(bundle, true);
        }
    }

    [Test]
    public async Task ClosingTheWindowMidRun_IsHeldForTheCancelPrompt()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            TrackWizardWindows();
            DesktopWindowService service = new(() => null);
            FakeExporter exporter = new() { Gate = new TaskCompletionSource() };
            ExportWizardViewModel vm = new(exporter, () => [Entry("a.dem", "de_dust2")]);
            service.ShowExportWizard(() => vm);
            Pump();
            DataTransferWizardWindow window = _opened.Single();

            vm.CurrentStep = 2;
            vm.DestinationFolder = Path.GetTempPath();
            await vm.NextCommand.ExecuteAsync(null);
            Task run = vm.StartCommand.ExecuteAsync(null);
            await Until(() => exporter.Runs.Count == 1);

            window.Close();
            Pump();
            bool held = window.IsVisible;
            bool prompt = Shows(window, "The export is still running. Cancel it and close?");

            vm.ConfirmCancelAndCloseCommand.Execute(null);
            await run;
            Pump();

            using (Assert.Multiple())
            {
                await Assert.That(held).IsTrue();
                await Assert.That(prompt).IsTrue();
                await Assert.That(vm.Summary?.Cancelled == true).IsTrue();
                await Assert.That(window.IsVisible).IsFalse();
            }
        });
    }
}
