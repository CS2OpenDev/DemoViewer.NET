namespace DemoViewer.NET.Playback2D.Pipeline.Export;

/// <summary>
///     Where rendered frames go.
///     <para>
///         One implementation pipes raw RGBA to an ffmpeg subprocess (<c>FfmpegFrameSink</c>), one
///         accumulates an animated GIF with no ffmpeg at all (<c>ManagedGifSink</c>), and one hashes and
///         forwards (<c>HashingFrameSink</c>, the determinism harness). All three live in Pipeline, which
///         is why this seam is the whole contract Core knows about encoding.
///     </para>
///     <para>
///         <b>The buffer is borrowed.</b> <see cref="WriteAsync" />'s span is pooled and reused by the
///         session on the very next frame: a sink that needs the bytes past the returned task must copy
///         them. <see cref="IAsyncDisposable.DisposeAsync" /> is where a sink finalises its output, so it
///         must be awaited, and it is called exactly once, including on cancellation.
///     </para>
/// </summary>
public interface IFrameSink : IAsyncDisposable
{
    /// <summary>Accepts one rendered frame.</summary>
    /// <param name="rgba">Row-major RGBA8888, <c>width * height * 4</c> bytes. Valid only until the task completes.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="ct">Cancels the write; the sink is still disposed afterwards.</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> rgba, int width, int height, CancellationToken ct);
}

/// <summary>
///     A sink that can be told its run has failed, before it is disposed.
///     <para>
///         Disposal is where a sink finalises its output, and it is called on every path. A sink that
///         cannot tell a finished run from a failed one finalises both: a render failure half way
///         through leaves a shorter file that plays normally at the path of a job reported Failed.
///     </para>
///     <para>
///         Internal, and separate from <see cref="IFrameSink" />: that interface is published, and a
///         caller's own sink owes this nothing. <c>SceneExportSession</c> asks for it by type.
///     </para>
/// </summary>
internal interface IAbortableFrameSink
{
    /// <summary>Marks the run failed or cancelled, so the following disposal leaves no output behind.</summary>
    void Abort();
}

/// <summary>
///     A sink that can say whether it has opened its output file.
///     <para>
///         Most sinks open the file no later than their first frame. One that holds its frames and opens
///         the file only when it is disposed has, until then, nothing at the path that is its own, however
///         many frames it took. <c>PackExporter</c> asks for this by type before it removes the file of a
///         clip that failed. Internal for the reason <see cref="IAbortableFrameSink" /> is.
///     </para>
/// </summary>
internal interface IOutputOpeningFrameSink
{
    /// <summary>True once the sink has opened, and so truncated, the file at its output path.</summary>
    bool OutputOpened { get; }
}
