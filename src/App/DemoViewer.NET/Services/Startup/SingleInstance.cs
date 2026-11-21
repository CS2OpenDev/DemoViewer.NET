#region

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace DemoViewer.NET.Services.Startup;

/// <summary>
///     One desktop instance per config root. The stores under that root load once and write their whole state
///     back, so a second process writing its stale copy over the first's would lose what the first added.
///     <para>
///         The claim is a lock file under the root opened with <see cref="FileShare.None" />, which the OS
///         releases when the process dies, so a crash leaves nothing that blocks the next launch. The holder
///         listens on a named pipe whose name is per user and per root; a second launch sends it the demo it was
///         asked to open (or nothing, to bring the window forward) and exits.
///     </para>
///     <para>
///         A relaunch (safe mode off, an update's restart) starts while the old process is still finishing, so
///         <see cref="Claim" /> waits for the lock for a while before giving up, and then starts anyway rather
///         than not at all. Only the desktop head claims: the test hosts and UiCapture never run its <c>Main</c>,
///         and a run under its own <see cref="AppPaths.ConfigDirEnvVar" /> root is another instance's business.
///     </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The lock file's name under the config root.</summary>
    public const string LockFileName = "instance.lock";

    /// <summary>How long a launch waits for an instance that is starting or exiting before it starts anyway.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    // A path is a few hundred characters; anything longer than this is not one.
    private const int MaxMessageBytes = 64 * 1024;

    private static readonly TimeSpan _connectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _readTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(200);

    private readonly CancellationTokenSource _stop = new();
    private readonly FileStream _lock;
    private readonly string _pipeName;
    private Task? _listening;

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        _pipeName = pipeName;
    }

    /// <summary>
    ///     Takes the lock for <paramref name="configRoot" />, or, when another instance holds it, hands that one
    ///     <paramref name="demoPath" /> and reports it. Waits up to <paramref name="wait" /> for an instance that
    ///     holds the lock but does not answer (starting, or exiting for a relaunch); past that it gives up the
    ///     guard and returns neither, so this launch still starts.
    /// </summary>
    /// <param name="configRoot">The config root; null (no filesystem) claims nothing.</param>
    /// <param name="demoPath">The demo this launch was asked to open, already a full path; null for none.</param>
    /// <param name="wait">How long to keep trying; null for <see cref="DefaultWait" />.</param>
    public static SingleInstanceClaim Claim(string? configRoot, string? demoPath, TimeSpan? wait = null)
    {
        if (configRoot is null)
        {
            return new SingleInstanceClaim(null, false);
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        TimeSpan limit = wait ?? DefaultWait;
        while (true)
        {
            if (TryAcquire(configRoot) is { } instance)
            {
                return new SingleInstanceClaim(instance, false);
            }

            if (TrySignal(configRoot, demoPath))
            {
                return new SingleInstanceClaim(null, true);
            }

            if (elapsed.Elapsed >= limit)
            {
                return new SingleInstanceClaim(null, false);
            }

            Thread.Sleep(_retryDelay);
        }
    }

    /// <summary>The lock for <paramref name="configRoot" />, or null while another instance holds it.</summary>
    public static SingleInstance? TryAcquire(string configRoot)
    {
        ArgumentNullException.ThrowIfNull(configRoot);
        try
        {
            Directory.CreateDirectory(configRoot);
            FileStream lockFile = new(Path.Combine(configRoot, LockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(lockFile, PipeName(configRoot));
        }
        catch (IOException)
        {
            return null; // held by another instance
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Sends <paramref name="demoPath" /> (or an empty message, to bring the window forward) to the instance
    ///     listening for <paramref name="configRoot" />. False when none answers.
    /// </summary>
    public static bool TrySignal(string configRoot, string? demoPath)
    {
        try
        {
            using NamedPipeClientStream client = new(".", PipeName(configRoot), PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(_connectTimeout);
            byte[] message = Encoding.UTF8.GetBytes(demoPath ?? "");
            client.Write(message, 0, message.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The pipe of <paramref name="configRoot" />'s instance. Per user, since another account's instance is no
    ///     concern of this one, and per root, since a run under another root is another instance.
    /// </summary>
    public static string PipeName(string configRoot)
    {
        string root = Path.GetFullPath(configRoot);
        if (OperatingSystem.IsWindows())
        {
            root = root.ToUpperInvariant();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Environment.UserName}\n{root}"));
        return "DemoViewer.NET-" + Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }

    /// <summary>
    ///     The demo a launch's command line names, as a full path against this process's working directory: the
    ///     first argument ending in <c>.dem</c>. Null for none.
    /// </summary>
    public static string? DemoArgument(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (string arg in args)
        {
            if (!arg.StartsWith('-') && arg.EndsWith(".dem", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Path.GetFullPath(arg);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     What a received message asks to open: a full local path to a <c>.dem</c>, or null for anything else. The
    ///     pipe takes any process of this user, so a share or device path, which would make the app authenticate
    ///     to a host of the sender's choosing, is refused like a relative one; the window still comes forward.
    /// </summary>
    public static string? AcceptedDemoPath(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)
            || !message.EndsWith(".dem", StringComparison.OrdinalIgnoreCase)
            || !Path.IsPathFullyQualified(message)
            || (message.Length >= 2 && message[0] is '\\' or '/' && message[1] is '\\' or '/')
            || message.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return null;
        }

        return message;
    }

    /// <summary>
    ///     Starts answering later launches. <paramref name="onActivate" /> runs on a pool thread with the demo the
    ///     launch asked for (through <see cref="AcceptedDemoPath" />), or null to only bring the window forward.
    /// </summary>
    public void Listen(Action<string?> onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        _listening ??= Task.Run(() => ServeAsync(onActivate, _stop.Token));
    }

    /// <summary>Stops answering and releases the lock, so a relaunch started next takes it at once.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            _listening?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop ends on the cancellation; nothing else it throws matters at exit.
        }

        _lock.Dispose();
        _stop.Dispose();
    }

    private async Task ServeAsync(Action<string?> onActivate, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            string? message;
            try
            {
                await using NamedPipeServerStream server = new(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);
                message = await ReadMessageAsync(server, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A sender that hung up or stalled, or a pipe name another process holds. Back off a little so a
                // pipe that cannot be created does not spin, and keep answering.
                try
                {
                    await Task.Delay(_retryDelay, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                onActivate(AcceptedDemoPath(message));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Single instance: handing on an activation failed: {ex}");
            }
        }
    }

    // Up to the sender's close, within a size and a time limit: a sender that never closes is not waited on.
    private static async Task<string?> ReadMessageAsync(Stream pipe, CancellationToken stop)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(_readTimeout);
        using MemoryStream received = new();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await pipe.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (received.Length + read > MaxMessageBytes)
            {
                return null;
            }

            received.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(received.GetBuffer(), 0, (int)received.Length);
    }
}

/// <summary>What <see cref="SingleInstance.Claim" /> came to.</summary>
/// <param name="Instance">The lock this launch now holds; null when it handed off or gave up waiting.</param>
/// <param name="HandedOff">True when a running instance took this launch's request, so this one exits.</param>
public sealed record SingleInstanceClaim(SingleInstance? Instance, bool HandedOff);
