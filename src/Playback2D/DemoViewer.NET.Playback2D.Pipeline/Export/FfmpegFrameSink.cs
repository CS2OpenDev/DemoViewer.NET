#region

using System.Globalization;
using DemoViewer.NET.Playback2D.Pipeline.Ffmpeg;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using ChannelClosedException = System.Threading.Channels.ChannelClosedException;

#endregion

namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>How an <see cref="FfmpegFrameSink" /> is configured.</summary>
/// <param name="OutputPath">Where the encoded file goes. Overwritten.</param>
/// <param name="FormatId">One of <see cref="ExportFormats" />.</param>
/// <param name="Width">Frame width; must match what the session renders.</param>
/// <param name="Height">Frame height.</param>
/// <param name="Fps">Output frame rate.</param>
/// <param name="BinaryFolder">
///     The directory holding <c>ffmpeg</c>, from <see cref="Ffmpeg.FfmpegLocator" />. Null relies on
///     <c>PATH</c>. Passed <b>per invocation</b>, never through <c>GlobalFFOptions</c>: that is
///     process-global mutable state, and a CLI export and an in-app export must be able to disagree.
/// </param>
/// <param name="Encoder">
///     Which rung of the <see cref="Ffmpeg.EncoderLadder" /> encodes, and at what quality. Null
///     means the format's software rung at
///     <see cref="ExportQuality.Standard" />, so a sink is constructible without a probe ever running.
///     <para>
///         It is a <b>value carried per sink</b>, never a process-global: two exports in one process may
///         be encoding on two different rungs at once. This is the same argument that already
///         keeps <c>BinaryFolder</c> off <c>GlobalFFOptions</c>.
///     </para>
/// </param>
/// <param name="DeletePartialOnCancel">Remove a half-written file when the export is cancelled or fails part way.</param>
/// <param name="Log">Optional line sink for ffmpeg's stderr.</param>
public sealed record FfmpegSinkOptions(
    string OutputPath,
    string FormatId,
    int Width,
    int Height,
    int Fps,
    string? BinaryFolder = null,
    EncoderSelection? Encoder = null,
    bool DeletePartialOnCancel = true,
    Action<string>? Log = null)
{
    /// <summary>The selection, resolved. Never null (see <see cref="Encoder" />).</summary>
    public EncoderSelection ResolvedEncoder =>
        Encoder ?? EncoderSelection.SoftwareDefault(FormatId);
}

/// <summary>
///     Encodes rendered frames by piping raw RGBA to an <b>ffmpeg subprocess</b>.
///     <para>
///         FFMpegCore builds the argument list and owns the process; no ffmpeg code is linked into this
///         program, which is what keeps the licence posture clean (see <c>THIRD-PARTY-NOTICES.md</c> §g).
///         The transport is FFMpegCore's named pipe rather than literal stdin: same mechanism, same
///         separateness, and it is what the library supports.
///     </para>
///     <para>
///         <b>The process starts on the first frame</b>, not in the constructor: an export cancelled
///         before it renders anything then leaves no process and no file at all.
///     </para>
///     <para>
///         <b>Cancellation kills ffmpeg.</b> The first write links the caller's token into the sink's own
///         source, which is what <c>CancellableThrough</c> watches; tripping it terminates the process,
///         and <see cref="DisposeAsync" /> then removes the partial output.
///     </para>
///     <para>
///         <b>Disposal gives up on an ffmpeg that has said nothing for 30 seconds</b> while draining:
///         it stops the process, removes the output, and throws <see cref="FfmpegEncodeException" />.
///     </para>
/// </summary>
public sealed class FfmpegFrameSink : IFrameSink, IAbortableFrameSink
{
    /// <summary>How many trailing stderr lines are kept to explain a failure.</summary>
    private const int StderrTailLines = 6;

    /// <summary>How long ffmpeg may stay silent while <see cref="DisposeAsync" /> drains it before it is stopped.</summary>
    private static readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a stopped ffmpeg is given to actually go away.</summary>
    private static readonly TimeSpan _killGrace = TimeSpan.FromSeconds(5);

