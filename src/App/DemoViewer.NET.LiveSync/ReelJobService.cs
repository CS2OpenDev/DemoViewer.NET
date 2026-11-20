#region

using Avalonia.Threading;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.Capture;
using Cs2VideoGenerator.Core.Engine;
using Cs2VideoGenerator.Core.Models;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.LiveSync;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The reel-generation background job. One job at a time:
///     suspends an active live-sync session (the single-CS2 interlock: a reel needs
///     <c>initializeCapture:true</c>, sync sessions run capture-free), holds the
///     <see cref="HeavyJobGate" /> reel session for the whole duration (background scans pause;
///     interactive demo loads are refused with the clear message), runs the capture compilation
///     (or the macOS dry-run walk), then tears down and returns sync to its Reconnect prompt.
///     <para>
///         Construction is UI-free (headless-testable): the shell attaches the returned
///         <see cref="IReelJobService" /> to the Reel chip; log lines flow through the optional
///         sink. <see cref="StatusChanged" /> marshals to the UI thread when one exists.
///     </para>
/// </summary>
public sealed class ReelJobService(
    LiveSyncService? liveSync,
    HeavyJobGate? gate,
    IOptionsMonitor<AppSettings>? settings,
    Action<string>? log = null,
    Func<bool>? isCs2Running = null) : IReelJobService, IDisposable
{
    private readonly Lock _lifecycle = new();
    private CancellationTokenSource? _cts;
    private Task? _job;
    private ReelRequest? _lastRequest;

    public void Dispose()
    {
        lock (_lifecycle)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <inheritdoc />
    public ReelJobStatus Status { get; private set; } = ReelJobStatus.Idle;

    /// <inheritdoc />
    public event EventHandler<ReelJobStatus>? StatusChanged;

    /// <inheritdoc />
    public void Start(ReelRequest request)
    {
        lock (_lifecycle)
        {
            if (Status.IsRunning)
            {
                throw new InvalidOperationException("A highlight reel is already being generated.");
            }

            // Running BEFORE the job is scheduled: the guard above reads Status, and the job's own
            // first SetStatus runs later on the pool, so a second Start in that gap would pass.
            // Only the property is set here; the job's first SetStatus raises StatusChanged with
            // this same value, and raising it from here could land after a fast terminal status.
            Status = new ReelJobStatus(ReelJobPhase.StartingSession, 0, request.Clips.Count, null, null, null, []);
            _lastRequest = request;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;
            // No token on Task.Run: a cancel before scheduling would skip RunAsync entirely and
            // leave Status at StartingSession forever. RunAsync observes the token itself.
            _job = Task.Run(() => RunAsync(request, token));
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync()
    {
        Task? job;
        lock (_lifecycle)
        {
            _cts?.Cancel();
            job = _job;
        }

        if (job is not null)
        {
            try
            {
                await job.ConfigureAwait(false);
            }
            catch
            {
                // The job's own status carries the outcome.
            }
        }
    }

    /// <inheritdoc />
    public void RetryRemaining()
    {
        ReelRequest? retry;
        lock (_lifecycle)
        {
            if (Status.IsRunning || _lastRequest is null || !Status.HasRetryableClips)
            {
                return;
            }

            List<ReelClip> remaining =
            [
                .. Status.FailedClipIndices
                    .Where(i => i >= 0 && i < _lastRequest.Clips.Count)
                    .Select(i => _lastRequest.Clips[i])
            ];
            retry = remaining.Count == 0
                ? null
                : _lastRequest with { Clips = remaining };
        }

        if (retry is not null)
        {
            Start(retry);
        }
    }

    // ── The job ───────────────────────────────────────────────────────────────

    private async Task RunAsync(ReelRequest request, CancellationToken cancellationToken)
    {
        IDisposable? reelSlot = null;
        bool suspendedSync = false;
        CsvgWebHost? host = null;
        int completed = 0;
        List<int> failed = [];
        // The TERMINAL status publishes only after teardown below. IsRunning stays true until
        // the machine is actually free (gate released, session stopped), so the interlocks and
        // the chip don't report free until wind-down actually finishes.
        ReelJobStatus? terminal = null;
        try
        {
            SetStatus(new ReelJobStatus(ReelJobPhase.StartingSession, 0, request.Clips.Count, null, null, null, []));

            // Raises the reel flag AND waits out any background parse already mid-demo.
            // CS2+OBS on 16 GB must never overlap a multi-GB parse.
            if (gate is not null)
            {
                reelSlot = await gate.EnterReelSessionAsync(cancellationToken).ConfigureAwait(false);
            }

            // Single-CS2 interlock: stop a live sync session first; its chip shows
            // "Paused for reel render" throughout. OwnsSessionResources, not IsSessionActive:
            // a Faulted sync session still holds the gRPC host (and port 50051) for fast retry.
            // Parked even when sync was off: SuspendedForReel is what refuses Enable/Reconnect, and
            // an Enable mid-reel would fight the reel for port 50051, the install and the one CS2.
            bool syncHeldCs2 = false;
            if (liveSync is not null)
            {
                syncHeldCs2 = liveSync.State.IsSessionActive || liveSync.OwnsSessionResources;
                if (syncHeldCs2)
                {
                    log?.Invoke("Suspending live sync for the reel render.");
                }

                await liveSync.SuspendForReelAsync().ConfigureAwait(false);
                suspendedSync = true;
            }

            if (!request.DryRun && !OperatingSystem.IsWindows())
            {
                // The reel path captures through InEngineHooked (below), whose frame source is a
                // Windows DXGI swapchain present hook, Windows-only by construction.
                terminal = Status with
                {
                    Phase = ReelJobPhase.Failed,
                    Error = "Real reel generation needs Windows — the InEngineHooked capture "
                    + "provider streams frames from a DXGI present hook. "
                    + "Use \"Dry run (mock)\" to validate the clip plan on other platforms."
                };
                return;
            }

            LiveSyncSettings baseSettings = (settings?.CurrentValue ?? new AppSettings()).LiveSync;
            LiveSyncSettings effective = request.DryRun
                ? new LiveSyncSettings
                {
                    MockMode = true,
                    ExternalMockServerPath = baseSettings.ExternalMockServerPath,
                    TickOffset = baseSettings.TickOffset,
                    GameWindowWidth = baseSettings.GameWindowWidth,
                    GameWindowHeight = baseSettings.GameWindowHeight
                }
                : baseSettings;

            // Same pre-launch guard as Live Sync: a reel launches CS2 through the same CSVG install path. A
            // suspended sync session's CS2 can still be exiting here, and the guard refuses a running CS2.
            if (!request.DryRun)
            {
                if (syncHeldCs2)
                {
                    await InstallRecovery
                        .WaitForCs2ExitAsync(InstallRecovery.Cs2ExitTimeout, cancellationToken, isCs2Running)
                        .ConfigureAwait(false);
                }

                await Task.Run(() => InstallRecovery.EnsureRestoredBeforeLaunch(baseSettings, log, isCs2Running),
                    cancellationToken).ConfigureAwait(false);
            }

            host = await CsvgWebHost.StartAsync(effective,
                log is null
                    ? null
                    : new OutputLogBridge(
                        (_, category, message) => log($"{category}: {message}"),
                        // Transient job: read the live setting directly (no hot gRPC stream here).
                        () => LiveSyncService.ToMelLevel((settings?.CurrentValue ?? new AppSettings()).LiveSync
                            .MinimumLogLevel),
                        () => (settings?.CurrentValue ?? new AppSettings()).LiveSync.CaptureFrameworkLogs),
                cancellationToken,
                // Pin the capture backend for real reels (dry runs are playback-only and never
                // bring capture up). InEngineHooked: offline startmovie clock + DXGI present-hook
                // frames, ~2.7x realtime; requires the plugin's "present-capture" capability, which
                // its CheckAvailability surfaces if missing.
                request.DryRun ? null : CaptureProviderNames.InEngineHooked).ConfigureAwait(false);
            CsvgVideoSession session = host.Session;

            if (request.DryRun)
            {
                (completed, failed) = await DryRunAsync(session, request, effective.TickOffset, cancellationToken)
                    .ConfigureAwait(false);
                terminal = new ReelJobStatus(
                    failed.Count == 0 ? ReelJobPhase.Completed : ReelJobPhase.Failed,
                    completed, request.Clips.Count, null,
                    failed.Count == 0 ? null : $"{failed.Count} clip(s) failed the dry-run walk.",
                    null, failed);
                return;
            }

            // Capture geometry is EXPLICIT and shared: the InEngineHooked present hook grabs the CS2
            // swapchain, so ffmpeg has to be told the frame size up front (raw frames carry no header)
            // and it MUST equal the window CS2 renders at. Launch CS2 at these dims AND stamp the same
            // onto each clip's metadata (below). Precedence: the reel request's explicit resolution (the
            // Reels-tab picker) wins; else the persisted CS2 window size; else fall back to 1080p.
            int captureWidth = request.Width > 0
                ? request.Width
                : effective.GameWindowWidth > 0
                    ? effective.GameWindowWidth
                    : 1920;
            int captureHeight = request.Height > 0
                ? request.Height
                : effective.GameWindowHeight > 0
                    ? effective.GameWindowHeight
                    : 1080;

            await session.StartAsync(
                new EngineSessionOptions
                {
                    Width = captureWidth, Height = captureHeight, Fullscreen = effective.GameFullscreen
                },
                true, cancellationToken).ConfigureAwait(false);

            Cs2Compilation compilation = BuildCompilation(request, captureWidth, captureHeight, effective.TickOffset);
            IReadOnlyList<string> issues = compilation.Validate();
            if (issues.Count > 0)
            {
                terminal = Status with
                {
                    Phase = ReelJobPhase.Failed,
                    Error = "The reel plan failed validation: " + string.Join("; ", issues)
                };
                return;
            }

            session.CompilationClipStarted += OnClipStarted;
            session.CompilationClipCompleted += OnClipCompleted;
            try
            {
                Cs2CompilationResult result = await session
                    .RunCompilationAsync(compilation, cancellationToken).ConfigureAwait(false);
                terminal = CompilationTerminalStatus(request, result, cancellationToken.IsCancellationRequested,
                    request.Concatenate ? compilation.Settings.GetConcatenatedOutputPath() : request.OutputDirectory);
            }
            finally
            {
                session.CompilationClipStarted -= OnClipStarted;
                session.CompilationClipCompleted -= OnClipCompleted;
            }
        }
        catch (OperationCanceledException)
        {
            terminal = new ReelJobStatus(ReelJobPhase.Cancelled, completed, request.Clips.Count, null,
                "Cancelled.", null, UnfinishedIndices(request, completed, failed));
        }
        catch (Exception ex)
        {
            terminal = new ReelJobStatus(ReelJobPhase.Failed, completed, request.Clips.Count, null,
                ex.Message, null, UnfinishedIndices(request, completed, failed));
        }
        finally
        {
            if (host is not null)
            {
                try
                {
                    await host.Session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Teardown is best-effort; the install restore is CSVG's stop path.
                }

                await host.DisposeAsync().ConfigureAwait(false);
            }

            reelSlot?.Dispose();
            if (suspendedSync)
            {
                liveSync?.EndReelSuspension();
            }

            SetStatus(terminal ?? Status with
            {
                Phase = ReelJobPhase.Failed, Error = "The reel job ended without a result."
            });
        }

        // CSVG's clip events may run concurrently: ONE lock covers the count, the failed list,
        // its snapshot, and the _status read-modify-write (piecemeal hedging left the composite
        // racy: a snapshot during an Add throws, and RMWs could lose updates).
        Task OnClipStarted(int index, Cs2CompilationClip clip)
        {
            lock (failed)
            {
                SetStatus(Status with { Phase = ReelJobPhase.Capturing, CurrentClipLabel = ClipLabel(request, index) });
            }

            return Task.CompletedTask;
        }

        Task OnClipCompleted(int index, Cs2ClipResult result)
        {
            lock (failed)
            {
                if (result.Success)
                {
                    completed++;
                }
                else
                {
                    failed.Add(index);
                }

                SetStatus(Status with
                {
                    Phase = ReelJobPhase.Capturing, ClipsCompleted = completed, FailedClipIndices = [.. failed]
                });
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///     The dry-run walks the plan against the mock: load (grouped per demo) → spectate
    ///     → play range (playback-only via PlayTickRangeAsync, timeout derived from the clip's REAL
    ///     tick rate). This validates command plumbing and tick math without capture. The range API
    ///     converts failures into a failed result rather than throwing; failures accumulate per clip.
    /// </summary>
    private async Task<(int Completed, List<int> Failed)> DryRunAsync(
        CsvgVideoSession session, ReelRequest request, int tickOffset, CancellationToken cancellationToken)
    {
        await session.StartWatchAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        int completed = 0;
        List<int> failed = [];
        string? loadedDemo = null;
        for (int i = 0; i < request.Clips.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReelClip clip = request.Clips[i];
            SetStatus(Status with
            {
                Phase = ReelJobPhase.Capturing, CurrentClipLabel = clip.Label, ClipsCompleted = completed
            });

            try
            {
                if (!string.Equals(loadedDemo, clip.DemoPath, StringComparison.OrdinalIgnoreCase))
                {
                    await session.LoadDemoAsync(clip.DemoPath, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    loadedDemo = clip.DemoPath;
                }

                await session.Engine.SetSpectatorTargetAsync(clip.PlayerNameRaw, cancellationToken)
                    .ConfigureAwait(false);

                int rate = clip.TickRate > 0 ? clip.TickRate : 64;
                TimeSpan timeout = TimeSpan.FromSeconds((clip.EndTick - clip.StartTick) / (double)rate + 60);
                (int startTick, int endTick) = Cs2Range(clip, tickOffset);
                DemoPlaybackResult result = await session.PlayTickRangeAsync(
                        startTick, endTick, timeout, cancellationToken)
                    .ConfigureAwait(false);
                if (result.Success)
                {
                    completed++;
                }
                else
                {
                    failed.Add(i);
                    log?.Invoke($"Dry-run clip {i + 1} failed: {result.ErrorMessage ?? "unknown"}");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(i);
                log?.Invoke($"Dry-run clip {i + 1} failed: {ex.Message}");
            }
        }

        return (completed, failed);
    }

    /// <summary>
    ///     THE emission boundary: a <see cref="ReelClip" /> window is FRAME CLOCK (the packaged clip
    ///     planner leaves it there), and the <c>TickOffset</c> shim converts it into CS2 demo-tick
    ///     space here, exactly once, on every path that reaches CS2 (the real compilation and the
    ///     dry-run walk both come through this method, so the two can never disagree).
    /// </summary>
    public static (int StartTick, int EndTick) Cs2Range(ReelClip clip, int tickOffset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return (checked((int)(clip.StartTick + tickOffset)), checked((int)(clip.EndTick + tickOffset)));
    }

    public static Cs2Compilation BuildCompilation(ReelRequest request, int width, int height, int tickOffset) => new()
    {
        Settings = new Cs2CompilationSettings
        {
            OutputDirectory = request.OutputDirectory,
            BaseFileName = request.BaseFileName,
            ContainerFormat = request.ContainerFormat,
            FrameRate = request.Fps,
            // Explicit geometry the in-engine (present-hook) backend requires; propagated to each
            // clip's CaptureMetadata. Matches the CS2 window dims StartAsync launches at.
            Width = width,
            Height = height,
            CaptureAudio = request.CaptureAudio,
            ConcatenateClips = request.Concatenate,
            Encoding = request.Crf is null && request.VideoBitrate is null
                ? null
                : new Cs2EncodingSettings { Crf = request.Crf, VideoBitrate = request.VideoBitrate }
        },
        Clips =
        [
            .. request.Clips.Select(clip => new Cs2CompilationClip
            {
                PlayerSteamId = clip.PlayerSteamId64,
                MatchChecksum = clip.DemoSha256 ?? $"{clip.DemoPath}:{clip.StartTick}",
                DemoFilePath = clip.DemoPath,
                StartTick = Cs2Range(clip, tickOffset).StartTick,
                EndTick = Cs2Range(clip, tickOffset).EndTick,
                PlayerNameToSpectate = clip.PlayerNameRaw,
                ClipOptions = request.NoHudPreset ? Cs2ClipOptions.NoHudDefault : Cs2ClipOptions.Default
            })
        ]
    };

    /// <summary>
    ///     The real reel's terminal status. CSVG's runner is fail-fast and never throws: the first
    ///     failed clip, a demo that fails to load and a cancellation all come back as a Failed result
    ///     holding only the clips it started. So the retryable set is every requested clip without a
    ///     successful result (matched by <see cref="Cs2ClipResult.ClipIndex" />, because the runner
    ///     records grouped by demo and start tick, not in request order), and the token decides
    ///     Cancelled. <see cref="UnfinishedIndices" />' contiguous-tail assumption does not hold here.
    /// </summary>
    internal static ReelJobStatus CompilationTerminalStatus(
        ReelRequest request, Cs2CompilationResult result, bool cancelled, string? outputPath)
    {
        HashSet<int> captured = [.. result.ClipResults.Where(r => r.Success).Select(r => r.ClipIndex)];
        List<int> unfinished = [.. Enumerable.Range(0, request.Clips.Count).Where(i => !captured.Contains(i))];
        if (cancelled)
        {
            return new ReelJobStatus(ReelJobPhase.Cancelled, captured.Count, request.Clips.Count, null,
                "Cancelled.", null, unfinished);
        }

        string? error = null;
        if (!result.Success)
        {
            error = $"{unfinished.Count} clip(s) not captured.";
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                error += " " + result.ErrorMessage;
            }
        }

        return new ReelJobStatus(
            result.Success ? ReelJobPhase.Completed : ReelJobPhase.Failed,
            captured.Count, request.Clips.Count, null,
            error,
            outputPath,
            unfinished);
    }

    private static string ClipLabel(ReelRequest request, int index) =>
        index >= 0 && index < request.Clips.Count ? request.Clips[index].Label : $"clip {index + 1}";

    private static List<int> UnfinishedIndices(ReelRequest request, int completed, List<int> failed)
    {
        // Everything not confirmed completed is retryable: recorded failures plus never-started.
        HashSet<int> failedSet = [.. failed];
        List<int> unfinished = [.. failedSet];
        int accountedFor = completed + failedSet.Count;
        for (int i = accountedFor; i < request.Clips.Count; i++)
        {
            unfinished.Add(i);
        }

        return unfinished;
    }

    private void SetStatus(ReelJobStatus next)
    {
        Status = next;
        if (Dispatcher.UIThread.CheckAccess())
        {
            StatusChanged?.Invoke(this, next);
        }
        else
        {
            Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(this, next));
        }
    }
}
