#region

using System.Collections.Concurrent;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Sdk.Export;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Export;
using DemoViewer.NET.ViewModels.Playback2D;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The dialog a first-party extension opens over its own scene: its own choices, the 2D export's saved folder
///     and quality, and only the folder written back when an export starts.
/// </summary>
public class FirstPartySceneExportTests
{
    private static readonly SceneExportDefaults _gif = new(ExportFormats.Gif, 20, 640, 640, true, true, true, false);

    [Test]
    public async Task TheDialog_TakesTheExtensionsChoices_AndTheSavedFolder_AndSavesOnlyTheFolderOnStart()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-scene-export-" + Guid.NewGuid().ToString("N"));
        string saved = Path.Combine(root, "saved");
        string picked = Path.Combine(root, "picked");
        Directory.CreateDirectory(saved);
        Directory.CreateDirectory(picked);
        try
        {
            SettingsService settings = new(root);
            settings.Write(s =>
            {
                s.Playback2D.ExportOutputDirectory = saved;
                s.Playback2D.ExportFps = 60;
                s.Playback2D.ExportFormatId = ExportFormats.WebM;
            });

            using HeavyJobGate gate = new();
            FirstPartyExports exports = new(gate, null!, settings);
            RecordingJob job = new();
            Playback2DExportDialogViewModel dialog = FirstPartySceneExport.NewDialog(exports, _gif,
                [new ExportRangeOption("all", 0, 99)], job, static (start, end, _, _) => end - start + 1, null,
                static () => null,
                new ExportDialogScene("Export scene", "scene", FirstPartySceneExport.SizePresets, null));

            using (Assert.Multiple())
            {
                await Assert.That(dialog.SelectedFormat).IsEqualTo(ExportFormats.Gif);
                await Assert.That(Path.GetDirectoryName(dialog.OutputPath)).IsEqualTo(saved);
            }

            dialog.OutputPath = Path.Combine(picked, "scene.gif");
            dialog.StartCommand.Execute(null);

            using (Assert.Multiple())
            {
                await Assert.That(job.Started).IsEqualTo(1);
                await Assert.That(settings.Current.Playback2D.ExportOutputDirectory).IsEqualTo(picked);
                await Assert.That(settings.Current.Playback2D.ExportFps).IsEqualTo(60);
                await Assert.That(settings.Current.Playback2D.ExportFormatId).IsEqualTo(ExportFormats.WebM);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     One export serves every pane an extension opens, and a run reads its log sink and its refusal copy
    ///     while it runs. Both are the pane's that started the run, whichever pane was opened last.
    /// </summary>
    [Test]
    public async Task ARun_LogsToThePaneThatStartedIt_AndRefusesInThatPanesWords() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "dv-scene-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using HeavyJobGate gate = new();
                using ExtensionSceneExport export = new("dev.example.export", "pack.fake",
                    new FirstPartyExports(gate, null!), null, null, new FaultRig().Guard, null);
                ConcurrentQueue<string> first = [];
                ConcurrentQueue<string> second = [];
                using ISceneExportPane started = export.OpenPane(PaneRequest("first", first.Enqueue));
                using ISceneExportPane opened = export.OpenPane(PaneRequest("second", second.Enqueue));

                Playback2DExportDialogViewModel dialog = (Playback2DExportDialogViewModel)started.ViewModel;
                dialog.OutputPath = Path.Combine(root, "scene.gif");
                dialog.StartCommand.Execute(null);

                // The capture is null, so the run is refused as soon as it is set up, on the pool. The refusal
                // reaches the log while the job is still running, and the banner says so until it ends.
                for (int i = 0; i < 400 && ((first.IsEmpty && second.IsEmpty) || dialog.ErrorBanner is not null); i++)
                {
                    await Task.Delay(25);
                }

                using (Assert.Multiple())
                {
                    await Assert.That(dialog.ErrorBanner).IsNull();
                    await Assert.That(second).IsEmpty().Because("the pane opened last started nothing");
                    await Assert.That(first.Any(line => line.Contains("nothing in first", StringComparison.Ordinal)))
                        .IsTrue().Because("the run is the first pane's, so the refusal is its copy in its log");
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        });

    private static SceneExportPaneRequest PaneRequest(string name, Action<string> log) =>
        new()
        {
            Title = "Export " + name,
            FileStem = name,
            Ranges = [new SceneExportRange("all", 0, 99)],
            Choices = new SceneExportChoices(SceneExportFormats.Gif, 20, 640, 640, true, true, true, false),
            OutputFrameCount = static (start, end, _, _) => end - start + 1,
            Capture = static () => null,
            NothingToExport = "nothing in " + name,
            Log = log
        };

    private sealed class RecordingJob : IExportJobService
    {
        public int Started { get; private set; }

        public ExportJobStatus Status => ExportJobStatus.Idle;

        public event EventHandler<ExportJobStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public void Start(Scene2DExportRequest request) => Started++;

        public Task CancelAsync() => Task.CompletedTask;
    }
}
