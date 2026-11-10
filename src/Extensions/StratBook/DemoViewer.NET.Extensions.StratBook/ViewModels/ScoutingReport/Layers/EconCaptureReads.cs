#region

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     The captures of the team's demos on one map, read once per team load and map for the layers that take
///     buys and timeouts from them (Economy, Timeouts, By score).
/// </summary>
public static class EconCaptureReads
{
    private static readonly ConditionalWeakTable<ScoutingReportData,
            ConcurrentDictionary<string, Task<IReadOnlyDictionary<string, DemoCaptures>?>>>
        _reads = [];

    /// <summary>
    ///     The demos' captures by demo path, or null when this host has no round index or no Round Facts. A demo
    ///     whose captures are absent or stale is left out (the reader counts it).
    /// </summary>
    /// <param name="context">The scope.</param>
    public static Task<IReadOnlyDictionary<string, DemoCaptures>?> For(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ScoutingReportData data = context.Data;
        return _reads.GetValue(data,
                _ => new ConcurrentDictionary<string, Task<IReadOnlyDictionary<string, DemoCaptures>?>>(
                    StringComparer.OrdinalIgnoreCase))
            .GetOrAdd(context.Map, map => ReadAsync(data, map));
    }

    private static async Task<IReadOnlyDictionary<string, DemoCaptures>?> ReadAsync(ScoutingReportData data,
        string map)
    {
        if (data.Sources.RoundIndex is null || data.Sources.RoundFacts is null)
        {
            return null;
        }

        TeamRoundSet? set = await StripData.RoundsFor(data).ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        IReadOnlyList<TeamRound> rounds = set.On(map);
        return await ScoutingLayerContext.Offload<IReadOnlyDictionary<string, DemoCaptures>?>(() =>
        {
            Dictionary<string, DemoCaptures> byDemo = new(StringComparer.Ordinal);
            foreach (TeamRound first in rounds.GroupBy(r => r.DemoPath, StringComparer.Ordinal)
                         .Select(g => g.First()))
            {
                try
                {
                    if (data.Captures.Read(first.DemoPath, map, first.Sha256) is { } captures)
                    {
                        byDemo[first.DemoPath] = captures;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or InvalidOperationException)
                {
                    // Unreadable: the demo counts as without captures.
                }
            }

            return byDemo;
        }).ConfigureAwait(false);
    }

    /// <summary>The team's timeouts on the scope's map, or null when the captures cannot be read on this host.</summary>
    /// <param name="context">The scope.</param>
    public static async Task<TeamTimeouts?> TimeoutsFor(ScoutingLayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IReadOnlyDictionary<string, DemoCaptures>? captures = await For(context).ConfigureAwait(false);
        TeamRoundSet? set = await StripData.RoundsFor(context.Data).ConfigureAwait(false);
        if (captures is null || set is null)
        {
            return null;
        }

        return TimeoutDerivation.Build(set.On(context.Map),
            path => captures.TryGetValue(path, out DemoCaptures? c) ? c.Timeouts : null);
    }
}
