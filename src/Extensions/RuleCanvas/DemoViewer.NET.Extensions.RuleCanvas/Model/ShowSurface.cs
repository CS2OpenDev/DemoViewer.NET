#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     One place a <c>show:</c> block surfaces an entry: a scoreboard entry or a table column, by its
///     position in that sequence so an edit can address it.
/// </summary>
/// <param name="Table">The table's name, or null for the scoreboard.</param>
/// <param name="Index">The entry's index in the scoreboard or in the table's columns.</param>
/// <param name="Stat">The reference as written: <c>kills</c>, <c>ace.count</c>, <c>kast.kast_pct</c>.</param>
/// <param name="Label">The <c>label:</c>, or null.</param>
/// <param name="Group">The scoreboard <c>group:</c>, or null; a column has none.</param>
public sealed record ShowReference(string? Table, int Index, string Stat, string? Label, string? Group)
{
    /// <summary>The values <c>as:</c> takes, in the schema's order.</summary>
    public static IReadOnlyList<string> AsOptions { get; } = ["ticks", "seconds", "time"];

    /// <summary>The boards a scoreboard entry can name.</summary>
    public static IReadOnlyList<string> BoardOptions { get; } = ["round", "match"];

    /// <summary>The scoreboard <c>boards:</c> override as written, or null for the default from the stat's <c>per:</c>; a column has none.</summary>
    public IReadOnlyList<string>? Boards { get; init; }

    /// <summary>The <c>as:</c> display format (<c>ticks</c>, <c>seconds</c>, <c>time</c>), or null for the raw value.</summary>
    public string? As { get; init; }

    public bool IsScoreboard => Table is null;

    /// <summary>The <c>as:</c> format as the file spells it, or null for <see cref="ColumnValueFormat.None" />.</summary>
    public static string? AsText(ColumnValueFormat format) => format switch
    {
        ColumnValueFormat.Ticks => "ticks",
        ColumnValueFormat.Seconds => "seconds",
        ColumnValueFormat.Time => "time",
        _ => null
    };

    /// <summary>Where the entry shows, in words: <c>scoreboard</c> or the table's name.</summary>
    public string Surface => Table ?? "scoreboard";
}

/// <summary>One <c>tables:</c> entry: its name, its grain and its columns.</summary>
public sealed record ShowTable(string Name, string Per, IReadOnlyList<ShowReference> Columns)
{
    /// <summary>An event table's <c>on:</c> trigger as the author spelled it; null on every other grain.</summary>
    public string? On { get; init; }

    /// <summary>The table's line in its file; zero when unknown.</summary>
    public int SourceLine { get; init; }
}

/// <summary>
///     Which entries a ruleset's <c>show:</c> block surfaces and where, read from the loaded document so
///     the answer is the file's. A reference names a stat by its id, a highlight by its id or its
///     <c>&lt;id&gt;.count</c>, and either by its ruleset-qualified spelling.
/// </summary>
public sealed class ShowSurface
{
    private ShowSurface(string rulesetId, IReadOnlyList<ShowReference> scoreboard, IReadOnlyList<ShowTable> tables)
    {
        RulesetId = rulesetId;
        Scoreboard = scoreboard;
        Tables = tables;
    }

    /// <summary>The answer for no document.</summary>
    public static ShowSurface Empty { get; } = new("", [], []);

    public string RulesetId { get; }

    public IReadOnlyList<ShowReference> Scoreboard { get; }

    public IReadOnlyList<ShowTable> Tables { get; }

    /// <summary>Every reference, scoreboard first, then each table's columns in document order.</summary>
    public IEnumerable<ShowReference> All => Scoreboard.Concat(Tables.SelectMany(t => t.Columns));

    public bool IsEmpty => Scoreboard.Count == 0 && Tables.Count == 0;

    /// <summary>Reads the <c>show:</c> block of <paramref name="doc" />; <see cref="Empty" /> for none.</summary>
    public static ShowSurface Of(RulesetDoc? doc)
    {
        if (doc?.Show is not { } show)
        {
            return doc is null ? Empty : new ShowSurface(doc.Id, [], []);
        }

        List<ShowReference> scoreboard = [];
        for (int i = 0; i < show.Scoreboard.Count; i++)
        {
            ScoreboardEntry entry = show.Scoreboard[i];
            scoreboard.Add(new ShowReference(null, i, entry.Stat, entry.Label, entry.Group)
            {
                Boards = entry.Boards is { Count: > 0 } boards ? boards : null, As = ShowReference.AsText(entry.As)
            });
        }

        List<ShowTable> tables = [];
        foreach (TableDef table in show.Tables)
        {
            List<ShowReference> columns = [];
            for (int i = 0; i < table.Columns.Count; i++)
            {
                TableColumn column = table.Columns[i];
                columns.Add(new ShowReference(table.Name, i, column.Stat, column.Label, null) { As = ShowReference.AsText(column.As) });
            }

            tables.Add(new ShowTable(table.Name, table.Per ?? "", columns)
            {
                On = string.IsNullOrWhiteSpace(table.On) ? null : table.On, SourceLine = table.Position.Line
            });
        }

        return new ShowSurface(doc.Id, scoreboard, tables) { ScoreboardLine = show.Position.Line };
    }

    /// <summary>The line the <c>show:</c> block starts on, which is where the scoreboard node jumps to; zero when unknown.</summary>
    public int ScoreboardLine { get; init; }

    /// <summary>The table named <paramref name="name" />, or null.</summary>
    public ShowTable? Table(string name) => Tables.FirstOrDefault(t => t.Name == name);

    /// <summary>Whether <paramref name="stat" /> as written in <c>show:</c> names the entry <paramref name="id" />.</summary>
    public static bool Refers(string stat, string id, string rulesetId)
    {
        ArgumentNullException.ThrowIfNull(stat);
        string bare = stat.StartsWith(rulesetId + ".", StringComparison.Ordinal) ? stat[(rulesetId.Length + 1)..] : stat;
        return bare == id || bare == id + ".count";
    }

    /// <summary>The places <paramref name="id" /> shows, in document order; empty when it is not surfaced.</summary>
    public IReadOnlyList<ShowReference> ReferencesTo(string id) => [.. All.Where(r => Refers(r.Stat, id, RulesetId))];

    /// <summary>
    ///     The badge for an entry: <c>shown: scoreboard as Kills</c>, with every further place after a comma,
    ///     or <c>not shown</c>.
    /// </summary>
    public string BadgeFor(string id)
    {
        IReadOnlyList<ShowReference> places = ReferencesTo(id);
        if (places.Count == 0)
        {
            return "not shown";
        }

        return "shown: " + string.Join(", ", places.Select(p => p.Surface + " as " + (p.Label ?? p.Stat)));
    }
}
