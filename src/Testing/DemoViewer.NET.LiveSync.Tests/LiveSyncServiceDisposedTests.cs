#region

using DemoViewer.NET.Configuration;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     The lifecycle members the chip flyout calls stay safe after disposal. Shutdown sets the
///     disposed flag and then spends seconds stopping CS2 while the window is still up; a Disable,
///     Re-sync or Reconnect clicked in that window must not fault the relay command and crash the
///     process mid-restore.
/// </summary>
[Category("Unit")]
public class LiveSyncServiceDisposedTests
{
    [Test]
    [Timeout(30_000)]
    public async Task LifecycleMembers_AfterDispose_AreNoOps(CancellationToken cancellationToken)
    {
        // No session was ever started, so the shell is never touched.
        LiveSyncService service = new(null!, new StaticMonitor());
        await service.DisposeAsync();

        Exception? enable = await CaptureAsync(() => service.EnableAsync(cancellationToken));
        Exception? disable = await CaptureAsync(() => service.DisableAsync(cancellationToken));
        Exception? resync = await CaptureAsync(() => service.ResyncAsync(cancellationToken));
        bool verified = true;
        Exception? verify = await CaptureAsync(async () =>
            verified = await service.VerifyMomentAsync(100, cancellationToken: cancellationToken));

        await Assert.That(enable).IsNull();
        await Assert.That(disable).IsNull();
        await Assert.That(resync).IsNull();
        await Assert.That(verify).IsNull();
        await Assert.That(verified).IsFalse();
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class StaticMonitor : IOptionsMonitor<AppSettings>
    {
        public AppSettings CurrentValue { get; } = new();

        public AppSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }
}