    // Wall time, for the drain's silence limit only. ExportClock rather than a tick count of our own:
    // it is the one type in the export path BannedApiTests lets read a clock.
    private readonly ExportClock _clock = ExportClock.Start();
    private readonly ChannelVideoFrameSource _frames = new();
    private readonly FfmpegSinkOptions _options;
    private readonly Queue<string> _stderrTail = new(StderrTailLines);
    private bool _aborted;
    private bool _disposed;
    private Task<bool>? _encoder;
    private CancellationTokenSource? _kill;
    private long _lastStderrMs;

    /// <summary>Creates the sink. No process is started until the first frame arrives.</summary>
    /// <param name="options">Output path, format and encoder settings.</param>
    public FfmpegFrameSink(FfmpegSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireQuotablePath(options.OutputPath);

        // Before anything is rendered: ffmpeg will not create it, and its refusal would otherwise land
        // after the whole range has been encoded into a pipe going nowhere.
        ExportOutputPath.EnsureDirectory(options.OutputPath);

        _options = options;
    }

    /// <summary>Frames handed to the encoder so far.</summary>
    private int FramesWritten { get; set; }

    /// <summary>The drain wait. Settable so a test can reach the timeout without a 30 s encode.</summary>
    internal TimeSpan ShutdownTimeout { get; init; } = _shutdownTimeout;

    /// <summary>
    ///     Stands in for the ffmpeg process, given the token that stops it. Settable so a test can end the
    ///     encoder the way a full disk does, at a moment of its choosing, without a child process.
    /// </summary>
    internal Func<CancellationToken, Task<bool>>? StartEncoder { get; init; }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (width != _options.Width || height != _options.Height)
        {
            throw new ExportValidationException(string.Create(CultureInfo.InvariantCulture,
                $"The sink was built for {_options.Width}x{_options.Height} but got {width}x{height}; a rawvideo stream carries no frame header, so a size change mid-stream is undecodable."));
        }

