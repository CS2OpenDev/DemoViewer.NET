#region

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>One table row by column name, the shape both a pass's rows and a run's rows fold from.</summary>
/// <param name="Dimensions">The row's dimensions: <c>tick</c>, <c>player_slot</c>, <c>team</c>.</param>
/// <param name="Values">The row's columns by label.</param>
public readonly record struct RoundTableRow(
    IReadOnlyDictionary<string, object?> Dimensions,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>A player's spot at an event: world position and place.</summary>
public readonly record struct EventSpot(float X, float Y, float Z, string? Place);

/// <summary>One kill row: the killer's.</summary>
public sealed record KillEventRow(
    int Tick,
    int AttackerSlot,
    int VictimSlot,
    int AttackerSide,
    string? Weapon,
    string? WeaponClass,
    bool Headshot,
    bool Trade,
    int TradedSlot,
    EventSpot? Attacker);

/// <summary>One death row: the victim's.</summary>
public sealed record DeathEventRow(
    int Tick,
    int VictimSlot,
    int AttackerSlot,
    int VictimSide,
    string? WeaponClass,
    EventSpot? Victim);

/// <summary>One plant row: the planter's own.</summary>
public sealed record PlantEventRow(int Tick, int PlanterSlot, int SiteEntity, EventSpot? Spot);

/// <summary>The event tables of one demo.</summary>
public sealed record RoundEventRows(
    IReadOnlyList<KillEventRow> Kills,
    IReadOnlyList<DeathEventRow> Deaths,
    IReadOnlyList<PlantEventRow> Plants)
{
    public static RoundEventRows Empty { get; } = new([], [], []);
}

/// <summary>
///     The round index's event ruleset: every kill and death with the positions of both players, and every plant
///     with the planter's, as <c>player_event</c> tables. The round index pass reads them off the visit's rules run
///     and folds them into the captures part beside the positions.
/// </summary>
public static class RoundEventsTable
{
    /// <summary>The extension's own name for the ruleset.</summary>
    public const string RulesetName = "roundindex_events";

    public const string KillsTable = "roundindex_kills";

    public const string DeathsTable = "roundindex_deaths";

    public const string PlantsTable = "roundindex_plants";

    private const string ResourceName = "DemoViewer.NET.Extensions.StratBook.Rulesets.roundindex_events.rules.yaml";

    /// <summary>Every table the ruleset declares.</summary>
    public static IReadOnlyList<string> Tables { get; } = [KillsTable, DeathsTable, PlantsTable];

    /// <summary>The ruleset's qualified id, as its YAML spells it.</summary>
    public static string RulesetId { get; } = RulesetContribution.QualifiedId(StratBookPack.PackId, RulesetName);

    /// <summary>What the rows mean: the shipped YAML and the engine build, hashed. The index fingerprint carries it.</summary>
    public static string Identity { get; } = ComputeIdentity();

    /// <summary>The ruleset's YAML.</summary>
    public static Stream OpenYaml() =>
        typeof(RoundEventsTable).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"the embedded ruleset '{ResourceName}' is missing");

    private static string ReadYaml()
    {
        using StreamReader reader = new(OpenYaml());
        return reader.ReadToEnd();
    }

    /// <summary>The shipped ruleset, parsed. Throws when it does not load.</summary>
    public static RulesetDoc LoadRuleset()
    {
        RuleConfigLoadResult read = YamlConfigLoader.LoadDocuments([(RulesetName + ".rules.yaml", ReadYaml())]);
        return read is { Success: true, Rulesets.Count: 1 }
            ? read.Rulesets[0]
            : throw new InvalidOperationException("the round index event ruleset does not load: "
                + string.Join("; ", read.Errors));
    }

    /// <summary>The tables as a pass reads them off its visit's run.</summary>
    /// <param name="table">The pass context's table reader.</param>
    public static RoundEventRows Read(Func<string, IReadOnlyList<FactRow>> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Read(name => table(name).Select(r => new RoundTableRow(RoundIndexTable.Cells(r.Dimensions),
            RoundIndexTable.Cells(r.Values))));
    }

    /// <summary>The tables off a run made outside a visit; absent tables read as empty.</summary>
    /// <param name="tables">The run's projected tables.</param>
    public static RoundEventRows Read(IReadOnlyList<MetricTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        Dictionary<string, MetricTable> byName = [];
        foreach (MetricTable t in tables)
        {
            byName[t.Name] = t;
        }

        return Read(name => byName.TryGetValue(name, out MetricTable? t)
            ? t.Rows.Select(r => new RoundTableRow(r.Dimensions, r.Values))
            : []);
    }

    /// <summary>Folds every table's rows, by name.</summary>
    /// <param name="rows">The rows of a table, by its name.</param>
    public static RoundEventRows Read(Func<string, IEnumerable<RoundTableRow>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        List<KillEventRow> kills = [];
        foreach (RoundTableRow row in rows(KillsTable))
        {
            if (Int(row.Dimensions, "tick") is not { } tick || Int(row.Dimensions, "player_slot") is not { } slot)
            {
                continue;
            }

            kills.Add(new KillEventRow(tick, slot, Int(row.Values, "victim_slot") ?? -1,
                Int(row.Values, "side") ?? Int(row.Dimensions, "team") ?? 0,
                Str(row.Values, "weapon"), Str(row.Values, "weapon_class"),
                Int(row.Values, "headshot") is > 0, Int(row.Values, "trade") is > 0,
                Int(row.Values, "traded_slot") ?? -1, Spot(row.Values)));
        }

        List<DeathEventRow> deaths = [];
        foreach (RoundTableRow row in rows(DeathsTable))
        {
            if (Int(row.Dimensions, "tick") is not { } tick || Int(row.Dimensions, "player_slot") is not { } slot)
            {
                continue;
            }

            deaths.Add(new DeathEventRow(tick, slot, Int(row.Values, "attacker_slot") ?? -1,
                Int(row.Values, "side") ?? Int(row.Dimensions, "team") ?? 0,
                Str(row.Values, "weapon_class"), Spot(row.Values)));
        }

        List<PlantEventRow> plants = [];
        foreach (RoundTableRow row in rows(PlantsTable))
        {
            // Every player gets the plant's row; only the planter's own carries the planter's position.
            if (Int(row.Dimensions, "tick") is not { } tick || Int(row.Dimensions, "player_slot") is not { } slot
                || Int(row.Values, "planter_slot") is not { } planter || planter != slot)
            {
                continue;
            }

            plants.Add(new PlantEventRow(tick, slot, Int(row.Values, "site") ?? -1, Spot(row.Values)));
        }

        kills.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.VictimSlot.CompareTo(b.VictimSlot));
        deaths.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.VictimSlot.CompareTo(b.VictimSlot));
        plants.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        return new RoundEventRows(kills, deaths, plants);
    }

    private static EventSpot? Spot(IReadOnlyDictionary<string, object?> values) =>
        Float(values, "pos_x") is { } x && Float(values, "pos_y") is { } y && Float(values, "pos_z") is { } z
            ? new EventSpot(x, y, z, Str(values, "place") is { Length: > 0 } place ? place : null)
            : null;

    private static string ComputeIdentity()
    {
        Assembly assembly = typeof(DemoAnalysis).Assembly;
        string engine = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(ReadYaml() + "\n" + engine));
        return Convert.ToHexStringLower(hash, 0, 16);
    }

    internal static int? Int(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            bool b => b ? 1 : 0,
            string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null,
            IConvertible c => c.ToInt32(CultureInfo.InvariantCulture),
            _ => null
        };

    internal static float? Float(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            float f => f,
            double d => (float)d,
            string => null,
            IConvertible c => c.ToSingle(CultureInfo.InvariantCulture),
            _ => null
        };

    internal static string? Str(IReadOnlyDictionary<string, object?> row, string column) =>
        row.GetValueOrDefault(column) switch
        {
            null => null,
            string s => s,
            IConvertible c => c.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
}
