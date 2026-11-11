#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     The Scouting Report's loads, shared by every tab that shows a report: one <see cref="ScoutingReportData" />
///     per team and projection key. A second tab on the same team joins the load instead of starting its own, and
///     the layers' per-load memos, the long form and the players' numbers come with it. A team's load is dropped
///     once no tab holds it.
///     <para>
///         <b>Holds.</b> A tab holds one team at a time: acquiring another releases the first. A changed key (the
///         stamps moved) or a tab re-projecting the team it already holds starts a fresh load, and every other tab
///         joins that one when it re-projects, so a change reloads once for all of them.
///     </para>
///     <para>
///         The tabs call it from the UI thread; it is locked anyway, since a load's continuation may ask for a shared
///         result from a worker.
///     </para>
/// </summary>
public sealed class ScoutingReportLoads
{
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<object, Guid> _held = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Guid, DateTime> _requestedAt = [];
    private readonly Func<Guid, string, Func<Action, Task>> _runSection;

    /// <param name="runSection">Runs a named load for a team; the pool when null.</param>
    public ScoutingReportLoads(Func<Guid, string, Func<Action, Task>>? runSection = null) =>
        _runSection = runSection ?? ((_, _) => Task.Run);

    /// <summary>The teams with a load held by at least one tab.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>How many tabs hold the team's current load.</summary>
    /// <param name="teamId">The team.</param>
    internal int Holders(Guid teamId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(teamId, out Entry? entry) ? entry.Holders.Count : 0;
        }
    }

    /// <summary>The team's load under <paramref name="key" />, joined when one is held, started otherwise.</summary>
    /// <param name="holder">The tab asking; it holds the team until it releases or acquires another.</param>
    /// <param name="teamId">The team.</param>
    /// <param name="teamName">Its name, for Review title cards.</param>
    /// <param name="key">The projection key: the team and the stamps of what it is built from.</param>
    /// <param name="sources">The services; every holder passes the same composition root's.</param>
    internal ScoutingReportData Acquire(object holder, Guid teamId, string teamName, string key,
        ScoutingReportSources sources)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_held.TryGetValue(holder, out Guid had) && had != teamId)
            {
                ReleaseLocked(holder);
            }

            if (!_entries.TryGetValue(teamId, out Entry? entry))
            {
                entry = new Entry(key, Start(teamId, teamName, sources));
                _entries[teamId] = entry;
            }
            else if (entry.Key != key || entry.Holders.Contains(holder))
            {
                entry.Restart(key, Start(teamId, teamName, sources));
            }

            entry.Holders.Add(holder);
            _held[holder] = teamId;
            return entry.Data;
        }
    }

    /// <summary>Lets go of the holder's team; its load is dropped when no other tab holds it.</summary>
    /// <param name="holder">The tab.</param>
    internal void Release(object holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        lock (_gate)
        {
            ReleaseLocked(holder);
        }
    }

    /// <summary>
    ///     A result built once per load and <paramref name="key" />: the first ask starts it, the same ask joins it,
    ///     another key replaces it. A load no longer held (a stale tab's) is built without being kept.
    /// </summary>
    /// <param name="data">The load it is built over.</param>
    /// <param name="slot">What it is ("long-form", "players").</param>
    /// <param name="key">What else it reads, beyond the load.</param>
    /// <param name="start">Starts the build.</param>
    internal Task<T> Share<T>(ScoutingReportData data, string slot, string key, Func<Task<T>> start)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(start);
        lock (_gate)
        {
            if (!_entries.TryGetValue(data.TeamId, out Entry? entry) || !ReferenceEquals(entry.Data, data))
            {
                return start();
            }

            if (entry.Shared.TryGetValue(slot, out (string Key, Task Task) had) && had.Key == key)
            {
                return (Task<T>)had.Task;
            }

            Task<T> task = start();
            entry.Shared[slot] = (key, task);
            return task;
        }
    }

    /// <summary>Runs a build as the team's named load; an inline runner hands back a completed task.</summary>
    /// <param name="teamId">The team.</param>
    /// <param name="section">The load's name.</param>
    /// <param name="build">The build; its result, or null when it threw out of the runner.</param>
    internal Task<T?> Run<T>(Guid teamId, string section, Func<T?> build) where T : class
    {
        ArgumentNullException.ThrowIfNull(build);
        T? result = null;
        Task run = _runSection(teamId, section)(() => result = build());
        return run.IsCompleted
            ? Task.FromResult(run.IsCompletedSuccessfully ? result : null)
            : run.ContinueWith(t => t.IsCompletedSuccessfully ? result : null, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    ///     Whether opening the team should queue its demos: true unless any tab asked for it within
    ///     <paramref name="window" />. A forced ask is always true. Either way a true answer restarts the window.
    /// </summary>
    /// <param name="teamId">The team.</param>
    /// <param name="now">The asking tab's clock.</param>
    /// <param name="window">How long an ask holds.</param>
    /// <param name="force">The button: always asks.</param>
    internal bool ClaimRequest(Guid teamId, DateTime now, TimeSpan window, bool force)
    {
        lock (_gate)
        {
            if (!force && _requestedAt.TryGetValue(teamId, out DateTime at) && now - at < window)
            {
                return false;
            }

            _requestedAt[teamId] = now;
            return true;
        }
    }

    private ScoutingReportData Start(Guid teamId, string teamName, ScoutingReportSources sources) =>
        ScoutingReportData.Start(teamId, teamName, sources, section => _runSection(teamId, section));

    private void ReleaseLocked(object holder)
    {
        if (!_held.Remove(holder, out Guid teamId) || !_entries.TryGetValue(teamId, out Entry? entry))
        {
            return;
        }

        entry.Holders.Remove(holder);
        if (entry.Holders.Count == 0)
        {
            _entries.Remove(teamId);
        }
    }

    private sealed class Entry(string key, ScoutingReportData data)
    {
        public string Key { get; private set; } = key;

        public ScoutingReportData Data { get; private set; } = data;

        public HashSet<object> Holders { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<string, (string Key, Task Task)> Shared { get; } = new(StringComparer.Ordinal);

        // Tabs still on the old load join this one when they re-project; until then they keep their own reference.
        public void Restart(string key, ScoutingReportData data)
        {
            Key = key;
            Data = data;
            Holders.Clear();
            Shared.Clear();
        }
    }
}
