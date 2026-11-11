#region

using System.Globalization;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     The download half the two Settings rows share: one cancellable <see cref="ExtensionUpdateService.DownloadAndStageAsync" />
///     at a time, its progress, and the line it shows. The rows keep the bound properties.
/// </summary>
internal sealed class ExtensionDownload : IDisposable
{
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Stages <paramref name="entry" />; <paramref name="report" /> gets (received, total) on the caller's context.</summary>
    public async Task<StageResult> RunAsync(ExtensionUpdateService service, ExtensionFeedEntry entry,
        Action<long, long> report)
    {
        _cts = new CancellationTokenSource();
        try
        {
            Progress<ExtensionDownloadProgress> progress = new(p => report(p.BytesReceived, p.TotalBytes));
            return await service.DownloadAndStageAsync(entry, progress, _cts.Token);
        }
        finally
        {
            CancellationTokenSource finished = _cts;
            _cts = null;
            finished.Dispose();
        }
    }

    /// <summary>Stops the running download; nothing is left on disk.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Stops a download the window's closing leaves behind.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
    }

    /// <summary>0 to 100.</summary>
    public static int Percent(long received, long total) =>
        total <= 0 ? 0 : (int)Math.Min(100, received * 100 / total);

    /// <summary>The line a row shows while it downloads.</summary>
    public static string Line(string version, long received, long total) =>
        $"Downloading {version}: {Megabytes(received)} of {Megabytes(total)} MB";

    /// <summary>Bytes as megabytes with one decimal, the form every download line uses.</summary>
    public static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
}
