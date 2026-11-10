#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     One entry of "Available extensions" in Settings: a first-party extension this build can install but
///     does not have. Name, version and size with an Install button while one is offered; the download's
///     progress and Cancel while it runs; the restart line once a copy is on disk; and, for a copy on disk this
///     launch refused, the reason and Remove.
/// </summary>
public sealed partial class InstallableExtensionRow : ObservableObject, IDisposable
{
    private readonly ExtensionUpdateService _service;
    private CancellationTokenSource? _download;
    private bool _disposed;

    /// <summary>The last catalogue check's entry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(Version))]
    [NotifyPropertyChangedFor(nameof(Size))]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private InstallableExtension _entry;

    /// <summary>True while a download is in flight; the row shows progress and a Cancel button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    private bool _isDownloading;

    /// <summary>Bytes received so far, while downloading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private long _bytesReceived;

    /// <summary>The download's size, from the feed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private long _totalBytes;

    /// <summary>Why the last install was refused, in user terms; null when it was not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    private string? _failure;

    /// <summary>True once Remove was pressed; the folder goes at the next start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isRemoved;

    /// <param name="entry">The catalogue entry.</param>
    /// <param name="service">The updater.</param>
    internal InstallableExtensionRow(InstallableExtension entry, ExtensionUpdateService service)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(service);
        _entry = entry;
        _service = service;
        _isRemoved = service.IsMarkedForRemoval(entry.Id);
    }

    /// <summary>The extension id.</summary>
    public string Id => Entry.Id;

    /// <summary>The extension's name.</summary>
    public string Name => Entry.Name;

    /// <summary>The version Install would fetch, else the one on disk, else the latest published; empty when none.</summary>
    public string Version =>
        Entry.Offered?.Version.ToString() ?? Entry.Staged?.ToString() ?? Entry.Rejected?.Version?.ToString()
        ?? Entry.Latest?.Version.ToString() ?? string.Empty;

    /// <summary>The offered zip's size, "4.5 MB"; empty when nothing is offered.</summary>
    public string Size => Entry.Offered is { } offered ? $"{ExtensionUpdateRow.Megabytes(offered.Size)} MB" : string.Empty;

    /// <summary>The Install button shows while a version is offered and nothing is in flight.</summary>
    public bool CanInstall => !IsDownloading && !IsRemoved && Entry.Status == InstallableStatus.Available;

    /// <summary>Remove shows for a copy on disk this launch refused, so a broken install is never stuck.</summary>
    public bool CanRemove => !IsDownloading && !IsRemoved && Entry.Status == InstallableStatus.NotLoaded;

    /// <summary>Whether <see cref="Failure" /> is set.</summary>
    public bool HasFailure => Failure is not null;

    /// <summary>Whether <see cref="Message" /> has anything to show.</summary>
    public bool HasMessage => Message.Length > 0;

    /// <summary>0 to 100 while downloading.</summary>
    public int ProgressPercent => TotalBytes <= 0 ? 0 : (int)Math.Min(100, BytesReceived * 100 / TotalBytes);

    /// <summary>The line under the name: one sentence per state.</summary>
    public string Message
    {
        get
        {
            if (IsRemoved)
            {
                return ExtensionUpdateRow.RemovedMessage;
            }

            if (IsDownloading)
            {
                string version = Entry.Offered?.Version.ToString() ?? string.Empty;
                return
                    $"Downloading {version}: {ExtensionUpdateRow.Megabytes(BytesReceived)} of {ExtensionUpdateRow.Megabytes(TotalBytes)} MB";
            }

            return Describe(Entry);
        }
    }

    /// <summary>The sentence for <paramref name="entry" />, the same one the row shows.</summary>
    public static string Describe(InstallableExtension entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Status switch
        {
            InstallableStatus.Available => string.Empty,
            InstallableStatus.PendingRestart => $"{entry.Staged} installed, restart to use it.",
            InstallableStatus.NotLoaded => entry.Rejected!.UserMessage,
            InstallableStatus.NeedsNewerApp => ExtensionUpdateRow.NeedsNewerApp(entry.Latest, entry.LatestCompatibility),
            InstallableStatus.NotPublished => "Nothing has been published yet.",
            InstallableStatus.FeedUnreachable => $"Could not check: {entry.Error}.",
            InstallableStatus.FeedInvalid => $"Could not check: {entry.Error}.",
            _ => string.Empty
        };
    }

    /// <summary>Replaces the entry, as a finished check does.</summary>
    internal void Apply(InstallableExtension entry)
    {
        Failure = null;
        Entry = entry;
    }

    /// <summary>Downloads and stages the offered version; the next start loads it.</summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsDownloading || Entry.Offered is not { } offered)
        {
            return;
        }

        IsDownloading = true;
        Failure = null;
        BytesReceived = 0;
        TotalBytes = offered.Size;
        _download = new CancellationTokenSource();
        try
        {
            Progress<ExtensionDownloadProgress> progress = new(p =>
            {
                BytesReceived = p.BytesReceived;
                TotalBytes = p.TotalBytes;
            });
            StageResult result = await _service.DownloadAndStageAsync(offered, progress, _download.Token);
            switch (result.Outcome)
            {
                case StageOutcome.Installed:
                case StageOutcome.AlreadyInstalled:
                    Entry = Entry with
                    {
                        Status = InstallableStatus.PendingRestart, Staged = offered.Version, Offered = null
                    };
                    break;
                case StageOutcome.Refused:
                    Failure = $"{Entry.Name} {offered.Version} could not be installed: {result.Detail}.";
                    break;
                case StageOutcome.Cancelled:
                    break;
            }
        }
        finally
        {
            CancellationTokenSource finished = _download;
            _download = null;
            finished.Dispose();
            IsDownloading = false;
        }
    }

    /// <summary>Stops the download; nothing is left on disk.</summary>
    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    /// <summary>Marks the refused copy for removal; its folder goes at the next start, or now when nothing of it loaded.</summary>
    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (!CanRemove)
        {
            return;
        }

        if (await _service.RemoveInstalledAsync(Id))
        {
            IsRemoved = true;
        }
    }

    /// <summary>Stops a download the window's closing leaves behind.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _download?.Cancel();
    }
}
