#region

using System.Globalization;

#endregion

// The plumbing that touches App-only concerns (the heavy-job gate, the app-managed ffmpeg
// directory) stays in the App, under DemoViewer.NET.Services.Export.Pack.
namespace DemoViewer.NET.Playback2D.Pipeline.Export.Pack;

/// <summary>Renders one pack clip into a sink. The seam every Pack Export test replaces.</summary>
public interface IPackClipRenderer
{
    /// <summary>
    ///     Renders the clip into <paramref name="sink" />, which it disposes (through the session) exactly
    ///     once. Runs on a worker.
    /// </summary>
    /// <param name="clip">The planned clip.</param>
    /// <param name="settings">The pack's settings.</param>
    /// <param name="sink">Where the frames go.</param>
    /// <param name="ct">Cancels the render.</param>
    Task RenderAsync(PackClip clip, PackSettings settings, IFrameSink sink, CancellationToken ct);
}

/// <summary>Opens the files a pack writes. The encoder half, behind a seam so the stitch is testable without ffmpeg.</summary>
public interface IPackEncoder
{
    /// <summary>
    ///     Chooses the encoder for the pack before anything renders, refusing a format this machine cannot
    ///     write (a video format with no ffmpeg).
    /// </summary>
    /// <param name="settings">The pack's settings.</param>
    /// <param name="ct">Cancels the probe.</param>
    /// <exception cref="ExportValidationException">The format cannot be written here.</exception>
    void Prepare(PackSettings settings, CancellationToken ct);

    /// <summary>A sink writing <paramref name="outputPath" /> with the prepared encoder.</summary>
    /// <param name="outputPath">The file.</param>
    /// <param name="settings">The pack's settings.</param>
    IFrameSink Open(string outputPath, PackSettings settings);
}

/// <summary>Where a pack export is.</summary>
/// <param name="Segment">The segment being written, one-based; 0 before the first.</param>
/// <param name="SegmentCount">Segments in the pack.</param>
/// <param name="Label">What is being written: a section's title, or a clip's number and note.</param>
public readonly record struct PackProgress(int Segment, int SegmentCount, string Label);

/// <summary>What a pack export wrote.</summary>
/// <param name="Outputs">The files written: the one pack file, or one GIF per clip.</param>
/// <param name="ClipsRendered">Clips that made it in.</param>
/// <param name="Failed">Clips that failed to render, with why; the pack went on without them.</param>
public sealed record PackResult(IReadOnlyList<string> Outputs, int ClipsRendered, IReadOnlyList<PackSkip> Failed);

/// <summary>
///     Pack Export: one video from many demos, annotations burned in, the Review
///     Queue's sections as title cards.
///     <para>
///         <b>How the clips are stitched.</b> One <c>SceneExportSession</c> per clip, as every other export
///         renders, all writing into <b>one</b> encoder: each session is handed a
///         <see cref="PackSegmentSink" /> over the pack's sink, whose dispose ends the clip and not the file,
///         and a title card is written straight into the same sink. The alternative was a file per clip
///         joined by ffmpeg's concat demuxer; that costs a second ffmpeg pass and a temp file per clip, and a
///         stream copy across separately encoded files leaves timestamp seams and per-file edit lists that
///         phone players stutter on. One encode has one timeline and one <c>moov</c> atom at the front
///         (MP4's faststart), which is what plays on a phone. The cost is that every clip must share one
///         frame size and rate, which a pack's settings fix anyway.
///     </para>
///     <para>
///         <b>GIF</b> is one file per clip (see <see cref="PackPlanner" />), each its own session and sink,
///         each held to the GIF frame cap.
///     </para>
///     <para>
///         <b>Failure.</b> A clip that fails on its own is left out and reported; a failure in the pack's
///         encoder ends the pack and removes the half-written file, as a cancellation does.
///     </para>
/// </summary>
public sealed class PackExporter
{
    private readonly IPackClipRenderer _clips;
    private readonly IPackEncoder _encoder;
    private readonly Action<string>? _log;
    private readonly ScenePalette _palette;

    /// <param name="clips">Renders a clip.</param>
    /// <param name="encoder">Opens the pack's files.</param>
    /// <param name="palette">The title cards' colours; the dark scene palette when null.</param>
    /// <param name="log">Optional line sink for a clip that failed.</param>
    public PackExporter(IPackClipRenderer clips, IPackEncoder encoder, ScenePalette? palette = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(encoder);
        _clips = clips;
        _encoder = encoder;
        _palette = palette ?? ScenePalette.Dark;
        _log = log;
    }

    /// <summary>Renders the plan. Runs on a worker; touches no dispatcher.</summary>
    /// <param name="plan">The pack.</param>
    /// <param name="progress">Where it is, or null.</param>
    /// <param name="ct">Cancels the pack; the half-written file does not survive.</param>
    /// <exception cref="ExportValidationException">The settings or the machine cannot write this pack.</exception>
    public async Task<PackResult> ExportAsync(PackPlan plan, IProgress<PackProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (PackPlanner.Validate(plan.Settings) is { } problem)
        {
            throw new ExportValidationException(problem);
        }

        if (plan.Clips.Count == 0)
        {
            throw new ExportValidationException("there is no clip in the queue that can be rendered");
        }

        _encoder.Prepare(plan.Settings, ct);
        return plan.Settings.IsGif
            ? await ExportGifsAsync(plan, progress, ct).ConfigureAwait(false)
            : await ExportVideoAsync(plan, progress, ct).ConfigureAwait(false);
    }

