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
///         The holder answers from the moment it takes the lock, so a held lock that does not answer belongs to
///         a process that is exiting or hung. A relaunch (safe mode off, an update's restart) starts after the
///         old process has let go, or while it is finishing, so <see cref="Claim" /> waits for the lock for a
///         while; past that the launch exits rather than start beside the holder. Only the desktop head claims: the test hosts and UiCapture never run its <c>Main</c>,
///         and a run under its own <see cref="AppPaths.ConfigDirEnvVar" /> root is another instance's business.
///     </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The lock file's name under the config root.</summary>
    public const string LockFileName = "instance.lock";

    /// <summary>How long a launch waits for an instance that holds the lock but does not answer.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(10);

    // A path is a few hundred characters; anything longer than this is not one.
    private const int MaxMessageBytes = 64 * 1024;

    private static readonly TimeSpan _connectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _readTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(200);

    private readonly CancellationTokenSource _stop = new();
    private readonly FileStream _lock;
    private readonly string _pipeName;
    private readonly Lock _gate = new();
    private readonly List<string?> _pending = [];
    private Action<string?>? _onActivate;
    private Task? _listening;

    private SingleInstance(FileStream lockFile, string pipeName)
    {
        _lock = lockFile;
        _pipeName = pipeName;
    }

    /// <summary>
    ///     Takes the lock for <paramref name="configRoot" />, or, when another instance holds it, hands that one
    ///     <paramref name="demoPath" /> and reports it. Waits up to <paramref name="wait" /> for an instance that
    ///     holds the lock but does not answer (exiting for a relaunch); past that it reports
    ///     <see cref="SingleInstanceClaim.Blocked" />, since starting beside a live holder is what the lock prevents.
    ///     A lock that cannot be opened at all (no access to the root) claims nothing and does not wait.
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
            if (Acquire(configRoot, out bool held) is { } instance)
            {
                return new SingleInstanceClaim(instance, false);
            }

            if (!held)
            {
                return new SingleInstanceClaim(null, false);
            }

            if (TrySignal(configRoot, demoPath))
            {
                return new SingleInstanceClaim(null, true);
            }

            if (elapsed.Elapsed >= limit)
            {
                return new SingleInstanceClaim(null, false, true);
            }

            Thread.Sleep(_retryDelay);
        }
    }

    /// <summary>The lock for <paramref name="configRoot" />, or null while another instance holds it.</summary>
    public static SingleInstance? TryAcquire(string configRoot)
    {
        ArgumentNullException.ThrowIfNull(configRoot);
        return Acquire(configRoot, out _);
    }

    // Held is false when the lock could not be opened for want of access, which no other instance explains.
    private static SingleInstance? Acquire(string configRoot, out bool held)
    {
        held = false;
        try
        {
            Directory.CreateDirectory(configRoot);
            FileStream lockFile = new(Path.Combine(configRoot, LockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(lockFile, PipeName(configRoot));
        }
        catch (IOException)
        {
            held = true; // by another instance
            return null;
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
    ///     Starts answering later launches, holding what they ask for until <see cref="Listen(Action{string})" />
    ///     sets the handler. Called right after the claim, so a launch during a slow start still hands off.
    /// </summary>
    public void Listen()
    {
        lock (_gate)
        {
            _listening ??= Task.Run(() => ServeAsync(_stop.Token));
        }
    }

    /// <summary>
    ///     Answers later launches with <paramref name="onActivate" />, which gets the demo the launch asked for
    ///     (through <see cref="AcceptedDemoPath" />), or null to only bring the window forward. Requests held
    ///     since <see cref="Listen()" /> run on the calling thread, later ones on a pool thread.
    /// </summary>
    public void Listen(Action<string?> onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        string?[] held;
        lock (_gate)
        {
            _onActivate = onActivate;
            held = [.. _pending];
            _pending.Clear();
        }

        Listen();
        foreach (string? demoPath in held)
        {
            Deliver(onActivate, demoPath);
        }
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

    private async Task ServeAsync(CancellationToken stop)
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

            string? demoPath = AcceptedDemoPath(message);
            Action<string?>? onActivate;
            lock (_gate)
            {
                onActivate = _onActivate;
                if (onActivate is null)
                {
                    _pending.Add(demoPath);
                }
            }

            if (onActivate is not null)
            {
                Deliver(onActivate, demoPath);
            }
        }
    }

    private static void Deliver(Action<string?> onActivate, string? demoPath)
    {
        try
        {
            onActivate(demoPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Single instance: handing on an activation failed: {ex}");
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
/// <param name="Blocked">True when another instance holds the lock and never answered, so this one exits too.</param>
public sealed record SingleInstanceClaim(SingleInstance? Instance, bool HandedOff, bool Blocked = false);
