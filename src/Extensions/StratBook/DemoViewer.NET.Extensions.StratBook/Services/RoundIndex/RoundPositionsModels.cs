#region

using System.Text.Json;
using System.Text.Json.Serialization;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

/// <summary>
///     The round positions file: the <c>.dvrp.json.gz</c> sibling of a demo's <c>.dvri.json</c>. Per
///     live round, per sampled step, the alive players' world positions and place ids, the tuples the
///     index builder's walk already had in hand when it closed the row. A Result Card thumbnail is a
///     scene built from one step of this file, and Overlay View reads every step, so neither ever
///     opens a demo: measured, a seek from the demo costs 0.6 to 3.3 s and half a gigabyte of heap per
///     hit, and the render itself half a millisecond.
///     <para>
///         A sibling rather than a block inside the sidecar because the query service deserializes
///         every sidecar at startup and never looks at a position; positions would double that read
///         for nothing. Gzipped because the compact JSON is 170 to 210 KB per demo and 56 to 60 KB
///         zipped, about the size of the sidecar it sits beside. The file carries the same fingerprint
///         as its sidecar, so a reader with the current fingerprint ignores a stale one.
///     </para>
/// </summary>
public sealed class RoundPositionsDocument
{
    /// <summary>The file shape version. 2 added <see cref="Players" />.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    ///     What a tuple means: integer world units, alive slots only, the last sample per slot at the
    ///     sampled tick. In the index fingerprint as <c>pos=</c>, so a change re-indexes rather than
    ///     reinterprets, and a sidecar written before positions existed carries no <c>pos=</c> and is stale.
    ///     2: alive and <see cref="RoundPositionsRound.Ct" /> read from the sample's own <c>IsAlive</c>
    ///     and <c>Team</c> (CS2DemoKit #58) and an empty place is unplaced. Both files are rebuilt, since
    ///     the fingerprint is theirs together: a version 1 <c>.dvri.json</c> counted <c>""</c> as a place
    ///     in its summaries and transitions and gated its tokens on the Round Facts kill tick. The
    ///     sidecar's own <c>schemaVersion</c> stays 1, because its shape did not change and
    ///     <c>SituationIndex</c> holds it equal to <c>RoundIndexStore.Schema</c>.
    ///     3: <see cref="Players" /> names every tuple's slot by SteamID64 and name; tuples keep the slot,
    ///     which is the same-round join to Round Facts.
    ///     4: a step is the first frame at or past its due tick, the cadence's frame, instead of the first
    ///     fourth frame at or past it.
    ///     5: a slot the final roster no longer holds is named by its last <c>player_connect</c> or
    ///     <c>player_disconnect</c>, where it read 0 and "".
    ///     6: a tuple carries the weapon in hand (<see cref="Weapons" />), armour, helmet and kit and money,
    ///     <c>[slot, x, y, z, placeId, weaponId, armor, flags, money]</c>; a file without them reads the five-wide
    ///     tuple with every extra unknown.
    /// </summary>
    public const int PositionSchema = 6;

