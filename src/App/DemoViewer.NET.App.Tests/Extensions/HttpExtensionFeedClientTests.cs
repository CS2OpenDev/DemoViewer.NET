#region

using System.Buffers;
using System.Net;
using DemoViewer.NET.Extensions.Updates;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="HttpExtensionFeedClient" /> over a fake handler: a body that keeps arriving completes however
///     long it takes, since only the headers have a whole-call budget; a body that stops arriving is a stalled
///     transfer within the stall budget; a cap is a cap. No request leaves the process.
/// </summary>
public class HttpExtensionFeedClientTests
{
    private static readonly Uri _url = new("https://example.invalid/pack.zip");

    [Test]
    public async Task Download_ThatKeepsArriving_CompletesPastTheHeaderBudget()
    {
        // Each chunk moves the manual clock 50 ms after its read is pending: no single read nears the 400 ms stall
        // budget, but the 600 ms of body passes the header budget and the stall budget taken as one deadline.
        ManualTimers clock = new();
        DateTimeOffset started = clock.GetUtcNow();
        byte[] chunk = new byte[1024];
        using PacedHandler handler = new(chunks: 12, chunk, pace: TimeSpan.FromMilliseconds(1), stallAfter: null,
            onChunk: () => clock.Advance(TimeSpan.FromMilliseconds(50)));
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(400), clock);
        using MemoryStream destination = new();
        List<long> progress = [];

        await client.DownloadAsync(_url, destination, 12 * 1024, new InlineProgress(progress), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        using (Assert.Multiple())
        {
            await Assert.That(destination.Length).IsEqualTo(12 * 1024);
            await Assert.That(progress[^1]).IsEqualTo(12 * 1024);
            await Assert.That(clock.GetUtcNow() - started).IsEqualTo(TimeSpan.FromMilliseconds(600));
        }
    }

