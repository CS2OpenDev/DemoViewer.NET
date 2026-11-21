#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Models;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The persisted Parser frame comes back on the first demo load after a relaunch. It used to be applied the
///     moment HasFile flipped, before the load had handed its frame list to the shell, so it resolved to no frame,
///     and the analysis landing seek then moved the selection anyway.
/// </summary>
[NotInParallel]
public class ParserSessionRestoreTests
{
    [Test]
    public async Task TheSavedParserFrame_IsSelectedAfterTheFirstLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvparserrestore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string demo = Path.Combine(dir, "a.dem");
        await File.WriteAllBytesAsync(demo, [1, 2, 3]);
        try
        {
            await HeadlessSession.RunOnUi(async () =>
            {
                SettingsService svc = new(dir);
                svc.SaveSession(new SessionPayload(new TabSessionState(5, null, true), null, null, false, false));

                List<DemoFrame> frames = new(10);
                for (int i = 0; i < 10; i++)
                {
                    frames.Add(new DemoFrame
                    {
                        CommandKind = EDemoCommands.DemPacket,
                        FrameNumber = i,
                        ServerTick = i,
                        HeaderLength = 0,
                        RawLength = 0,
                        RawStart = 0,
                        IsCompressed = false
                    });
                }

                using HeavyJobGate gate = new();
                using DemoProcessingQueue queue = new(gate, run => run(),
                    parseBytes: _ => SyntheticParsedDemo.Create(frames), compactHeap: () => Task.CompletedTask);
                MainViewModel vm = new(library: TestLibraries.Empty(), settingsService: svc, heavyJobGate: gate,
                    processingQueue: queue);
                try
                {
                    vm.RestoreSession();
                    await vm.LoadDemoFromPathAsync(demo);

                    using (Assert.Multiple())
                    {
                        await Assert.That(vm.HasFile).IsTrue();
                        await Assert.That(vm.Playback.CurrentFrameIndex).IsEqualTo(5);
                        await Assert.That(vm.SelectedFrame).IsSameReferenceAs(vm.Frames[5]);
                    }
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