    /// <summary>The serializer options: camel case and compact like the sidecar, tuples through their converter.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new RoundPositionConverter(), new RoundPositionsPlayerConverter() }
    };

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Identical to the <c>.dvri.json</c> this file belongs to.</summary>
    public string Fingerprint { get; set; } = "";

    public RoundPositionsDemo Demo { get; set; } = new();

    /// <summary>Ticks between sampled steps, the sidecar's value: <c>tick = freezeEndTick + step * cadenceTicks</c>.</summary>
    public int CadenceTicks { get; init; }

    /// <summary>The place table a tuple's place id indexes; <see cref="PlaceCountToken.NullPlace" /> stands for a null place.</summary>
    public List<string> Places { get; init; } = [];

    /// <summary>
    ///     The weapon classes a tuple's weapon id indexes (<c>CWeaponAWP</c>, <c>CAK47</c> ...), or null when no tuple
    ///     carries one: a file written before schema 6, or a fold that had no weapon column.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Weapons { get; set; }

    public List<RoundPositionsRound> Rounds { get; init; } = [];

    /// <summary>The player on each slot a tuple names, in slot order: who the slot is across demos.</summary>
    public List<RoundPositionsPlayer> Players { get; set; } = [];

    /// <summary>The player on a tuple's slot, or null when the file names none.</summary>
    /// <param name="slot">The tuple's slot.</param>
    public RoundPositionsPlayer? PlayerOf(int slot)
    {
        foreach (RoundPositionsPlayer player in Players)
        {
            if (player.Slot == slot)
            {
                return player;
            }
        }

        return null;
    }

    /// <summary>The round with this number, or null.</summary>
    /// <param name="number">The Round Facts round number.</param>
    public RoundPositionsRound? Round(int number) => Rounds.FirstOrDefault(r => r.Number == number);

    /// <summary>The step a frame-clock tick maps to within a round: the inverse of the sidecar's tick rule.</summary>
    /// <param name="round">The round.</param>
    /// <param name="tick">Frame clock.</param>
    public int StepFor(RoundPositionsRound round, int tick)
    {
        ArgumentNullException.ThrowIfNull(round);
        return CadenceTicks <= 0 ? 0 : (tick - round.FreezeEndTick) / CadenceTicks;
    }

    /// <summary>The weapon class a tuple names, or null when it carries none.</summary>
    /// <param name="weaponId">The tuple's weapon id.</param>
    public string? WeaponOf(int weaponId) =>
        Weapons is { } weapons && weaponId >= 0 && weaponId < weapons.Count ? weapons[weaponId] : null;

    /// <summary>The place a tuple names, or null for the reserved null place.</summary>
    /// <param name="placeId">The tuple's place id.</param>
    public string? PlaceOf(int placeId)
    {
        if (placeId < 0 || placeId >= Places.Count)
        {
            return null;
        }

        string place = Places[placeId];
        return string.Equals(place, PlaceCountToken.NullPlace, StringComparison.Ordinal) ? null : place;
    }

    /// <summary>The compact JSON text.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The compact JSON as UTF-8: what the store writes.</summary>
    public byte[] SerializeUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    /// <summary>The document a file's text describes, or null when it does not parse.</summary>
    /// <param name="json">The uncompressed text.</param>
    public static RoundPositionsDocument? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RoundPositionsDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The document UTF-8 JSON describes, or null when it does not parse.</summary>
    /// <param name="utf8">The JSON.</param>
    public static RoundPositionsDocument? TryDeserialize(byte[] utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<RoundPositionsDocument>(utf8, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The join keys, so a reader with a hash can refuse a file that belongs to another demo.</summary>
public sealed class RoundPositionsDemo
{
    public string StableKey { get; init; } = "";

    public string? Sha256 { get; init; }
}

/// <summary>One live round: which slots sat CT, and the alive tuples per sampled step.</summary>
public sealed class RoundPositionsRound
{
    /// <summary>== <c>RoundFacts.Number</c>.</summary>
    public int Number { get; init; }

    /// <summary>Frame clock. Step 0 is sampled here.</summary>
    public int FreezeEndTick { get; init; }

    /// <summary>Slots whose sample was CT this round (CS2DemoKit #58 <c>Team</c>); every other tuple is T.</summary>
    public int[] Ct { get; set; } = [];

    /// <summary>
    ///     Indexed by step. A step the walk did not sample is an empty list, so the list is as long as
    ///     the round's last sampled step plus one and a tuple's tick is always its index times the cadence.
    /// </summary>
    public List<List<RoundPosition>> Pos { get; init; } = [];

    /// <summary>The tuples at a step, or none when the step is out of range or was not sampled.</summary>
    /// <param name="step">The step.</param>
    public IReadOnlyList<RoundPosition> At(int step) =>
        step >= 0 && step < Pos.Count ? Pos[step] : [];
}

/// <summary>
///     The player on one slot, stored as <c>[slot, "steamId64", name]</c>. The SteamID64 is a string so a
///     JSON reader with doubles keeps every digit; a bot reads 0 and is told apart by its name.
/// </summary>
/// <param name="Slot">The controller slot the tuples name.</param>
/// <param name="SteamId64">The player's SteamID64, or 0 when the demo carried none.</param>
/// <param name="Name">The player's name as the demo's roster had it.</param>
public sealed record RoundPositionsPlayer(int Slot, ulong SteamId64, string Name);

/// <summary><c>[slot, "steamId64", name]</c>.</summary>
internal sealed class RoundPositionsPlayerConverter : JsonConverter<RoundPositionsPlayer>
{
    public override RoundPositionsPlayer Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.StartArray);
        reader.Read();
        int slot = reader.GetInt32();
        reader.Read();
        ulong steamId = ulong.Parse(reader.GetString() ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        reader.Read();
        string name = reader.GetString() ?? "";
        reader.Read();
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.EndArray);
        return new RoundPositionsPlayer(slot, steamId, name);
    }

    public override void Write(Utf8JsonWriter writer, RoundPositionsPlayer value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Slot);
        writer.WriteStringValue(value.SteamId64.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteStringValue(value.Name);
        writer.WriteEndArray();
    }
}

