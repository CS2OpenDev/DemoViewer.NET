#region

using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.ViewModels.Transfer;

/// <summary>
///     What the export and import wizards share: the run itself (off the UI thread, progress posted back),
///     cancellation, the ask-before-closing prompt while a run is going, and the summary on the Done step.
/// </summary>
public abstract partial class TransferWizardViewModel : ViewModelBase
{
    /// <summary>The D4 warning: demos the queue is still working on may be left out of the transfer.</summary>
    public const string InFlightWarningText =
        "Some demos are still being processed. Those demos MAY not be included: the transfer takes each demo "
        + "as it stands when it gets to it, and the summary lists any it had to skip.";

    private readonly Func<int>? _demosInFlight;
    private readonly Action<string>? _openFolder;
    private readonly Action<Action> _post;

    private bool _closeAfterRun;
    private CancellationTokenSource? _cts;

    /// <summary>Set when a run ends; the Done step reads from it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    [NotifyPropertyChangedFor(nameof(SummaryHeadline))]
    [NotifyPropertyChangedFor(nameof(SummaryLines))]
    [NotifyPropertyChangedFor(nameof(HasSummaryLines))]
    [NotifyPropertyChangedFor(nameof(CanOpenFolder))]
    private TransferSummary? _summary;

    [ObservableProperty]
    private string? _currentDemo;

    /// <summary>A failure the current step should show, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    /// <summary>Shown when the user tries to close the window while a run is going.</summary>
    [ObservableProperty]
    private bool _isCancelPromptOpen;

    /// <summary>True from the start of a run until it has ended, however it ended.</summary>
    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _progressText = "";

    /// <summary>Storage provider from the view, for the folder pickers. Null headless and in tests.</summary>
    protected IStorageProvider? StorageProvider { get; private set; }

    /// <param name="post">Runs an action on the UI thread. Defaults to the Avalonia dispatcher.</param>
    /// <param name="demosInFlight">How many demos the processing queue holds right now (for the D4 warning).</param>
    /// <param name="openFolder">Opens a folder in the OS file manager, or null where there is none.</param>
    protected TransferWizardViewModel(Action<Action>? post, Func<int>? demosInFlight, Action<string>? openFolder)
    {
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _demosInFlight = demosInFlight;
        _openFolder = openFolder;
    }

    /// <summary>The window title.</summary>
    public abstract string Title { get; }

    /// <summary>True when the queue is working on demos now. Re-read whenever a step that shows it opens.</summary>
    public bool DemosInFlight => _demosInFlight?.Invoke() > 0;

    /// <summary>True when <see cref="ErrorText" /> has something to show.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>True once a run has ended.</summary>
    public bool HasSummary => Summary is not null;

    /// <summary>The Done step's headline.</summary>
    public string SummaryHeadline => Summary is null ? "" : Headline(Summary);

    /// <summary>One line per demo that did not go through cleanly, then one per warning.</summary>
    public IReadOnlyList<string> SummaryLines => Summary is null ? [] : Lines(Summary);

    /// <summary>True when <see cref="SummaryLines" /> is not empty.</summary>
    public bool HasSummaryLines => SummaryLines.Count > 0;

    /// <summary>Open folder is offered once there is a folder on disk to show.</summary>
    public bool CanOpenFolder => _openFolder is not null && SummaryFolder is { Length: > 0 } f && Directory.Exists(f);

    /// <summary>The folder the Done step's Open folder button shows.</summary>
    protected virtual string? SummaryFolder => Summary?.BundleFolder;

    /// <summary>The wizard is done with; the host closes the window.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Hands the view's storage provider to the folder pickers.</summary>
    public void SetStorageProvider(IStorageProvider? provider)
    {
        StorageProvider = provider;
        OnPropertyChanged(nameof(CanPickFolder));
    }

    /// <summary>Whether a folder picker is wired (false headless and on the browser).</summary>
    public bool CanPickFolder => StorageProvider is not null;

