#region

using System.Diagnostics;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using DemoViewer.NET.Playback2D.Pipeline.Export;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using DemoViewer.NET.Playback2D.Pipeline.Headless;
using DemoViewer.NET.Playback2D.Pipeline.Rendering;
using SkiaSharp;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     What an export does when the thing on the other end of the pipe dies.
///     <para>
///         These are the cases the review found unpinned: every existing failure test uses a sink that
///         throws <i>synchronously from <c>WriteAsync</c></i>, which is the one shape a real encoder
///         never has. A real one fails <b>out of band</b> (its process exits while the render loop
///         is still handing it frames) and it fails <b>on disposal</b>, when the container is
///         finalised.
///     </para>
/// </summary>
public class ExportFailureTests
{
    /// <summary>
    ///     An ffmpeg that cannot open its output must fail the export, not hang it.
    ///     <para>
    ///         Risk R2's exact failure mode. ffmpeg exits within milliseconds of a bad output path; the
    ///         bounded channel then fills, and nothing will ever drain it again. Before the fix the
    ///         write loop blocked forever: <c>dv2d export</c> to a non-existent directory never
    ///         returned, and the 30 s disposal timeout never got a chance to run because disposal was
    ///         never reached.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task AnFfmpegThatCannotOpenItsOutput_FailsTheWrites_RatherThanBlockingForever()
    {
        FfmpegLocation located = FfmpegLocator.Locate(null);
        if (!located.Found)
        {
            throw new SkipTestException("no ffmpeg on PATH; the subprocess sink cannot be exercised");
        }

        // An output directory that is gone by the time ffmpeg opens the file, so it fails at
        // output-open and exits immediately.
        //
        // The sink's constructor creates the directory (ExportOutputPath, a missing parent is the one
        // export failure worth pre-empting, since ffmpeg's refusal would otherwise arrive only after the
        // whole range had been rendered). That is a courtesy at CONSTRUCTION, not a guarantee at WRITE,
        // and R2's deadlock is a property of the write loop: whatever kills ffmpeg early, a full channel
        // with no reader must still fault its writer rather than park it forever. Removing the directory
        // between the two reproduces that with ffmpeg's original "No such file or directory".
        string directory = Path.Combine(Path.GetTempPath(), $"dv-nodir-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "out.webm");
        FfmpegFrameSink sink = new(new FfmpegSinkOptions(path, ExportFormats.WebM, 64, 64, 30,
            located.Directory));
        Directory.Delete(directory);

        byte[] frame = new byte[64 * 64 * 4];

        // Far more frames than the channel can hold, so the writer must reach the full-channel wait.
        Task loop = Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                await sink.WriteAsync(frame, 64, 64, CancellationToken.None);
            }
        });

        Task finished = await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(30)));
        bool endedOnItsOwn = ReferenceEquals(finished, loop);

        if (endedOnItsOwn)
        {
            // Disposal re-raises the encoder's failure, which is what turns this into a Failed export
            // rather than a silent one, so it is expected to throw here too.
            try
            {
                await sink.DisposeAsync();
            }
            catch (Exception)
            {
                // Asserted through `loop` instead; see below.
            }
        }

        await Assert.That(endedOnItsOwn).IsTrue();
        await Assert.That(loop.IsFaulted).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();

        // And the failure must say what ffmpeg said. The pipe breaks before FFMpegCore observes the
        // process exit, so the raw fault is "Pipe is broken". That is true, and no help to anybody.
        Exception surfaced = loop.Exception!.GetBaseException();
        await Assert.That(surfaced).IsTypeOf<FfmpegEncodeException>();
        await Assert.That(surfaced.Message).Contains("Error opening output");
    }

    /// <summary>
    ///     An ffmpeg that does not drain in time must be stopped and must fail the export.
    ///     <para>
    ///         Before the fix the timeout was swallowed: disposal returned normally, so the session
    ///         reported <see cref="ExportPhase.Completed" /> for a file it had just tried to delete, and
    ///         nothing ever told the child to stop, so it went on encoding with nobody waiting for it.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task AnFfmpegThatDoesNotDrainInTime_IsStopped_AndFailsTheExport()
    {
        FfmpegLocation located = FfmpegLocator.Locate(null);
        if (!located.Found)
        {
            throw new SkipTestException("no ffmpeg on PATH; the subprocess sink cannot be exercised");
        }

        string path = Path.Combine(Path.GetTempPath(), $"dv-drain-{Guid.NewGuid():N}.gif");

        // GIF, because its palette pass does all of its work after the last frame: the drain is the
        // encode. No patience at all, so the timeout is reached however quickly this ffmpeg finishes.
        FfmpegFrameSink sink = new(new FfmpegSinkOptions(path, ExportFormats.Gif, 320, 240, 20,
            located.Directory))
        {
            ShutdownTimeout = TimeSpan.Zero
        };

        try
        {
            byte[] frame = new byte[320 * 240 * 4];
            for (int i = 0; i < 40; i++)
            {
                Array.Fill(frame, (byte)(i * 6));
                await sink.WriteAsync(frame, 320, 240, CancellationToken.None);
            }

            Exception? failure = null;
            try
            {
                await sink.DisposeAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await Assert.That(failure).IsTypeOf<FfmpegEncodeException>();

            // Windows will not delete a file another process holds open, so its absence is also the
            // evidence that the child is gone.
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     An encoder that takes every frame and then fails to finish the file must leave no file.
    ///     <para>
    ///         A GIF's palette pass and MP4's faststart rewrite both happen after the last frame, so a
    ///         full disk shows up here and nowhere earlier: the render loop ended cleanly, nothing was
    ///         aborted, and the failure is first seen by disposal. Before the fix that one path skipped
    ///         the delete, and a truncated file stayed at the path of a job reported Failed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AnEncoderThatFailsWhileFinishingTheFile_LeavesNoFile_AndFailsAsAnEncodeFailure()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-drainfail-{Guid.NewGuid():N}.mp4");
        TaskCompletionSource<bool> encoder = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FfmpegFrameSink sink = null!;
        sink = new FfmpegFrameSink(new FfmpegSinkOptions(path, ExportFormats.Mp4, 8, 8, 30))
        {
            StartEncoder = _ =>
            {
                File.WriteAllBytes(sink.EncodePath, [0, 0, 0, 24]); // what a muxer has written when it dies
                return encoder.Task;
            }
        };

        try
        {
            await sink.WriteAsync(new byte[8 * 8 * 4], 8, 8, CancellationToken.None);

            // Disposal is now waiting on the drain; the encoder dies under it.
            Task closing = sink.DisposeAsync().AsTask();
            IOException pipe = new("Pipe is broken");
            encoder.SetException(pipe);

            Exception? failure = null;
            try
            {
                await closing;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(File.Exists(sink.EncodePath)).IsFalse();
            await Assert.That(failure).IsTypeOf<FfmpegEncodeException>();
            await Assert.That(failure!.InnerException).IsSameReferenceAs(pipe);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The other half of the same switch: a caller that asked to keep partial output keeps it.</summary>
    [Test]
    public async Task AnEncoderThatFailsWhileFinishingTheFile_KeepsTheFile_WhenPartialOutputWasAskedFor()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-drainkeep-{Guid.NewGuid():N}.mp4");
        TaskCompletionSource<bool> encoder = new(TaskCreationOptions.RunContinuationsAsynchronously);

        FfmpegFrameSink sink = null!;
        sink = new FfmpegFrameSink(new FfmpegSinkOptions(path, ExportFormats.Mp4, 8, 8, 30,
            DeletePartialOnCancel: false))
        {
            StartEncoder = _ =>
            {
                File.WriteAllBytes(sink.EncodePath, [0, 0, 0, 24]);
                return encoder.Task;
            }
        };

        try
        {
            await sink.WriteAsync(new byte[8 * 8 * 4], 8, 8, CancellationToken.None);
            Task closing = sink.DisposeAsync().AsTask();
            encoder.SetException(new IOException("Pipe is broken"));

            await Assert.ThrowsAsync<FfmpegEncodeException>(async () => await closing);

            // Kept beside the output under its partial name; the output path itself is never written.
            await Assert.That(File.Exists(sink.EncodePath)).IsTrue();
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            File.Delete(sink.EncodePath);
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A run that fails half way must not leave a file that looks finished.
    ///     <para>
    ///         The session closes the sink on every path, and closing is where a GIF is written and
    ///         where ffmpeg muxes. Before the fix a render failure at frame 5 of 12 therefore finalised a
    ///         five-frame file that plays normally, at the path of a job reported Failed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ARenderFailureMidRun_LeavesNoGif()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-fail-{Guid.NewGuid():N}.gif");

        try
        {
            using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
            using CpuSurfaceProvider surfaces = new();

            Exception? failure = null;
            try
            {
                await new SceneExportSession(compositor).RunAsync(
                    ExportFixtures.Request(12, ExportFormats.Gif, new SKSizeI(64, 48), fps: 20),
                    new FailingFrameSource(ExportFixtures.Source(12), 5), new ManagedGifSink(path, 20),
                    surfaces, null, CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            await Assert.That(failure).IsNotNull();
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A cancel that lands after the last frame was written, and before the sink is closed.
    ///     <para>
    ///         Nothing in the render loop reads the token again by then, so nothing throws; but the sink
    ///         does read it, and writes no file. The run was reported Completed, with an output path at
    ///         which there was nothing. A cancelled run is Cancelled, wherever the cancel landed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ACancelAfterTheLastFrame_IsReportedCancelled_NotCompletedWithNoFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-latecancel-{Guid.NewGuid():N}.gif");

        try
        {
            using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
            using CpuSurfaceProvider surfaces = new();
            using CancellationTokenSource source = new();

            List<ExportProgress> reports = [];
            DirectProgress progress = new(report =>
            {
                reports.Add(report);

                // Finalizing is reported once, after the last write and before the sink is closed.
                if (report.Phase == ExportPhase.Finalizing)
                {
                    source.Cancel();
                }
            });

            Exception? failure = null;
            try
            {
                await new SceneExportSession(compositor).RunAsync(
                    ExportFixtures.Request(6, ExportFormats.Gif, new SKSizeI(64, 48), fps: 20),
                    ExportFixtures.Source(6), new ManagedGifSink(path, 20), surfaces, progress, source.Token);
            }
            catch (OperationCanceledException ex)
            {
                failure = ex;
            }

            // The two have to agree: no file and Cancelled, never no file and Completed.
            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(reports[^1].Phase).IsEqualTo(ExportPhase.Cancelled);
            await Assert.That(failure).IsNotNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A cancel that lands while ffmpeg is draining, after the session's last look at the token.
    ///     The cancel stops ffmpeg and the output is removed, so the close must say it was cancelled
    ///     rather than return as if the file were finished.
    /// </summary>
    [Test]
    public async Task ACancelDuringTheDrain_ThrowsFromTheClose_AndLeavesNoFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-draincancel-{Guid.NewGuid():N}.mp4");
        using CancellationTokenSource source = new();
        FfmpegFrameSink sink = DrainUntilKilled(path, ExportFormats.Mp4, 8, 8);

        try
        {
            await sink.WriteAsync(new byte[8 * 8 * 4], 8, 8, source.Token);
            Task closing = sink.DisposeAsync().AsTask();
            await source.CancelAsync();

            Exception? failure = null;
            try
            {
                await closing;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await Assert.That(failure is OperationCanceledException).IsTrue();
            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(File.Exists(sink.EncodePath)).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A caller whose own token was cancelled before it closed the sink, as an <c>await using</c> does
    ///     after the cancel it is handling. The output goes, but the close does not throw over the
    ///     caller's own exception.
    /// </summary>
    [Test]
    public async Task ACancelBeforeTheClose_ClosesQuietly_AndLeavesNoFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-precancel-{Guid.NewGuid():N}.mp4");
        using CancellationTokenSource source = new();
        FfmpegFrameSink sink = DrainUntilKilled(path, ExportFormats.Mp4, 8, 8);

        try
        {
            await sink.WriteAsync(new byte[8 * 8 * 4], 8, 8, source.Token);
            await source.CancelAsync();
            await sink.DisposeAsync();

            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(File.Exists(sink.EncodePath)).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A cancel between the session's last look at the token and the close. Both sinks then close
    ///     quietly with no file, so the session has to look again.
    /// </summary>
    [Test]
    [Arguments(ExportFormats.Gif)]
    [Arguments(ExportFormats.WebM)]
    public async Task ACancelJustBeforeTheClose_IsReportedCancelled_NotCompletedWithNoFile(string format)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-closecancel-{Guid.NewGuid():N}.{format}");
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        using CancellationTokenSource source = new();
        List<ExportProgress> reports = [];
        bool gif = string.Equals(format, ExportFormats.Gif, StringComparison.Ordinal);
        int fps = gif ? 20 : 30;
        IFrameSink inner = gif ? new ManagedGifSink(path, fps) : DrainUntilKilled(path, format, 64, 48);
        CancelOnCloseFrameSink sink = new(inner, source, beforeClose: true);

        try
        {
            Exception? failure = null;
            try
            {
                await new SceneExportSession(compositor).RunAsync(
                    ExportFixtures.Request(4, format, new SKSizeI(64, 48), fps: fps),
                    ExportFixtures.Source(4), sink, surfaces, new DirectProgress(reports.Add), source.Token);
            }
            catch (OperationCanceledException ex)
            {
                failure = ex;
            }

            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(reports[^1].Phase).IsEqualTo(ExportPhase.Cancelled);
            await Assert.That(failure).IsNotNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The same cancel through the session: the run is Cancelled, never Completed with no file.</summary>
    [Test]
    public async Task ACancelDuringTheDrain_IsReportedCancelled_NotCompletedWithNoFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-draincancel-{Guid.NewGuid():N}.webm");
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        using CancellationTokenSource source = new();
        List<ExportProgress> reports = [];
        CancelOnCloseFrameSink sink = new(DrainUntilKilled(path, ExportFormats.WebM, 64, 48), source);

        try
        {
            Exception? failure = null;
            try
            {
                await new SceneExportSession(compositor).RunAsync(
                    ExportFixtures.Request(4, ExportFormats.WebM, new SKSizeI(64, 48), fps: 30),
                    ExportFixtures.Source(4), sink, surfaces, new DirectProgress(reports.Add), source.Token);
            }
            catch (OperationCanceledException ex)
            {
                failure = ex;
            }

            await Assert.That(File.Exists(path)).IsFalse();
            await Assert.That(reports[^1].Phase).IsEqualTo(ExportPhase.Cancelled);
            await Assert.That(failure).IsNotNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     An export to a path that already holds an earlier export, the dialog's default, that is then
    ///     cancelled or fails while finishing: the earlier export must still be there, untouched.
    /// </summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ACancelledOrFailedExport_LeavesAnEarlierExportAtThePathIntact(bool cancel)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-keepearlier-{Guid.NewGuid():N}.mp4");
        byte[] earlier = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(path, earlier);
        TaskCompletionSource<bool> encoder = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FfmpegFrameSink sink = new(new FfmpegSinkOptions(path, ExportFormats.Mp4, 8, 8, 30))
        {
            StartEncoder = kill =>
            {
                kill.Register(() => encoder.TrySetCanceled(kill));
                return encoder.Task;
            }
        };

        try
        {
            await sink.WriteAsync(new byte[8 * 8 * 4], 8, 8, CancellationToken.None);
            if (cancel)
            {
                ((IAbortableFrameSink)sink).Abort();
                await sink.DisposeAsync();
            }
            else
            {
                Task closing = sink.DisposeAsync().AsTask();
                encoder.SetException(new IOException("No space left on device"));
                await Assert.ThrowsAsync<FfmpegEncodeException>(async () => await closing);
            }

            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(earlier);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A finished encode replaces the earlier export and leaves nothing beside it.</summary>
    [Test]
    public async Task AFinishedExport_ReplacesTheEarlierOne_AndLeavesNoPartialFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dv-replace-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        byte[] encoded = [0, 0, 0, 24, 9, 9];
        FfmpegFrameSink sink = null!;
        sink = new FfmpegFrameSink(new FfmpegSinkOptions(path, ExportFormats.Mp4, 8, 8, 30))
        {
            StartEncoder = _ =>
            {
                File.WriteAllBytes(sink.EncodePath, encoded);
                return Task.FromResult(true);
            }
        };

        try
        {
            await sink.WriteAsync(new byte[8 * 8 * 4], 8, 8, CancellationToken.None);
            await sink.DisposeAsync();

            await Assert.That(await File.ReadAllBytesAsync(path)).IsEquivalentTo(encoded);
            await Assert.That(File.Exists(sink.EncodePath)).IsFalse();
            await Assert.That(Path.GetExtension(sink.EncodePath)).IsEqualTo(".mp4");
            await Assert.That(Path.GetDirectoryName(sink.EncodePath)).IsEqualTo(Path.GetDirectoryName(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The same through a real ffmpeg: it encodes beside the output, and the result is moved over it.</summary>
    [Test]
    [Category("Integration")]
    public async Task ARealEncode_ReplacesTheEarlierExport_AndLeavesNoPartialFile()
    {
        FfmpegLocation located = FfmpegLocator.Locate(null);
        if (!located.Found)
        {
            throw new SkipTestException("no ffmpeg on PATH; the subprocess sink cannot be exercised");
        }

        string dir = Path.Combine(Path.GetTempPath(), $"dv-realreplace-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "clip.webm");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);

        try
        {
            FfmpegFrameSink sink = new(new FfmpegSinkOptions(path, ExportFormats.WebM, 64, 48, 30, located.Directory));
            for (int i = 0; i < 3; i++)
            {
                await sink.WriteAsync(new byte[64 * 48 * 4], 64, 48, CancellationToken.None);
            }

            await sink.DisposeAsync();

            await Assert.That(new FileInfo(path).Length).IsGreaterThan(5);
            await Assert.That(Directory.GetFiles(dir)).Count().IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>The managed GIF writer replaces an earlier export the same way: whole, or not at all.</summary>
    [Test]
    public async Task AFinishedGif_ReplacesTheEarlierOne_AndLeavesNoPartialFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dv-gifreplace-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "clip.gif");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);

        try
        {
            ManagedGifSink sink = new(path, 20);
            await sink.WriteAsync(new byte[8 * 8 * 4], 8, 8, CancellationToken.None);
            await sink.DisposeAsync();

            byte[] written = await File.ReadAllBytesAsync(path);
            await Assert.That(written.Length).IsGreaterThan(5);
            await Assert.That(written[0]).IsEqualTo((byte)'G');
            await Assert.That(Directory.GetFiles(dir)).Count().IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>An encoder that writes a stub file and runs until the token that stops ffmpeg fires.</summary>
    private static FfmpegFrameSink DrainUntilKilled(string path, string format, int width, int height)
    {
        FfmpegFrameSink sink = null!;
        sink = new FfmpegFrameSink(new FfmpegSinkOptions(path, format, width, height, 30))
        {
            StartEncoder = kill =>
            {
                File.WriteAllBytes(sink.EncodePath, [0, 0, 0, 24]);
                TaskCompletionSource<bool> encoder = new(TaskCreationOptions.RunContinuationsAsynchronously);
                kill.Register(() => encoder.TrySetCanceled(kill));
                return encoder.Task;
            }
        };
        return sink;
    }

    /// <summary>
    ///     The cancel arrives once the session has started closing the sink: mid-drain, or with
    ///     <paramref name="beforeClose" /> just before the inner sink's close begins.
    /// </summary>
    private sealed class CancelOnCloseFrameSink(
        IFrameSink inner,
        CancellationTokenSource source,
        bool beforeClose = false)
        : IFrameSink, IAbortableFrameSink
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct) =>
            inner.WriteAsync(rgba, width, height, ct);

        public void Abort() => ((IAbortableFrameSink)inner).Abort();

        public async ValueTask DisposeAsync()
        {
            if (beforeClose)
            {
                await source.CancelAsync();
                await inner.DisposeAsync();
                return;
            }

            Task closing = inner.DisposeAsync().AsTask();
            await source.CancelAsync();
            await closing;
        }
    }

    /// <summary>The same failure through the subprocess sink: ffmpeg is stopped, not allowed to mux.</summary>
    [Test]
    [Category("Integration")]
    public async Task ARenderFailureMidRun_LeavesNoFfmpegOutput()
    {
        FfmpegLocation located = FfmpegLocator.Locate(null);
        if (!located.Found)
        {
            throw new SkipTestException("no ffmpeg on PATH; the subprocess sink cannot be exercised");
        }

        string path = Path.Combine(Path.GetTempPath(), $"dv-fail-{Guid.NewGuid():N}.webm");

        try
        {
            using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
            using CpuSurfaceProvider surfaces = new();

            Exception? failure = null;
            try
            {
                await new SceneExportSession(compositor).RunAsync(
                    ExportFixtures.Request(40, ExportFormats.WebM, new SKSizeI(64, 48), fps: 30),
                    new FailingFrameSource(ExportFixtures.Source(40), 30),
                    new FfmpegFrameSink(new FfmpegSinkOptions(path, ExportFormats.WebM, 64, 48, 30,
                        located.Directory)),
                    surfaces, null, CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                failure = ex;
            }

            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Message).IsEqualTo("the demo could not be decoded");
            await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     A sink that only fails when it is closed must fail the run.
    ///     <para>
    ///         This is the partial-file lie: every frame rendered, so the loop succeeded, but the
    ///         container was never finalised. Muxing happens on disposal, so "all frames written" is not
    ///         "a file exists that plays": reporting <see cref="ExportPhase.Completed" /> here would
    ///         point a user at a file that does not decode.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ASinkThatFailsOnlyWhenClosed_FailsTheRun_AndNeverReportsCompleted()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        FailOnCloseFrameSink sink = new();
        List<ExportProgress> reports = [];

        Exception? failure = null;
        try
        {
            await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(6),
                ExportFixtures.Source(6), sink, surfaces, new DirectProgress(reports.Add),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(sink.DisposeCount).IsEqualTo(1);

        foreach (ExportProgress report in reports)
        {
            await Assert.That(report.Phase).IsNotEqualTo(ExportPhase.Completed);
        }
    }

    /// <summary>
    ///     The terminal progress report is a contract: exactly one arrives, on every path.
    ///     <para>
    ///         A caller that drives a progress bar off <see cref="ExportProgress.Phase" /> has nothing
    ///         else to go on. Before the fix a throwing disposal escaped the session's <c>finally</c>
    ///         before the terminal report was made, so the last thing such a caller ever saw was
    ///         <see cref="ExportPhase.Rendering" />, a bar frozen at 100 % on an export that failed.
    ///     </para>
    /// </summary>
    [Test]
    public async Task AThrowingDisposal_StillReportsATerminalPhase()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        FailOnCloseFrameSink sink = new();
        List<ExportProgress> reports = [];

        try
        {
            await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(4),
                ExportFixtures.Source(4), sink, surfaces, new DirectProgress(reports.Add),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // The point of the test is what was reported, not what was thrown.
        }

        await Assert.That(reports.Count).IsGreaterThan(0);
        await Assert.That(reports[^1].Phase).IsEqualTo(ExportPhase.Failed);
        await Assert.That(reports[^1].Detail).IsNotNull();
    }

    /// <summary>A cancelled run still reports its terminal phase, and reports it as cancelled.</summary>
    [Test]
    public async Task ACancelledRun_ReportsCancelled_AsItsLastWord()
    {
        using SceneCompositor compositor = SceneLayerCatalog.CreateSceneStack();
        using CpuSurfaceProvider surfaces = new();
        using CancellationTokenSource source = new();
        CancellingFrameSink sink = new(source, 3);
        List<ExportProgress> reports = [];

        try
        {
            await new SceneExportSession(compositor).RunAsync(ExportFixtures.Request(40),
                ExportFixtures.Source(40), sink, surfaces, new DirectProgress(reports.Add),
                source.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        await Assert.That(reports[^1].Phase).IsEqualTo(ExportPhase.Cancelled);
    }

    /// <summary>Reports straight through, so a test sees them in the order the session made them.</summary>
    private sealed class DirectProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }

    /// <summary>A source that stops producing frames part way through, as a corrupt demo region does.</summary>
    private sealed class FailingFrameSource(ISceneFrameSource inner, int failAtFrame) : ISceneFrameSource
    {
        public int FrameCount => inner.FrameCount;

        public SceneTime TimeAt(int frameIndex) => inner.TimeAt(frameIndex);

        public Scene2DFrame FrameAt(int frameIndex) =>
            frameIndex >= failAtFrame
                ? throw new InvalidOperationException("the demo could not be decoded")
                : inner.FrameAt(frameIndex);
    }

    /// <summary>Accepts every frame and then fails on close, a container that will not mux.</summary>
    private sealed class FailOnCloseFrameSink : IFrameSink
    {
        public int DisposeCount { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            throw new InvalidOperationException("the container could not be finalised");
        }
    }
}

/// <summary>
///     The bridge must never leave a writer parked on a queue that has stopped being drained. Its
///     <c>Fault</c> path existed from the start and had no caller. These pin the caller.
/// </summary>
public class ChannelVideoFrameSourceFaultTests
{
    [Test]
    public async Task FaultingTheChannel_ReleasesAWriterAlreadyBlockedOnAFullQueue()
    {
        ChannelVideoFrameSource source = new(2);
        byte[] frame = new byte[16];

        Task writer = Task.Run(async () =>
        {
            for (int i = 0; i < 50; i++)
            {
                await source.WriteAsync(frame, 2, 2, CancellationToken.None);
            }
        });

        // Let the writer fill the queue and park on it.
        Stopwatch spin = Stopwatch.StartNew();
        while (!writer.IsCompleted && spin.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10);
        }

        await Assert.That(writer.IsCompleted).IsFalse();

        source.Fault(new InvalidOperationException("the encoder exited"));

        Task finished = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(ReferenceEquals(finished, writer)).IsTrue();
        await Assert.That(writer.IsFaulted).IsTrue();

        source.Dispose();
    }
}