/// <summary>
///     One alive player at one sampled step, stored as <c>[slot, x, y, z, placeId]</c> in integer world units, or
///     <c>[slot, x, y, z, placeId, weaponId, armor, flags, money]</c> once it carries the extras (schema 6).
/// </summary>
/// <param name="Slot">The controller slot.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="PlaceId">Into <see cref="RoundPositionsDocument.Places" />.</param>
/// <param name="WeaponId">Into <see cref="RoundPositionsDocument.Weapons" />, or -1 when unknown.</param>
/// <param name="Armor">Armour, or -1 when unknown.</param>
/// <param name="Flags"><see cref="HelmetFlag" /> and <see cref="DefuserFlag" />, or -1 when unknown.</param>
/// <param name="Money">Money, or -1 when unknown.</param>
public readonly record struct RoundPosition(
    int Slot,
    int X,
    int Y,
    int Z,
    int PlaceId,
    int WeaponId = -1,
    int Armor = -1,
    int Flags = -1,
    int Money = -1)
{
    /// <summary>The <see cref="Flags" /> bit for a helmet.</summary>
    public const int HelmetFlag = 1;

    /// <summary>The <see cref="Flags" /> bit for a defuse kit.</summary>
    public const int DefuserFlag = 2;

    /// <summary>True when the tuple carries none of the schema 6 extras.</summary>
    public bool IsBare => WeaponId < 0 && Armor < 0 && Flags < 0 && Money < 0;

    /// <summary>True when it has a helmet; null when unknown.</summary>
    public bool? HasHelmet => Flags < 0 ? null : (Flags & HelmetFlag) != 0;

    /// <summary>True when it has a kit; null when unknown.</summary>
    public bool? HasDefuser => Flags < 0 ? null : (Flags & DefuserFlag) != 0;
}

