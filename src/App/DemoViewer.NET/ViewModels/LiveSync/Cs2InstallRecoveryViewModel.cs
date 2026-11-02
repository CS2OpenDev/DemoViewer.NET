#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2DemoKit.Analysis.Diagnostics;
using DemoViewer.NET.Services.LiveSync;
using DemoViewer.NET.ViewModels.Diagnostics;
using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.ViewModels.LiveSync;

/// <summary>
///     The shell banner for a CS2 install that a crashed Live Sync or Reels session left modified. It lives
///     on the shell rather than on the Live Sync chip because the chip is developer-only by default while
///     Reels are on for every profile; the chip's own offer mirrors this one.
///     <para>
///         Probes at startup and again each time CS2 is released (Live Sync back to Off, a reel finished).
///         Leftovers the engine proves safe to undo are restored unasked and reported; anything else holds
///         the banner, with no dismiss, until it is restored. While a session or reel owns CS2 the banner
///         stands down: the engine's pre-launch guard owns the install then.
///     </para>
/// </summary>
public sealed partial class Cs2InstallRecoveryViewModel : ViewModelBase, IDisposable
{
    internal const string LeftoverText =
        "A Live Sync or Reels session didn't finish cleanly and left DemoViewer's changes in your CS2 install. "
        + "Restore it before you next play CS2.";

    internal const string Cs2RunningText =
        "A Live Sync or Reels session didn't finish cleanly and left DemoViewer's changes in your CS2 install. "
        + "Close CS2, then restore it.";

    internal const string AutoRestoredText =
        "DemoViewer undid the changes an unfinished Live Sync or Reels session left in your CS2 install. "
        + "Your CS2 settings were not changed.";

    internal const string RestoredText = "Your CS2 install is restored.";

    internal const string StillRunningText =
        "CS2 is still running. Close it, then select Restore my CS2 install again.";

    private readonly Func<bool> _isCs2Busy;
    private readonly ILiveSyncService _liveSync;
    private ILogger? _diagLog;
    private bool _disposed;

    [ObservableProperty]
    private string? _failureText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    private bool _isCs2Running;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _isRestoring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Message), nameof(CanDismiss))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private bool _needsRestore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Message), nameof(CanDismiss))]
    private string? _notice;

    private bool _wasBusy;

    /// <param name="liveSync">The engine: its probe and restore run against the real install.</param>
    /// <param name="isCs2Busy">True while a Live Sync session or a reel owns CS2 and the install with it.</param>
    public Cs2InstallRecoveryViewModel(ILiveSyncService liveSync, Func<bool> isCs2Busy)
    {
        ArgumentNullException.ThrowIfNull(liveSync);
        ArgumentNullException.ThrowIfNull(isCs2Busy);
        _liveSync = liveSync;
        _isCs2Busy = isCs2Busy;
        _liveSync.StateChanged += OnLiveSyncStateChanged;
    }

    private ILogger DiagLog => _diagLog ??= DiagnosticsLog.CreateLogger("App.LiveSync");

    /// <summary>True while the banner shows: leftovers to restore, or the outcome of a restore.</summary>
    public bool IsVisible => NeedsRestore || Notice is not null;

    /// <summary>The banner sentence.</summary>
    public string Message => NeedsRestore ? IsCs2Running ? Cs2RunningText : LeftoverText : Notice ?? "";

    /// <summary>Only a finished restore can be dismissed; leftovers hold the banner until they are gone.</summary>
    public bool CanDismiss => !NeedsRestore && Notice is not null;

    private bool CanRestore => NeedsRestore && !IsRestoring;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _liveSync.StateChanged -= OnLiveSyncStateChanged;
    }

    /// <summary>
    ///     Probes the install and acts on it: nothing, an unattended restore, or the banner. A no-op while
    ///     CS2 is busy or a restore is running. Detection is best-effort, so a probe error shows nothing.
    /// </summary>
    internal async Task CheckAsync()
    {
        if (_disposed || IsRestoring || _isCs2Busy())
        {
            return;
        }

        Cs2InstallProbe probe;
        try
        {
            probe = await _liveSync.ProbeInstallAsync().ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        // A session may have started, or the user restored by hand, while the probe was out.
        if (_disposed || IsRestoring || _isCs2Busy())
        {
            return;
        }

        IsCs2Running = probe.Cs2Running;
        if (!probe.HasLeftovers)
        {
            NeedsRestore = false;
            FailureText = null;
            return;
        }

        if (probe.SafeToAutoRestore)
        {
            IsRestoring = true;
            try
            {
                await _liveSync.RestoreInstallAsync().ConfigureAwait(true);
                NeedsRestore = false;
                FailureText = null;
                Notice = AutoRestoredText;
                return;
            }
            catch (Exception ex)
            {
                AppLog.OperationFailed(DiagLog, "restore the CS2 install automatically", ex);
                FailureText = Describe(ex);
            }
            finally
            {
                IsRestoring = false;
            }
        }

        Notice = null;
        NeedsRestore = true;
    }

    /// <summary>
    ///     Re-evaluates against who owns CS2: stands the banner down while it is busy and probes again
    ///     once it is released. The shell calls this on every reel status change.
    /// </summary>
    internal void Reevaluate()
    {
        if (_isCs2Busy())
        {
            _wasBusy = true;
            NeedsRestore = false;
            return;
        }

        if (_wasBusy)
        {
            _wasBusy = false;
            _ = CheckAsync();
        }
    }

    private void OnLiveSyncStateChanged(object? sender, LiveSyncStateChangedEventArgs e) => Reevaluate();

    /// <summary>The one-click restore, with the outcome in the banner either way.</summary>
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore()
    {
        IsRestoring = true;
        FailureText = null;
        try
        {
            Cs2InstallProbe probe = await _liveSync.ProbeInstallAsync().ConfigureAwait(true);
            IsCs2Running = probe.Cs2Running;
            if (probe.HasLeftovers)
            {
                if (probe.Cs2Running)
                {
                    FailureText = StillRunningText;
                    return;
                }

                await _liveSync.RestoreInstallAsync().ConfigureAwait(true);
            }

            NeedsRestore = false;
            Notice = RestoredText;
        }
        catch (Exception ex)
        {
            AppLog.OperationFailed(DiagLog, "restore the CS2 install", ex);
            FailureText = Describe(ex);
        }
        finally
        {
            IsRestoring = false;
        }
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (CanDismiss)
        {
            Notice = null;
            FailureText = null;
        }
    }

    // The fallback names Steam's own repair rather than the csvg CLI: it is the one every profile has, and
    // it puts back an unpatched gameinfo.gi, which is what stops the plugin loading.
    internal static string Describe(Exception ex)
    {
        string reason = ex switch
        {
            UnauthorizedAccessException => "access to a file in the CS2 folder was denied",
            FileNotFoundException => "the backup DemoViewer made of gameinfo.gi is missing",
            IOException => "a file in the CS2 folder is in use",
            _ => "some of the changes were still there afterwards"
        };

        return $"Couldn't restore your CS2 install: {reason}. Close CS2 and try again. If it still fails, "
               + "use Verify integrity of game files on Counter-Strike 2 in Steam.";
    }
}
