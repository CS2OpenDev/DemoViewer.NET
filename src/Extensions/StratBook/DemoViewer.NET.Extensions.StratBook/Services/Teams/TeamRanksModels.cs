#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>Which ranking a <see cref="RankEntry" /> belongs to.</summary>
public enum RankSystem
{
    /// <summary>Valve Regional Standings.</summary>
    Vrs,

    /// <summary>The HLTV world ranking.</summary>
    Hltv
}

/// <summary>How an opponent stood in the rankings on the day of a demo.</summary>
public enum OpponentTier
{
    /// <summary>Ranked 30 or better (HLTV preferred, else VRS).</summary>
    Top30,

    /// <summary>Ranked, but below 30.</summary>
    Other,

    /// <summary>No rank entered for the team.</summary>
    Unknown
}

/// <summary>
///     One rank the user typed in by hand for a team: the number and the day it applies from. Entries are kept,
///     so a demo from last month is judged by the rank the team had then.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Access | ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public sealed class RankEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; init; }

    public RankSystem System { get; init; }

    /// <summary>One-based; a positive integer.</summary>
    public int Rank { get; init; }

    /// <summary>The day the rank was set, the date shown as "set 2026-11-09".</summary>
    public DateOnly AsOf { get; init; }
}

/// <summary>
///     The on-disk shape of <c>team-ranks.json</c>. Refused rather than overwritten when it cannot be read or is
///     newer than this build, the same rule <c>veto-history.json</c> follows.
/// </summary>
public sealed class TeamRanksFile
{
    public const int CurrentSchema = 1;

    public int SchemaVersion { get; init; } = CurrentSchema;

    public List<RankEntry> Entries { get; init; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
