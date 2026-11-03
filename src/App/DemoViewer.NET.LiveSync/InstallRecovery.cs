#region

using System.Diagnostics;
using Cs2VideoGenerator.Core;
using Cs2VideoGenerator.Core.DependencyInjection;
using Cs2VideoGenerator.Core.Services;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.LiveSync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.LiveSync;

/// <summary>
///     Crash recovery for the CS2 install. A DV crash with a
///     live session skips <c>ShutdownRequested</c>, so CSVG's stop-time restore never runs and the
///     install stays modified: <c>gameinfo.gi</c> keeps the injected <c>Game csgo/csvg</c> search
///     path (the plugin loads on every NORMAL CS2 launch) and the plugin files stay under
///     <c>game/csgo/csvg/</c>. Live Sync and Reels both launch CS2 through CSVG's one process
///     manager, so either leaves the same signature and this one probe covers both.
///     <para>
///         Detection is CONTENT-based, not backup-index-based: CSVG deliberately keeps backups
///         after a clean stop (redundancy; pruned by age), so "backups exist" is normal — the
///         crash signature is the marker still present in <c>gameinfo.gi</c> and/or the plugin
///         directory still existing. Restore un-patches <c>gameinfo.gi</c> from CSVG's own backup
///         and removes the plugin files. User-settings backups are deliberately NOT restored
///         here: they snapshot the crashed session's start, and blindly restoring them could
///         clobber settings the user changed since — CSVG's next session start self-heals their
///         hygiene, and <c>csvg restore</c> is the manual full-restore fallback.
///     </para>
///     <para>
///         Both operations run against a short-lived CSVG service container (no Kestrel, no
///         session) so install discovery, backup layout, and uninstall logic stay CSVG's own —
///         nothing is reimplemented DV-side. Mock mode has no real files: both no-op.
///     </para>
/// </summary>
internal static class InstallRecovery
{
    /// <summary>The exact search-path line CSVG's <c>ModifyGameInfoFile</c> injects.</summary>
    private const string GameInfoMarker = "Game\tcsgo/csvg";

    /// <summary>
    ///     Serializes every restore. The banner's unattended restore and the pre-launch guard run on different
    ///     threads and would otherwise both rewrite gameinfo.gi and delete the plugin directory at once.
    /// </summary>
    private static readonly object RestoreGate = new();

    /// <summary>How long a launch waits for the CS2 it just stopped to exit before the guard runs.</summary>
    internal static readonly TimeSpan Cs2ExitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Probes the real CS2 install for leftover CSVG modifications. Null when nothing is
    ///     determinable — mock mode, no CS2 install found, or unreadable files — which callers
    ///     must treat as "nothing to offer", never as an error.
    /// </summary>
    public static LeftoverState? Detect(LiveSyncSettings settings, Func<bool>? isCs2Running = null)
    {
        if (IsMock(settings))
        {
            return null;
        }

        try
        {
            using ServiceProvider services = BuildCsvgServices(settings);
            Cs2DirectoryUtil paths = services.GetRequiredService<Cs2DirectoryUtil>();
            string gameInfoPath = paths.Cs2GameInfoFilePath;
            if (!File.Exists(gameInfoPath))
            {
                return null;
            }

            string gameInfo = File.ReadAllText(gameInfoPath);
            bool patched = gameInfo.Contains(GameInfoMarker, StringComparison.Ordinal);
            bool pluginPresent = Directory.Exists(paths.CsvgPluginDirectory);
            if (!patched && !pluginPresent)
            {
                return new LeftoverState(false, false, false, false);
            }

            bool running = (isCs2Running ?? IsCs2Running)();
            return new LeftoverState(patched, pluginPresent, running,
                !running && (!patched || BackupUndoesOnlyThePatch(gameInfoPath, gameInfo)));
        }
        catch
        {
            // Install discovery failing (no Steam/CS2 on this machine) means there is nothing
            // to recover — detection must never surface an error of its own.
            return null;
        }
    }

    /// <summary>
    ///     Restores the install: un-patches <c>gameinfo.gi</c> from CSVG's backup and removes the
    ///     plugin files. Also the permanent-disable uninstall path. Throws on hard failures (the
    ///     caller surfaces them with the <c>csvg restore</c> fallback copy).
    /// </summary>
    public static void Restore(LiveSyncSettings settings, Action<string>? log = null, Func<bool>? isCs2Running = null)
    {
        if (IsMock(settings))
        {
            return;
        }

        // A running CS2 holds the plugin DLL open and may be the user's own game with the plugin loaded:
        // pulling files out from under it fails half way at best.
        if ((isCs2Running ?? IsCs2Running)())
        {
            throw new InvalidOperationException("CS2 is running. Close it, then try again.");
        }

        lock (RestoreGate)
        {
            RestoreCore(settings, log);
        }
    }