        // Queue the frame BEFORE starting ffmpeg: FFMpegCore builds the input arguments by pulling the
        // first frame off this enumerator (that is how it learns the pixel format and the size), so a
        // start with an empty channel would block the argument build until the next write.
        try
        {
            await _frames.WriteAsync(rgba, width, height, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException) when (_encoder is not null)
        {
            // The stream was ended under us, which only EndFrameStream does, and only because the
            // encoder stopped. Surface why it stopped rather than the channel's own
            // "the channel has been closed", which names the symptom and not one cause.
            await AwaitEncoderAsync().ConfigureAwait(false);
            throw;
        }

        FramesWritten++;

        if (_encoder is null)
        {
            _kill = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _encoder = StartEncoder?.Invoke(_kill.Token)
                       ?? BuildProcessor(_kill.Token).ProcessAsynchronously(true, BuildOptions(_options));

            // The pump task is the channel's ONLY reader. If ffmpeg exits early (a bad output path, a
            // full disk, a codec the located build does not carry), nothing will ever drain the queue
            // again, and a writer parked on a full one would wait forever. That is risk R2's deadlock,
            // and it is not covered by DisposeAsync's timeout because disposal is never reached. Ending
            // the encoder therefore ends the frame stream, which turns that wait into the encoder's own
            // exception at the very next write.
            _ = _encoder.ContinueWith(EndFrameStream, _frames, CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }

        if (_encoder.IsFaulted)
        {
            // Surface an encoder that died (a bad codec name, a full disk) at the next write instead of
            // rendering another 1700 frames into a pipe nobody is reading.
            await AwaitEncoderAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Stops ffmpeg where it is, so the disposal that follows removes the output rather than muxing it.
    ///     <para>
    ///         Without this a run that failed for a reason of its own (a frame that would not decode)
    ///         still ended its stream cleanly, and a clean end of stream is what tells ffmpeg to
    ///         finalise the container: a shorter video that plays, where a failed export was reported.
    ///     </para>
    /// </summary>
    void IAbortableFrameSink.Abort()
    {
        if (_disposed)
        {
            return;
        }

        _aborted = true;
        _kill?.Cancel();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Whether the output is to be removed: the run was aborted or cancelled, or the drain below fails.
        bool discard = _aborted || (_kill?.IsCancellationRequested ?? false);
        _frames.Complete();

        if (_encoder is not null)
        {
            try
            {
                // A timeout rather than an unbounded await: R2's failure mode is a deadlock between the
                // pump and the writer, and a diagnosable "ffmpeg did not exit in 30 s" beats a hang.
                await WaitForDrainAsync(_encoder).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                discard = true;
            }
            catch (TimeoutException)
            {
                // Abandoning the wait stops nothing: the child would go on encoding with nobody waiting
                // for it, and holding the output open against the delete below. Stop it, and say so:
                // returning normally here is what the session reports as Completed.
                string message = string.Create(CultureInfo.InvariantCulture,
                    $"ffmpeg said nothing for {ShutdownTimeout.TotalSeconds:0.###} s while finishing the file and was stopped; the export is incomplete.");
                _options.Log?.Invoke(message);
                discard = true;
                await StopEncoderAsync(_encoder).ConfigureAwait(false);
                throw new FfmpegEncodeException(message);
            }
            catch (Exception ex)
            {
                // Every frame was taken and ffmpeg then failed to finish the file: a full disk under a
                // GIF's palette pass or MP4's faststart rewrite, which both run after the last frame.
                // Nothing aborted this run, so this is the only place that learns the file is not one.
                _options.Log?.Invoke($"ffmpeg failed: {ex.Message}");
                discard = true;

                if (ex is FfmpegEncodeException)
                {
                    throw;
                }

                // The same naming of the cause AwaitEncoderAsync does for a failure seen by a write.
                string tail = StderrTail();
                throw tail.Length == 0
                    ? new FfmpegEncodeException($"ffmpeg failed: {ex.Message}", ex)
                    : new FfmpegEncodeException($"ffmpeg failed: {tail}", ex);
            }
            finally
            {
                _frames.Dispose();
                _kill?.Dispose();

                if (discard && _options.DeletePartialOnCancel)
                {
                    TryDeleteOutput();
                }
            }

            return;
        }

        _frames.Dispose();
        _kill?.Dispose();
    }

    /// <summary>
    ///     Waits for the encoder to finish, for as long as ffmpeg keeps talking.
    ///     <para>
    ///         The limit is on <b>silence</b>, not on the drain as a whole. A GIF's palette pass does all
    ///         of its work after the last frame, so on a long clip the drain legitimately outlasts any
    ///         flat limit; ffmpeg reports progress on stderr the whole time, and a process that is still
    ///         reporting is not the deadlock this wait exists to end.
    ///     </para>
    /// </summary>
    /// <exception cref="TimeoutException">ffmpeg wrote nothing for <see cref="ShutdownTimeout" />.</exception>
    private async Task WaitForDrainAsync(Task encoder)
    {
        Volatile.Write(ref _lastStderrMs, NowMs());

        while (!encoder.IsCompleted)
        {
            TimeSpan remaining = ShutdownTimeout -
                                 TimeSpan.FromMilliseconds(NowMs() - Volatile.Read(ref _lastStderrMs));
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException();
            }

            try
            {
                await encoder.WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Go round: stderr may have moved since the wait began. Unfiltered on purpose. The
                // encoder can finish between the wait's timer firing and this handler running, and a
                // filter on "still running" then lets the wait's timeout out as if it were the stall:
                // a finished file deleted and the export failed. The loop condition and the await below
                // read the encoder's own outcome; the throw above is the only stall there is.
            }
        }

        await encoder.ConfigureAwait(false);
    }

    private long NowMs() => (long)_clock.Elapsed.TotalMilliseconds;

    /// <summary>Stops ffmpeg through the token <c>CancellableThrough</c> watches and waits briefly for it to go.</summary>
    private async Task StopEncoderAsync(Task encoder)
    {
        try
        {
            await _kill!.CancelAsync().ConfigureAwait(false);
            await encoder.WaitAsync(_killGrace).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever a stopped encoder ends with (cancellation, a broken pipe, a non-zero exit, or
            // still nothing at all) is not the news. The stall is, and the caller is about to report it.
        }
    }

    /// <summary>
    ///     Awaits the encoder, turning a failure into one that names the cause.
    ///     <para>
    ///         When ffmpeg refuses its output the pipe breaks before FFMpegCore observes the exit, so the
    ///         raw fault is <c>IOException: Pipe is broken</c>. True, and useless. ffmpeg has already
    ///         said what was wrong on stderr, which <see cref="_stderrTail" /> is holding; a failure that
    ///         reads "Error opening output …: No such file or directory" is one a user can act on.
    ///     </para>
    /// </summary>
    private async Task AwaitEncoderAsync()
    {
        try
        {
            await _encoder!.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string tail = StderrTail();
            throw tail.Length == 0
                ? new FfmpegEncodeException($"ffmpeg failed: {ex.Message}", ex)
                : new FfmpegEncodeException($"ffmpeg failed: {tail}", ex);
        }
    }

    private string StderrTail()
    {
        lock (_stderrTail)
        {
            // Newest first: the last thing ffmpeg said before it died is the thing that explains it, and
            // the lines before it are the banner.
            return _stderrTail.Count == 0 ? string.Empty : string.Join(" | ", _stderrTail.Reverse());
        }
    }

    private void OnStderr(string line)
    {
        Volatile.Write(ref _lastStderrMs, NowMs());
        _options.Log?.Invoke(line);

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_stderrTail)
        {
            _stderrTail.Enqueue(line);
            while (_stderrTail.Count > StderrTailLines)
            {
                _stderrTail.Dequeue();
            }
        }
    }

    /// <summary>
    ///     Ends the frame stream when the encoder task ends, whatever ended it.
    ///     <para>
    ///         A cancelled encoder is left alone: the token that killed it is the caller's own, so the
    ///         writer is already observing it, and faulting here would race a clean
    ///         <see cref="OperationCanceledException" /> into a <see cref="ChannelClosedException" />.
    ///     </para>
    /// </summary>
    private static void EndFrameStream(Task encoder, object? state)
    {
        ChannelVideoFrameSource frames = (ChannelVideoFrameSource)state!;

        if (encoder.IsFaulted)
        {
            frames.Fault(encoder.Exception!.GetBaseException());
            return;
        }

        if (encoder.IsCompletedSuccessfully)
        {
            // Normally this is redundant: DisposeAsync completes the stream and only then awaits the
            // encoder. It matters when ffmpeg exits 0 on its own, before the render loop is finished.
            frames.Complete();
        }
    }

    /// <summary>
    ///     The exact ffmpeg argument line this sink would run, without starting anything. What the
    ///     argument tests assert against, and what a <c>--verbose</c> export logs.
    /// </summary>
    /// <param name="options">The settings to describe.</param>
    public static string DescribeArguments(FfmpegSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // One stub frame so RawVideoPipeSource can read the format and size it stamps into the input
        // arguments; it is never serialized because no process is started.
        StubFrame stub = new(options.Width, options.Height);
        return Build(options, new RawVideoPipeSource([stub]) { FrameRate = options.Fps }).Arguments;
    }

    private FFMpegArgumentProcessor BuildProcessor(CancellationToken ct) =>
        Build(_options, new RawVideoPipeSource(_frames) { FrameRate = _options.Fps })
            .CancellableThrough(ct)
            .NotifyOnError(OnStderr);

    private static FFMpegArgumentProcessor Build(FfmpegSinkOptions options, RawVideoPipeSource source)
    {
        RequireQuotablePath(options.OutputPath);

        return FFMpegArguments
            .FromPipeInput(source)
            .OutputToFile(options.OutputPath, true, arguments => Configure(arguments, options));
    }

    /// <summary>
    ///     Refuses an output path that cannot sit inside the quotes FFMpegCore puts round it.
    ///     <para>
    ///         FFMpegCore writes the path as <c>"path"</c>, escaping nothing, and the whole command line
    ///         goes to the process as one string. A double quote in the path closes that token early and
    ///         the rest of the name is read as ffmpeg options; a trailing backslash escapes the closing
    ///         quote and swallows the arguments after it. A double quote is a legal file-name character
    ///         on Linux and macOS, and a strat's name is the default file name.
    ///     </para>
    ///     <para>
    ///         Refused rather than escaped: the quoting belongs to FFMpegCore, and the escaping rules
    ///         (backslash runs before a quote) are the platform's argument parser's, not ours to guess.
    ///     </para>
    /// </summary>
    private static void RequireQuotablePath(string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (outputPath.Contains('"', StringComparison.Ordinal))
        {
            throw new ExportValidationException(
                "The output path contains a double quote, which cannot be passed to ffmpeg. Rename the file.");
        }

        if (outputPath.EndsWith('\\'))
        {
            throw new ExportValidationException(
                "The output path ends in a backslash, which cannot be passed to ffmpeg. Name a file.");
        }
    }

    private static void Configure(FFMpegArgumentOptions arguments, FfmpegSinkOptions options)
    {
        if (string.Equals(options.FormatId, ExportFormats.Gif, StringComparison.Ordinal))
        {
            // The standard SINGLE-input equivalent of the two-pass palettegen/paletteuse
            // recipe. A literal two-pass would need the input twice, and over a pipe that means
            // spilling a multi-gigabyte rawvideo temp file. There is no -c:v here and no ladder rung
            // to choose: the filter chain IS the encoder.
            arguments
                .WithCustomArgument(GifFilter(options))
                .Loop(0)
                .DisableChannel(Channel.Audio);
            return;
        }

        // Everything else is one rung of the ladder plus the arguments its quality maps to. The codec,
        // the rate control and the speed control all come from ONE place, so a sink cannot
        // disagree with what `export --json` says it used.
        EncoderSelection selection = options.ResolvedEncoder;

        arguments
            .WithVideoCodec(selection.Encoder.Name)
            .WithCustomArgument(selection.Arguments)
            .ForcePixelFormat(selection.Encoder.PixelFormat);

        if (string.Equals(options.FormatId, ExportFormats.Mp4, StringComparison.Ordinal))
        {
            // Moves the moov atom to the front so the file starts playing before it has all downloaded.
            // A container property, not an encoder one, which is why it survives the rung swap.
            arguments.WithFastStart();
        }

        // Every invocation, every format: this pipeline has no audio to carry, and letting ffmpeg guess
        // is how a container ends up with an empty audio stream.
        arguments.DisableChannel(Channel.Audio);
    }

    private static string GifFilter(FfmpegSinkOptions options)
    {
        string chain = string.Create(CultureInfo.InvariantCulture,
            $"[0:v]fps={options.Fps},scale={options.Width}:-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");
        return "-filter_complex \"" + chain + "\"";
    }

    private static FFOptions BuildOptions(FfmpegSinkOptions options) =>
        string.IsNullOrEmpty(options.BinaryFolder)
            ? new FFOptions()
            : new FFOptions { BinaryFolder = options.BinaryFolder };

    private void TryDeleteOutput()
    {
        try
        {
            if (File.Exists(_options.OutputPath))
            {
                File.Delete(_options.OutputPath);
            }
        }
        catch (IOException ex)
        {
            _options.Log?.Invoke($"could not remove the partial export: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _options.Log?.Invoke($"could not remove the partial export: {ex.Message}");
        }
    }

    /// <summary>A frame that exists only to tell FFMpegCore the stream's shape. Never serialized.</summary>
    private sealed class StubFrame(int width, int height) : IVideoFrame
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public string Format => "rgba";

        public void Serialize(Stream pipe) =>
            throw new NotSupportedException("The argument-describing stub frame is never written.");

        public Task SerializeAsync(Stream pipe, CancellationToken token) =>
            throw new NotSupportedException("The argument-describing stub frame is never written.");
    }
}

/// <summary>
///     An ffmpeg subprocess failed. The message carries ffmpeg's own last words rather than the pipe
///     error that usually races ahead of them.
/// </summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class FfmpegEncodeException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing copy, normally ffmpeg's stderr tail.</param>
    /// <param name="inner">The raw failure: usually the broken pipe, not the reason for it.</param>
    public FfmpegEncodeException(string message, Exception inner) : base(message, inner)
    {
    }

    /// <summary>Creates the exception.</summary>
    public FfmpegEncodeException() : base("The ffmpeg subprocess failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">User-facing copy.</param>
    public FfmpegEncodeException(string message) : base(message)
    {
    }
}
