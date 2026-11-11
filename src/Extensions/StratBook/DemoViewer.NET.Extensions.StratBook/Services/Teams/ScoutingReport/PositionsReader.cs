#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>Why a demo's positions were not read.</summary>
public enum PositionsMiss
{
    /// <summary>They were read.</summary>
    None,

    /// <summary>The host has no round index.</summary>
    NoIndex,

    /// <summary>The demo has no positions file, or it does not parse or names another demo.</summary>
    Missing,

    /// <summary>The file was built under another fingerprint than the one in force for the map.</summary>
    Stale
}

/// <summary>
///     The one way a Scouting Report layer reads a demo's round positions: through the round index, under the
///     fingerprint in force for the demo's map, so a file built under other settings reads as absent the way the
///     Setup Heatmaps and the Situations cards treat it. Counts the demos it could not read, stale ones included,
///     for the partial-positions notice. Safe off the UI thread.
/// </summary>
public sealed class PositionsReader
{
    private readonly Func<string?, string>? _fingerprintFor;
    private readonly RoundIndexStore? _store;
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stale = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <param name="store">The round index; null reads every demo as without positions.</param>
    /// <param name="fingerprintFor">The fingerprint current positions carry per map; null accepts any (a test host).</param>
    public PositionsReader(RoundIndexStore? store, Func<string?, string>? fingerprintFor)
    {
        _store = store;
        _fingerprintFor = fingerprintFor;
    }

    /// <summary>Demos read without positions so far, stale ones included.</summary>
    public int WithoutCount
    {
        get
        {
            lock (_gate)
            {
                return _missing.Count + _stale.Count;
            }
        }
    }

    /// <summary>Demos whose positions were built under another fingerprint.</summary>
    public int StaleCount
    {
        get
        {
            lock (_gate)
            {
                return _stale.Count;
            }
        }
    }

    /// <summary>A demo's positions, or null when there are none or they are stale.</summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="map">Its map, for the fingerprint.</param>
    /// <param name="sha256">Its hash, or null.</param>
    public RoundPositionsDocument? Read(string demoPath, string? map, string? sha256) =>
        Read(demoPath, map, sha256, out _);

    /// <summary>A demo's positions, or null with the reason.</summary>
    /// <param name="demoPath">The demo.</param>
    /// <param name="map">Its map, for the fingerprint.</param>
    /// <param name="sha256">Its hash, or null.</param>
    /// <param name="miss">Why none were read.</param>
    public RoundPositionsDocument? Read(string demoPath, string? map, string? sha256, out PositionsMiss miss)
    {
        ArgumentNullException.ThrowIfNull(demoPath);
        if (_store is null)
        {
            miss = PositionsMiss.NoIndex;
            Note(demoPath, miss);
            return null;
        }

        string? fingerprint = _fingerprintFor?.Invoke(map);
        if (_store.TryReadPositions(demoPath, fingerprint, sha256) is { } positions)
        {
            miss = PositionsMiss.None;
            return positions;
        }

        // Only the miss path pays the second read: it tells a stale file from an absent one.
        miss = fingerprint is not null && _store.TryReadPositions(demoPath, null, sha256) is not null
            ? PositionsMiss.Stale
            : PositionsMiss.Missing;
        Note(demoPath, miss);
        return null;
    }

    private void Note(string demoPath, PositionsMiss miss)
    {
        lock (_gate)
        {
            (miss == PositionsMiss.Stale ? _stale : _missing).Add(demoPath);
        }
    }
}
