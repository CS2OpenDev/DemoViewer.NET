#region

using System.Runtime.ExceptionServices;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Modules.Playback2D.Annotations;

/// <summary>
///     Lets a writer from outside the 2D tab (a transfer import) write a demo's annotation file while that demo is
///     open, without either side losing a stroke. The order is fixed: each open session on the demo saves its
///     unsaved strokes and stops saving, the writer writes (merging with what is on disk by then), and each
///     session reloads the file and puts back what was drawn meanwhile. Everything here runs on the UI thread,
///     where the sessions' documents live.
/// </summary>
internal sealed class AnnotationWriteCoordinator
{
    private readonly Lock _gate = new();
    private readonly List<AnnotationSessionController> _sessions = [];

    /// <summary>Makes <paramref name="session" /> take part until the returned registration is disposed.</summary>
    public IDisposable Register(AnnotationSessionController session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        return new Registration(this, session);
    }

    /// <summary>
    ///     Saves and holds every open session on any of <paramref name="demoPaths" />. The writer writes after this
    ///     returns, then ends the hold.
    /// </summary>
    /// <param name="demoPaths">Every path of the demo whose annotations are about to be written.</param>
    public async Task<Hold> HoldAsync(IReadOnlyCollection<string> demoPaths)
    {
        ArgumentNullException.ThrowIfNull(demoPaths);
        AnnotationSessionController[] open;
        lock (_gate)
        {
            open =
            [
                .. _sessions.Where(s => s.AttachedDemoPath is { } path
                                        && demoPaths.Contains(path, PathComparer.Paths))
            ];
        }

        Hold hold = new([]);
        try
        {
            foreach (AnnotationSessionController session in open)
            {
                await session.BeginExternalWriteAsync();
                hold.Add(session);
            }
        }
        catch (Exception ex)
        {
            // The writer gets no hold to end, so the sessions held so far are let go here.
            try
            {
                await hold.EndAsync(false);
            }
            catch (Exception endEx)
            {
                throw new AggregateException(ex, endEx);
            }

            throw;
        }

        return hold;
    }

    /// <summary>The sessions one <see cref="HoldAsync" /> holds.</summary>
    public sealed class Hold
    {
        private readonly List<AnnotationSessionController> _sessions;

        internal Hold(List<AnnotationSessionController> sessions) => _sessions = sessions;

        internal void Add(AnnotationSessionController session) => _sessions.Add(session);

        /// <summary>
        ///     Ends the hold: each session reloads the file when <paramref name="written" />, keeping what was drawn
        ///     during the hold, and saves again.
        /// </summary>
        /// <param name="written">Whether the writer changed the file.</param>
        /// <exception cref="AggregateException">More than one session failed to end; each still ended its hold.</exception>
        public async Task EndAsync(bool written)
        {
            AnnotationSessionController[] sessions = [.. _sessions];
            _sessions.Clear();
            List<Exception>? failures = null;
            foreach (AnnotationSessionController session in sessions)
            {
                // One session failing must not leave the next one held, its saves refused for good.
                try
                {
                    await session.EndExternalWriteAsync(written);
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            switch (failures)
            {
                case [var only]:
                    ExceptionDispatchInfo.Throw(only);
                    break;
                case [_, _, ..]:
                    throw new AggregateException(failures);
            }
        }
    }

    private sealed class Registration(AnnotationWriteCoordinator owner, AnnotationSessionController session)
        : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lock (owner._gate)
                {
                    owner._sessions.Remove(session);
                }
            }
        }
    }
}