    private async Task<PackResult> ExportVideoAsync(PackPlan plan, IProgress<PackProgress>? progress,
        CancellationToken ct)
    {
        PackSettings settings = plan.Settings;
        List<PackSkip> failed = [];
        int rendered = 0;
        WrittenSink sink = new(_encoder.Open(settings.OutputPath, settings));
        try
        {
            for (int i = 0; i < plan.Segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                switch (plan.Segments[i])
                {
                    case PackTitleCard card:
                        progress?.Report(new PackProgress(i + 1, plan.Segments.Count, card.Title));
                        await WriteTitleCardAsync(card, settings, sink, ct, _palette).ConfigureAwait(false);
                        break;

                    case PackClip clip:
                        progress?.Report(new PackProgress(i + 1, plan.Segments.Count, Label(clip)));
                        PackSegmentSink segment = new(sink);
                        try
                        {
                            await _clips.RenderAsync(clip, settings, segment, ct).ConfigureAwait(false);
                            rendered++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && !segment.InnerFaulted)
                        {
                            Fail(failed, clip, ex);

                            // Frames already in the stream stay there; a clip that failed half way shows its half.
                            if (segment.FramesWritten > 0)
                            {
                                rendered++;
                            }
                        }

                        break;
                }
            }

            if (rendered == 0)
            {
                throw new ExportValidationException(
                    $"no clip could be rendered: {string.Join("; ", failed.Select(f => f.Reason))}");
            }
        }
        catch
        {
            try
            {
                await sink.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The pack is already failing for a reason of its own; that one is the one to report.
            }

            // Only what this run wrote. Nothing is opened before the first frame, so a pack cancelled
            // while its first demo parses has no file of its own, and the one at the path is the pack an
            // earlier export finished.
            if (sink.Written)
            {
                TryDelete(settings.OutputPath);
            }

            throw;
        }

        try
        {
            await sink.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Closing the file is where it is muxed. Every clip rendered, and there is still no pack.
            if (sink.Written)
            {
                TryDelete(settings.OutputPath);
            }

            throw;
        }

        // A cancel after the last clip: a sink closes quietly on a token already cancelled, with no file.
        if (ct.IsCancellationRequested)
        {
            if (sink.Written)
            {
                TryDelete(settings.OutputPath);
            }

            throw new OperationCanceledException(ct);
        }

        return new PackResult([settings.OutputPath], rendered, failed);
    }

    private async Task<PackResult> ExportGifsAsync(PackPlan plan, IProgress<PackProgress>? progress,
        CancellationToken ct)
    {
        List<PackSkip> failed = [];
        List<string> outputs = [];
        IReadOnlyList<PackClip> clips = plan.Clips;
        for (int i = 0; i < clips.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            PackClip clip = clips[i];
            string path = clip.OutputPath
                ?? PackPlanner.ClipPath(plan.Settings.OutputPath, clip.Number, clip.Section, clip.Entry);
            progress?.Report(new PackProgress(i + 1, clips.Count, Label(clip)));
            WrittenSink? sink = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                sink = new WrittenSink(_encoder.Open(path, plan.Settings));
                await _clips.RenderAsync(clip, plan.Settings, sink, ct).ConfigureAwait(false);
                outputs.Add(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(failed, clip, ex);

                // As for the video pack: a clip that failed before a frame left the file alone.
                if (sink is { Written: true })
                {
                    TryDelete(path);
                }
            }
        }

        if (outputs.Count == 0)
        {
            throw new ExportValidationException(
                $"no clip could be rendered: {string.Join("; ", failed.Select(f => f.Reason))}");
        }

        return new PackResult(outputs, outputs.Count, failed);
    }

    /// <summary>Writes a title card: one drawn frame, repeated for the card's hold.</summary>
    private static async Task WriteTitleCardAsync(PackTitleCard card, PackSettings settings, IFrameSink sink,
        CancellationToken ct, ScenePalette? palette = null)
    {
        byte[] rgba =
            PackTitleCardRenderer.Render(card.Title, card.Subtitle, settings.Side, palette ?? ScenePalette.Dark);
        for (int f = 0; f < card.Frames; f++)
        {
            ct.ThrowIfCancellationRequested();
            await sink.WriteAsync(rgba, settings.Side, settings.Side, ct).ConfigureAwait(false);
        }
    }

    /// <summary>"Clip 3: smoke into CT", as progress and the failure list name a clip.</summary>
    /// <param name="clip">The clip.</param>
    private static string Label(PackClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        string number = string.Create(CultureInfo.InvariantCulture, $"clip {clip.Number}");
        return string.IsNullOrWhiteSpace(clip.Entry.Note) ? number : $"{number}: {clip.Entry.Note}";
    }

    private void Fail(List<PackSkip> failed, PackClip clip, Exception ex)
    {
        failed.Add(new PackSkip(clip.Entry, $"{Label(clip)}: {ex.Message}"));
        _log?.Invoke($"pack export: {Label(clip)} in {clip.Entry.DemoPath}: {ex.Message}");
    }

    /// <summary>
    ///     A file's sink, and whether this run has put a frame into it. A sink opens its file no earlier
    ///     than its first frame, so a run that never wrote has nothing at the path that is its own to remove.
    ///     A sink that opens it later still, at its disposal, says so itself: its frames are not yet a file.
    /// </summary>
    private sealed class WrittenSink(IFrameSink inner) : IFrameSink, IAbortableFrameSink
    {
        private bool _written;

        /// <summary>A frame was written, or a write failed part way: the file may be this run's.</summary>
        public bool Written => inner is IOutputOpeningFrameSink opening ? opening.OutputOpened : _written;

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
        {
            try
            {
                await inner.WriteAsync(rgba, width, height, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _written = true;
                throw;
            }

            _written = true;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        // The session asks for this by type, so the wrapper must not hide it from the sink underneath.
        void IAbortableFrameSink.Abort() => (inner as IAbortableFrameSink)?.Abort();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"pack export: could not remove {path}: {ex.Message}");
        }
    }
}
