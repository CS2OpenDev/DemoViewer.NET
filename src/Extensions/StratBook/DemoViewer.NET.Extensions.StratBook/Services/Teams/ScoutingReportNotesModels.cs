#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     What the user did to one team's Scouting Report: the findings
///     starred for the one-pager, the generated lines rewritten or left out, the free notes added beside
///     them, and the summary that heads the export. A finding is addressed by its key (the section and
///     what it counts, never an index), so a star or an edit survives a rebuild that reorders or adds
///     findings; one whose finding no longer exists is kept and simply not shown.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Access | ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public sealed class ScoutingReportTeamNotes
{
    public Guid TeamId { get; init; }

    /// <summary>The user's own paragraph at the top of both forms; empty prints nothing.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Keys of the findings on the one-pager.</summary>
    public List<string> Starred { get; set; } = [];

    /// <summary>A finding's rewritten text by key. Absent means the generated line stands.</summary>
    public Dictionary<string, string> Edits { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys of generated findings left out of both forms.</summary>
    public List<string> Hidden { get; set; } = [];

    /// <summary>Findings the user wrote, in the order added.</summary>
    public List<ScoutingReportNote> Notes { get; set; } = [];

    /// <summary>
    ///     Keys of suggested lines already starred once on the user's behalf. A suggested line starts starred
    ///     the first time it is built and never again, so unstarring one sticks.
    /// </summary>
    public List<string> Seeded { get; set; } = [];
}

/// <summary>One finding the user wrote by hand. Its key is <c>note:</c> plus <see cref="Id" />.</summary>
[UsedImplicitly(ImplicitUseKindFlags.Access | ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public sealed class ScoutingReportNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = "";
}

/// <summary>
///     <c>&lt;config&gt;/scouting-report-notes.json</c>, beside <c>teams.json</c> and <c>veto-history.json</c>.
///     Refused rather than overwritten when it cannot be read, the same rule both follow.
/// </summary>
public sealed class ScoutingReportNotesFile
{
    /// <summary>
    ///     2: keys carry their scope, <c>map|side|layer|buy|window|text</c>
    ///     (<see cref="ScoutingReportStarKeys" />); a schema-1 file is remapped as it is read.
    /// </summary>
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; init; } = CurrentSchema;

    public List<ScoutingReportTeamNotes> Teams { get; init; } = [];

    /// <summary>Same shape as <see cref="VetoHistoryFile.JsonOptions" />.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