/// <summary>
///     <c>[slot, x, y, z, placeId]</c> or <c>[slot, x, y, z, placeId, weaponId, armor, flags, money]</c>. A bare
///     tuple writes five wide, so a file without the extras is byte-identical to the schema 5 shape, and a reader
///     takes either.
/// </summary>
internal sealed class RoundPositionConverter : JsonConverter<RoundPosition>
{
    public override RoundPosition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.StartArray);
        Span<int> cells = [-1, 0, 0, 0, 0, -1, -1, -1, -1];
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            int value = reader.GetInt32();
            if (count < cells.Length)
            {
                cells[count] = value;
            }

            count++;
        }

        RoundIndexRunConverter.Expect(ref reader, JsonTokenType.EndArray);
        if (count < 5)
        {
            throw new JsonException($"a position tuple has {count} cells, fewer than five");
        }

        return new RoundPosition(cells[0], cells[1], cells[2], cells[3], cells[4], cells[5], cells[6], cells[7],
            cells[8]);
    }

    public override void Write(Utf8JsonWriter writer, RoundPosition value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Slot);
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.PlaceId);
        if (!value.IsBare)
        {
            writer.WriteNumberValue(value.WeaponId);
            writer.WriteNumberValue(value.Armor);
            writer.WriteNumberValue(value.Flags);
            writer.WriteNumberValue(value.Money);
        }

        writer.WriteEndArray();
    }
}

/// <summary>The one build that yields both files, so they can never disagree on a row.</summary>
/// <param name="Index">The <c>.dvri.json</c> document.</param>
/// <param name="Positions">The <c>.dvrp.json.gz</c> document, carrying the same fingerprint.</param>
/// <param name="Disagreements">
///     Per round, how often a sample's own <c>IsAlive</c>/<c>Team</c> disagreed with Round Facts'
///     <c>Slots</c>/<c>Kills</c> (CS2DemoKit #58 cross-check). The sample always won; this is
///     diagnostic only.
/// </param>
public sealed record RoundIndexBuild(
    RoundIndexDocument Index,
    RoundPositionsDocument Positions,
    IReadOnlyList<RoundIndexDisagreement> Disagreements)
{
    /// <summary>World units rounded to the integer a tuple stores; a marker at thumbnail size is thirty units wide.</summary>
    /// <param name="value">A world coordinate.</param>
    public static int Quantize(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>One round's disagreement count between a sample's own fields and Round Facts, see <see cref="RoundIndexBuild.Disagreements" />.</summary>
/// <param name="Round">The round number.</param>
/// <param name="SideMismatches">Slots where the sample's <c>Team</c> disagreed with the row's seated side.</param>
/// <param name="AliveMismatches">Slots the row's <c>Kills</c> call dead where the sample's <c>IsAlive</c> is true.</param>
public sealed record RoundIndexDisagreement(int Round, int SideMismatches, int AliveMismatches);

/// <summary>
///     Counts, per round, how often a walk sample's own <c>IsAlive</c>/<c>Team</c> disagreed with the
///     Round Facts row it was cross-checked against (CS2DemoKit #58). Shared by
///     <c>RoundIndexBuilder</c> and <c>RoundOccupancyBuilder</c>, the two walks that still hold the
///     facts-derived roster beside the sample. Not a gate: the sample always wins.
/// </summary>
public sealed class RoundIndexDisagreementTally
{
    private readonly Dictionary<int, (int Side, int Alive)> _byRound = [];

    /// <summary>Adds one slot's disagreement, if any, to its round's counts.</summary>
    /// <param name="round">The round number.</param>
    /// <param name="sideMismatch">The sample's <c>Team</c> disagreed with the row's seated side.</param>
    /// <param name="aliveMismatch">The row's <c>Kills</c> call the slot dead where the sample is alive.</param>
    public void Record(int round, bool sideMismatch, bool aliveMismatch)
    {
        if (!sideMismatch && !aliveMismatch)
        {
            return;
        }

        (int Side, int Alive) counts = _byRound.GetValueOrDefault(round);
        _byRound[round] = (counts.Side + (sideMismatch ? 1 : 0), counts.Alive + (aliveMismatch ? 1 : 0));
    }

    /// <summary>The tallied rounds, in round order; empty when nothing disagreed.</summary>
    public IReadOnlyList<RoundIndexDisagreement> ToDisagreements() =>
    [
        .. _byRound.OrderBy(kv => kv.Key)
            .Select(kv => new RoundIndexDisagreement(kv.Key, kv.Value.Side, kv.Value.Alive))
    ];
}