    private static void RestoreCore(LiveSyncSettings settings, Action<string>? log)
    {
        using ServiceProvider services = BuildCsvgServices(settings);
        Cs2DirectoryUtil paths = services.GetRequiredService<Cs2DirectoryUtil>();
        string gameInfoPath = paths.Cs2GameInfoFilePath;

        if (File.Exists(gameInfoPath)
            && File.ReadAllText(gameInfoPath).Contains(GameInfoMarker, StringComparison.Ordinal))
        {
            log?.Invoke($"Restoring patched gameinfo.gi from CSVG backup: {gameInfoPath}");
            services.GetRequiredService<IBackupManager>().RestorePath(gameInfoPath);
        }

        log?.Invoke("Removing CSVG plugin files from the CS2 install.");
        services.GetRequiredService<IPluginInstaller>().UninstallPluginFiles();

        LeftoverState? after = Detect(settings, static () => false);
        if (after?.Any == true)
        {
            throw new InvalidOperationException(
                "The CS2 install still carries CSVG modifications after restore. "
                + "Run `csvg restore` (or `csvg doctor`) from the CSVG CLI to repair it manually.");
        }

        log?.Invoke("CS2 install restored.");
    }

    /// <summary>
    ///     The guard Live Sync and Reels both run before CSVG launches CS2. CSVG's launch backs up
    ///     <c>gameinfo.gi</c> first, and with no backup left over from the crashed session it would
    ///     back up the PATCHED file and put that back after every later session. So the leftovers
    ///     are undone here, or the launch is refused with the reason. Only a restore
    ///     <see cref="LeftoverState.SafeToAutoRestore" /> proves safe runs unasked: a backup that no longer
    ///     matches (CS2 updated since) would roll the update back, so that case is left to the banner.
    /// </summary>
    public static void EnsureRestoredBeforeLaunch(LiveSyncSettings settings, Action<string>? log = null,
        Func<bool>? isCs2Running = null)
    {
        lock (RestoreGate)
        {
            LeftoverState? state = Detect(settings, isCs2Running);
            if (state?.Any != true)
            {
                return;
            }

            if (!state.SafeToAutoRestore)
            {
                throw new InvalidOperationException(
                    "An earlier session left changes in your CS2 install that could not be undone, so CS2 was not "
                    + "started. "
                    + (state.Cs2Running
                        ? "CS2 is running. Close it, then try again."
                        : "Its backup of gameinfo.gi no longer matches your install, so DemoViewer won't restore it "
                          + "on its own. Use Restore my CS2 install on the banner, or Verify integrity of game "
                          + "files on Counter-Strike 2 in Steam."));
            }

            log?.Invoke("An earlier session left CSVG changes in the CS2 install; restoring them before launch.");
            try
            {
                RestoreCore(settings, log);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "An earlier session left changes in your CS2 install that could not be undone, so CS2 "
                    + $"was not started. {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    ///     Waits for a CS2 that was just told to stop to finish exiting, so the pre-launch guard does not
    ///     refuse a CS2 that is on its way out. Returns false if it is still running at
    ///     <paramref name="timeout" />; the guard then gives the reason.
    /// </summary>
    public static async Task<bool> WaitForCs2ExitAsync(TimeSpan timeout, CancellationToken cancellationToken,
        Func<bool>? isCs2Running = null)
    {
        Func<bool> running = isCs2Running ?? IsCs2Running;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (running())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    ///     True when the backup CSVG's <c>RestorePath</c> would use (the adjacent copy wins over the
    ///     central one) is exactly <paramref name="current" /> without the injected line. Restoring it
    ///     then changes nothing but CSVG's own line, which is what makes an unattended restore safe: a
    ///     game update since the backup, or a doubled marker, fails the comparison.
    /// </summary>
    internal static bool BackupUndoesOnlyThePatch(string gameInfoPath, string current)
    {
        string backupPath = Cs2DirectoryUtil.GetBackupFilePath(gameInfoPath);
        return File.Exists(backupPath) && IsExactlyThePatch(current, File.ReadAllText(backupPath));
    }

    internal static bool IsExactlyThePatch(string patched, string original)
    {
        int at = patched.IndexOf(GameInfoMarker, StringComparison.Ordinal);
        if (at < 0 || patched.IndexOf(GameInfoMarker, at + 1, StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        int lineStart = patched.LastIndexOf('\n', at) + 1;
        int lineEnd = patched.IndexOf('\n', at);
        return lineEnd >= 0
               && string.Equals(patched.Remove(lineStart, lineEnd + 1 - lineStart), original,
                   StringComparison.Ordinal);
    }

    private static bool IsCs2Running()
    {
        Process[] found = Process.GetProcessesByName("cs2");
        foreach (Process process in found)
        {
            process.Dispose();
        }

        return found.Length > 0;
    }

    private static bool IsMock(LiveSyncSettings settings) => settings.UsesMock();

    private static ServiceProvider BuildCsvgServices(LiveSyncSettings settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CsvgWebHost.ProjectSettings(settings))
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCs2VideoGeneratorCore(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     What a crashed session left behind. <see cref="Any" /> gates the restore offer;
    ///     <see cref="SafeToAutoRestore" /> lets the app undo it unattended.
    /// </summary>
    internal sealed record LeftoverState(
        bool GameInfoPatched,
        bool PluginFilesPresent,
        bool Cs2Running,
        bool SafeToAutoRestore)
    {
        public bool Any => GameInfoPatched || PluginFilesPresent;

        public Cs2InstallProbe ToProbe() => new(Any, Cs2Running, Any && SafeToAutoRestore);
    }
}
