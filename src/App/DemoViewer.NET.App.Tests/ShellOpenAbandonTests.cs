#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     An open that ends without the shell holding its demo: closed while it analyses, replaced by an open that
///     then dies before taking the shell, or failed outright. Each must leave a shell that says what it holds.
/// </summary>
[NotInParallel]
public class ShellOpenAbandonTests
{
    private static ParsedDemo DemoWithFrames()
    {
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

        return SyntheticParsedDemo.Create(frames);
    }

    private static async Task InTempDir(Func<string, Task> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-open-abandon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir);
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

    [Test]
    public async Task ACloseWhileTheOpenAnalyses_LeavesTheShellClosed() =>
        await InTempDir(async dir =>
        {
            string a = Path.Combine(dir, "a.dem");
            await File.WriteAllBytesAsync(a, [1, 2, 3, 4]);
            await HeadlessSession.RunOnUi(async () =>
            {
                using HeavyJobGate gate = new();
                using DemoProcessingQueue queue = new(gate, run => run(), parseBytes: _ => DemoWithFrames(),
                    compactHeap: () => Task.CompletedTask);
                MainViewModel vm = new(library: TestLibraries.Empty(), heavyJobGate: gate, processingQueue: queue);

                Task? close = null;
                vm.Analysis.BeforeBuildForTests = () =>
                {
                    // The user presses Close Demo while Match Overview says "Analysing".
                    close ??= vm.CloseDemoCommand.ExecuteAsync(null);
                    return Task.CompletedTask;
                };

                await vm.LoadDemoFromPathAsync(a);
                await Assert.That(close).IsNotNull();
                await close!.WaitAsync(TimeSpan.FromSeconds(30));

                using (Assert.Multiple())
                {
                    await Assert.That(vm.HasFile).IsFalse();
                    await Assert.That(vm.IsLoading).IsFalse();
                    await Assert.That(vm.LoadedDemoPath).IsNull();
                    await Assert.That(vm.HexViewRaw.HasData).IsFalse()
                        .Because("the closed demo's buffer must not be pinned again by the hex view");
                    await Assert.That(vm.StatusText).IsEqualTo("No demo loaded.");
                }
            });
        });

    [Test]
    public async Task AnOpenReplacedByOneWhoseReadFails_DoesNotLeaveTheShellLoading() =>
        await InTempDir(async dir =>
        {
            string a = Path.Combine(dir, "a.dem");
            string b = Path.Combine(dir, "b.dem");
            await File.WriteAllBytesAsync(a, [1]);
            await File.WriteAllBytesAsync(b, [2]);
            await HeadlessSession.RunOnUi(async () =>
            {
                using ManualResetEventSlim parsing = new();
                using ManualResetEventSlim release = new();
                using HeavyJobGate gate = new();
                using DemoProcessingQueue queue = new(gate, run => run(), parseBytes: _ =>
                    {
                        parsing.Set();
                        release.Wait(TimeSpan.FromSeconds(30));
                        return DemoWithFrames();
                    },
                    compactHeap: () => Task.CompletedTask);
                MainViewModel vm = new(library: TestLibraries.Empty(), heavyJobGate: gate, processingQueue: queue);
                vm.ReadDemoBytes = (path, ct) => path == b
                    ? Task.FromException<byte[]>(new IOException("b is locked"))
                    : File.ReadAllBytesAsync(path, ct);

                Task openA = vm.LoadDemoFromPathAsync(a);
                await Task.Run(() => parsing.Wait(TimeSpan.FromSeconds(30)));

                // B begins (replacing A in the queue) and dies before it takes the shell.
                await vm.LoadDemoFromPathAsync(b);
                release.Set();
                await openA.WaitAsync(TimeSpan.FromSeconds(30));

                using (Assert.Multiple())
                {
                    await Assert.That(vm.IsLoading).IsFalse();
                    await Assert.That(vm.StatusText).DoesNotStartWith("Parsing a.dem");
                    await Assert.That(vm.LoadedDemoPath).IsNull();
                }
            });
        });

    [Test]
    public async Task AFailedOpen_ClearsTheDemoPathAndTellsModulesTheOldDemoIsGone() =>
        await InTempDir(async dir =>
        {
            string a = Path.Combine(dir, "a.dem");
            string b = Path.Combine(dir, "b.dem");
            await File.WriteAllBytesAsync(a, [1]);
            await File.WriteAllBytesAsync(b, [2]);
            await HeadlessSession.RunOnUi(async () =>
            {
                using HeavyJobGate gate = new();
                using DemoProcessingQueue queue = new(gate, run => run(),
                    parseBytes: bytes => bytes.Span[0] == 1
                        ? DemoWithFrames()
                        : throw new InvalidDataException("not a demo"),
                    compactHeap: () => Task.CompletedTask);
                MainViewModel vm = new(library: TestLibraries.Empty(), heavyJobGate: gate, processingQueue: queue);

                await vm.LoadDemoFromPathAsync(a);
                await Assert.That(vm.LoadedDemoPath).IsEqualTo(a);

                int resets = 0;
                vm.ModuleContext!.DemoReset += () => resets++;
                await vm.LoadDemoFromPathAsync(b);

                using (Assert.Multiple())
                {
                    await Assert.That(vm.HasFile).IsFalse();
                    await Assert.That(vm.LoadedDemoPath).IsNull()
                        .Because("the failed demo is not open, and the one before it was unloaded");
                    await Assert.That(resets).IsEqualTo(1);
                }
            });
        });
}