    [Test]
    public async Task Download_ThatStopsArriving_IsAStalledTransfer_NotACancellation()
    {
        byte[] chunk = new byte[1024];
        using PacedHandler handler = new(chunks: 20, chunk, pace: TimeSpan.FromMilliseconds(10), stallAfter: 3);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300));
        using MemoryStream destination = new();

        ExtensionDownloadException? ex = await Assert.ThrowsAsync<ExtensionDownloadException>(() =>
            client.DownloadAsync(_url, destination, 20 * 1024, null, CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(ex!.Message).IsEqualTo("the transfer stalled");
            await Assert.That(destination.Length).IsEqualTo(3 * 1024);
        }
    }

    [Test]
    public async Task Download_RefusesMoreBytesThanTheFeedSaid_AndSlowHeaders()
    {
        byte[] chunk = new byte[1024];
        using PacedHandler over = new(chunks: 4, chunk, pace: TimeSpan.Zero, stallAfter: null);
        using HttpExtensionFeedClient client = new(over, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using MemoryStream destination = new();
        ExtensionDownloadException? tooMany = await Assert.ThrowsAsync<ExtensionDownloadException>(() =>
            client.DownloadAsync(_url, destination, 2 * 1024, null, CancellationToken.None));

        using PacedHandler slow = new(chunks: 1, chunk, pace: TimeSpan.Zero, stallAfter: null,
            headerDelay: TimeSpan.FromSeconds(2));
        using HttpExtensionFeedClient impatient = new(slow, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));
        ExtensionDownloadException? noHeaders =
            await Assert.ThrowsAsync<ExtensionDownloadException>(() =>
                impatient.GetFeedAsync(_url, CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(tooMany!.Message).Contains("larger than the 2048 bytes");
            await Assert.That(noHeaders!.Message).IsEqualTo("the server did not answer in time");
        }
    }

    [Test]
    public async Task Download_CancelledByTheCaller_IsACancellation()
    {
        byte[] chunk = new byte[1024];
        using PacedHandler handler = new(chunks: 100, chunk, pace: TimeSpan.FromMilliseconds(20), stallAfter: null);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        using MemoryStream destination = new();
        using CancellationTokenSource cts = new();
        cts.CancelAfter(TimeSpan.FromMilliseconds(120));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.DownloadAsync(_url, destination, 100 * 1024, null, cts.Token));
        await Assert.That(destination.Length).IsLessThan(100 * 1024);
    }

    // A stalled read is abandoned, not cancelled, and can still complete into the buffer it was handed. That
    // buffer must not go back to the shared pool: the next renter on this thread would get it and see the late
    // bytes. The manual clock fires the stall on this thread, so the client's cleanup runs here too.
    [Test]
    public async Task Download_ThatStalls_DoesNotHandItsBufferToTheNextRenter()
    {
        ManualTimers clock = new();
        GatedStream body = new([0xAB]);
        using GatedHandler handler = new(body);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), clock);
        using MemoryStream destination = new();

        Task download = client.DownloadAsync(_url, destination, 1024 * 1024, null, CancellationToken.None);
        await body.ReadPending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForOneTimer(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        byte[] next = ArrayPool<byte>.Shared.Rent(80 * 1024);
        Array.Clear(next);
        body.Release();
        bool untouched = next.All(b => b == 0);
        ArrayPool<byte>.Shared.Return(next);

        ExtensionDownloadException? ex = await Assert.ThrowsAsync<ExtensionDownloadException>(() => download);
        using (Assert.Multiple())
        {
            await Assert.That(ex!.Message).IsEqualTo("the transfer stalled");
            await Assert.That(untouched).IsTrue();
        }
    }

    [Test]
    public async Task Feed_ThatStalls_DoesNotHandItsBufferToTheNextRenter()
    {
        ManualTimers clock = new();
        GatedStream body = new("AAAA"u8.ToArray());
        using GatedHandler handler = new(body);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), clock);

        Task<string> feed = client.GetFeedAsync(_url, CancellationToken.None);
        await body.ReadPending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForOneTimer(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        char[] next = ArrayPool<char>.Shared.Rent(16 * 1024);
        Array.Clear(next);
        body.Release();
        bool untouched = next.All(c => c == '\0');
        ArrayPool<char>.Shared.Return(next);

        ExtensionDownloadException? ex = await Assert.ThrowsAsync<ExtensionDownloadException>(() => feed);
        using (Assert.Multiple())
        {
            await Assert.That(ex!.Message).IsEqualTo("the transfer stalled");
            await Assert.That(untouched).IsTrue();
        }
    }

    // The header budget's timer is gone once the headers are in; the one left is the stall budget's.
    private static async Task WaitForOneTimer(ManualTimers clock)
    {
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(10));
        while (clock.Pending != 1)
        {
            await Task.Delay(5, limit.Token);
        }
    }

    // ExtensionUpdateService reads a missing feed off the exception's status code, so the client must keep it.
    [Test]
    public async Task Feed_NotFound_ThrowsWithTheStatusCode()
    {
        using StatusHandler handler = new(HttpStatusCode.NotFound);
        using HttpExtensionFeedClient client = new(handler, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        HttpRequestException? ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetFeedAsync(new Uri("https://example.invalid/extensions.json"), CancellationToken.None));

        using (Assert.Multiple())
        {
            await Assert.That(ex!.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            await Assert.That(handler.Requests).IsEqualTo(1);
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("Not Found") });
        }
    }

    // Serves chunks at a fixed pace from a stream that waits between them, after an optional header delay;
    // with stallAfter set, the read after that many chunks never returns. onChunk runs after the pace wait, so
    // the caller's read is already pending when it does.
    private sealed class PacedHandler(
        int chunks,
        byte[] chunk,
        TimeSpan pace,
        int? stallAfter,
        TimeSpan? headerDelay = null,
        Action? onChunk = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (headerDelay is { } delay)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PacedStream(chunks, chunk, pace, stallAfter, onChunk))
            };
        }
    }

    private sealed class PacedStream(int chunks, byte[] chunk, TimeSpan pace, int? stallAfter, Action? onChunk) : Stream
    {
        private int _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_served >= chunks)
            {
                return 0;
            }

            if (stallAfter is { } stall && _served >= stall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (pace > TimeSpan.Zero)
            {
                await Task.Delay(pace, cancellationToken);
            }

            onChunk?.Invoke();
            int n = Math.Min(buffer.Length, chunk.Length);
            chunk.AsSpan(0, n).CopyTo(buffer.Span);
            _served++;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GatedHandler(GatedStream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    // Its first read waits, deaf to cancellation, until Release; then it writes its fill into the buffer it was
    // handed, inline on the releasing thread, as a socket receive landing late would.
    private sealed class GatedStream(byte[] fill) : Stream
    {
        private readonly TaskCompletionSource _gate = new();
        private bool _served;

        public TaskCompletionSource ReadPending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release() => _gate.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_served)
            {
                return 0;
            }

            _served = true;
            ReadPending.TrySetResult();
            await _gate.Task;
            int n = Math.Min(buffer.Length, fill.Length);
            fill.AsSpan(0, n).CopyTo(buffer.Span);
            buffer.Span[n..].Fill(fill[0]);
            return buffer.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Timers fire only when the test advances the clock, on the advancing thread.
    private sealed class ManualTimers : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        public int Pending
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer = new(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<Timer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.DueAt <= _now)];
                foreach (Timer t in due)
                {
                    _timers.Remove(t);
                }
            }

            foreach (Timer t in due)
            {
                t.Fire();
            }
        }

        private sealed class Timer(ManualTimers owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan)
                {
                    throw new NotSupportedException("a periodic timer");
                }

                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        DueAt = owner._now + dueTime;
                        owner._timers.Add(this);
                    }
                }

                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                lock (owner._gate)
                {
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class InlineProgress(List<long> sink) : IProgress<long>
    {
        public void Report(long value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