    /// <summary>Shows the OS folder picker and returns the local path picked, or null.</summary>
    protected async Task<string?> PickFolderAsync(string title)
    {
        if (StorageProvider is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>
    ///     Asks to close. True when the window may close now. While a run is going it opens the cancel prompt
    ///     and returns false; the window then closes through <see cref="CloseRequested" /> once the user
    ///     cancels and the run has stopped.
    /// </summary>
    public bool TryClose()
    {
        if (!IsRunning)
        {
            return true;
        }

        IsCancelPromptOpen = true;
        return false;
    }

    [RelayCommand]
    private void Close()
    {
        if (TryClose())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The Progress step's Cancel button: stops the run and stays open on the summary.</summary>
    [RelayCommand]
    private void CancelRun() => _cts?.Cancel();

    /// <summary>The cancel prompt's "Cancel and close".</summary>
    [RelayCommand]
    private void ConfirmCancelAndClose()
    {
        IsCancelPromptOpen = false;
        if (!IsRunning)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _closeAfterRun = true;
        _cts?.Cancel();
    }

    /// <summary>The cancel prompt's "Keep going".</summary>
    [RelayCommand]
    private void DismissCancelPrompt() => IsCancelPromptOpen = false;

    [RelayCommand]
    private void OpenFolder()
    {
        if (SummaryFolder is { Length: > 0 } folder)
        {
            _openFolder?.Invoke(folder);
        }
    }

    /// <summary>
    ///     Runs <paramref name="work" /> on the thread pool, posting its progress to the UI thread, and stores
    ///     the summary. A cancel or a failure still ends with a summary, so the Done step always has one.
    /// </summary>
    protected async Task RunTransferAsync(string folder,
        Func<IProgress<TransferProgress>, CancellationToken, Task<TransferSummary>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using CancellationTokenSource cts = new();
        _cts = cts;
        _closeAfterRun = false;
        ErrorText = null;
        Summary = null;
        ProgressFraction = 0;
        ProgressText = "Starting…";
        CurrentDemo = null;
        IsRunning = true;

        PostingProgress progress = new(this);
        TransferSummary summary;
        try
        {
            summary = await Task.Run(() => work(progress, cts.Token), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            summary = new TransferSummary([], [], folder, Cancelled: true);
        }
#pragma warning disable CA1031 // any failure ends on the Done step with its message, never with a dead wizard
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ex.Message;
            summary = new TransferSummary([],
                [new TransferWarning(TransferWarningKind.Other, ex.Message, Blocking: true)], folder);
        }
        finally
        {
            _cts = null;
            progress.Stop();
            IsRunning = false;
            IsCancelPromptOpen = false;
        }

        Summary = summary;
        ProgressFraction = summary.Cancelled ? ProgressFraction : 1;
        OnRunFinished();

        if (_closeAfterRun)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Moves to the Done step.</summary>
    protected abstract void OnRunFinished();

    /// <summary>The verb for the headline, "exported" or "imported".</summary>
    protected abstract string DoneVerb { get; }

    private string Headline(TransferSummary s)
    {
        if (s.Cancelled)
        {
            return s.Items.Count == 0
                ? "Cancelled. Nothing more was copied."
                : $"Cancelled after {Plural(s.Transferred.Count + s.Merged.Count, "demo")} {DoneVerb}.";
        }

        if (s.Items.Count == 0 && s.Warnings.Any(w => w.Blocking))
        {
            return "The transfer did not finish.";
        }

        string text = $"{Plural(s.Transferred.Count, "demo")} {DoneVerb}";
        if (s.Merged.Count > 0)
        {
            text += $", {s.Merged.Count} merged";
        }

        int other = s.Skipped.Count + s.Stale.Count + s.Failed.Count;
        return other == 0 ? text + "." : text + $", {other} with notes below.";
    }

    private static List<string> Lines(TransferSummary s)
    {
        List<string> lines = [];
        lines.AddRange(s.Failed.Select(i => Line("Failed", i)));
        lines.AddRange(s.Skipped.Select(i => Line("Skipped", i)));
        lines.AddRange(s.Stale.Select(i => Line("Will be re-analysed", i)));
        lines.AddRange(s.Warnings.Select(w => w.Message));
        return lines;
    }

    private static string Line(string what, TransferSummaryItem item) =>
        string.IsNullOrWhiteSpace(item.Reason)
            ? $"{what}: {item.DisplayName}"
            : $"{what}: {item.DisplayName} ({item.Reason})";

    /// <summary>"1 demo", "3 demos".</summary>
    protected static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>Bytes for display, the library's own format.</summary>
    protected static string Bytes(long bytes) => DemoEntry.FormatSize(Math.Max(0, bytes));

    private void ApplyProgress(TransferProgress p)
    {
        ProgressFraction = p.Fraction;
        ProgressText = p.BytesTotal > 0
            ? $"{p.DemosDone} of {p.DemosTotal} demos · {Bytes(p.BytesDone)} of {Bytes(p.BytesTotal)}"
            : $"{p.DemosDone} of {p.DemosTotal} demos";
        CurrentDemo = p.Current;
    }

    // Posts each report to the UI thread. Reports that land after the run ended are dropped, so a late one
    // cannot rewind the bar on the Done step.
    private sealed class PostingProgress(TransferWizardViewModel owner) : IProgress<TransferProgress>
    {
        private volatile bool _stopped;

        public void Report(TransferProgress value)
        {
            if (_stopped)
            {
                return;
            }

            owner._post(() =>
            {
                if (!_stopped)
                {
                    owner.ApplyProgress(value);
                }
            });
        }

        public void Stop() => _stopped = true;
    }
}
