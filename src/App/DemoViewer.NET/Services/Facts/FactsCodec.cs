#region

using System.Buffers;
using System.Collections;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using CS2DemoKit.Analysis.Output;

#endregion

namespace DemoViewer.NET.Services.Facts;

/// <summary>
///     A <see cref="FactTable" /> as the gzipped JSON sidecar it is stored in, and the engine's table as one. A
///     number with a fraction is written with a decimal point or an exponent and a whole number without, so the
///     two kinds read back as they were written.
/// </summary>
internal static class FactsCodec
{
    private const int Format = 1;

    // The most a sidecar may inflate to, the record sidecars' ceiling. A table is a few megabytes at the most;
    // a sidecar that came in a transfer bundle is whatever the bundle's author put there, and a few kilobytes
    // of gzip can inflate to gigabytes.
    private const int MaxInflated = 256 * 1024 * 1024;

    // JSON lets a name repeat inside one object. The encoder never writes that, and a column with two cells has
    // no one answer, so the parse refuses it like any other malformed document.
    private static readonly JsonDocumentOptions _strict = new() { AllowDuplicateProperties = false };

    /// <summary>The engine's table as the SDK's, every cell normalized.</summary>
    public static FactTable FromMetric(MetricTable table, FactKey key, string fingerprint, int schema, string grain) =>
        new(key, fingerprint, schema, grain,
            [.. table.DimensionColumns],
            [.. table.ValueColumns],
            new Dictionary<string, string>(table.ColumnClocks, StringComparer.Ordinal),
            [
                .. table.Rows.Select(r => new FactRow(
                    r.Dimensions.ToDictionary(c => c.Key, c => FromEngine(c.Value), StringComparer.Ordinal),
                    r.Values.ToDictionary(c => c.Key, c => FromEngine(c.Value), StringComparer.Ordinal)))
            ]);

    /// <summary>An engine cell: null, a boolean, a whole number, a number, text, or a list of those.</summary>
    public static FactValue FromEngine(object? value) => value switch
    {
        null => FactValue.Null,
        FactValue fact => fact,
        bool flag => FactValue.Of(flag),
        string text => FactValue.Of(text),
        sbyte or byte or short or ushort or int or uint or long => FactValue.Of(Convert.ToInt64(value,
            CultureInfo.InvariantCulture)),
        ulong big => big <= long.MaxValue ? FactValue.Of((long)big) : FactValue.Of(big),
        float or double or decimal => FactValue.Of(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        JsonElement element => FromJson(element),
        IEnumerable items => FactValue.Of(items.Cast<object?>().Select(FromEngine)),
        _ => FactValue.Of(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    public static byte[] Encode(FactTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, true))
        using (Utf8JsonWriter json = new(gzip))
        {
            json.WriteStartObject();
            json.WriteNumber("format", Format);
            json.WriteString("ruleset", table.Key.RulesetId);
            json.WriteString("output", table.Key.Output);
            json.WriteString("fingerprint", table.Fingerprint);
            json.WriteNumber("schema", table.Schema);
            json.WriteString("grain", table.Grain);
            WriteStrings(json, "dimensions", table.Dimensions);
            WriteStrings(json, "values", table.Values);
            json.WriteStartObject("clocks");
            foreach ((string column, string clock) in table.ColumnClocks)
            {
                json.WriteString(column, clock);
            }

            json.WriteEndObject();
            json.WriteStartArray("rows");
            foreach (FactRow row in table.Rows)
            {
                json.WriteStartObject();
                WriteCells(json, "d", row.Dimensions);
                WriteCells(json, "v", row.Values);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The table in <paramref name="bytes" />, or null when they are not one this format reads.</summary>
    public static FactTable? Decode(byte[] bytes) => DecodeWithin(bytes, MaxInflated);

    /// <summary><see cref="Decode" /> with the inflate ceiling given: for tests.</summary>
    internal static FactTable? DecodeWithin(byte[] bytes, int maxInflated)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            if (Inflate(bytes, maxInflated) is not { } json)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(json, _strict);
            JsonElement root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != Format)
            {
                return null;
            }

            return new FactTable(
                new FactKey(Name(root.GetProperty("ruleset")), Name(root.GetProperty("output"))),
                Name(root.GetProperty("fingerprint")),
                root.GetProperty("schema").GetInt32(),
                root.GetProperty("grain").GetString() ?? "",
                [.. root.GetProperty("dimensions").EnumerateArray().Select(Name)],
                [.. root.GetProperty("values").EnumerateArray().Select(Name)],
                root.GetProperty("clocks").EnumerateObject()
                    .ToDictionary(p => p.Name, p => Name(p.Value), StringComparer.Ordinal),
                [
                    .. root.GetProperty("rows").EnumerateArray().Select(r => new FactRow(
                        ReadCells(r.GetProperty("d")),
                        ReadCells(r.GetProperty("v"))))
                ]);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException
                                       or InvalidOperationException
                                       or FormatException or IOException)
        {
            return null;
        }
    }

    // The table's names are not nullable, and a reader of the table takes them as present. A JSON null where one
    // belongs makes the file one this format does not read.
    private static string Name(JsonElement element) =>
        element.GetString() ?? throw new InvalidDataException("A name is null.");

    // The inflated bytes, or null once they pass max: nothing past the ceiling is read.
    private static ReadOnlyMemory<byte>? Inflate(byte[] gzipped, int max)
    {
        using MemoryStream input = new(gzipped, false);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream inflated = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (inflated.Length + read > max)
                {
                    return null;
                }

                inflated.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return inflated.GetBuffer().AsMemory(0, (int)inflated.Length);
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);
        foreach (string value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    private static void WriteCells(Utf8JsonWriter json, string name, IReadOnlyDictionary<string, FactValue> cells)
    {
        json.WriteStartObject(name);
        foreach ((string column, FactValue value) in cells)
        {
            json.WritePropertyName(column);
            WriteValue(json, value);
        }

        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, FactValue value)
    {
        switch (value.Kind)
        {
            case FactValueKind.Whole:
                json.WriteNumberValue(value.AsInteger()!.Value);
                break;
            case FactValueKind.Number:
                double number = value.AsNumber()!.Value;
                if (!double.IsFinite(number))
                {
                    // JSON has no NaN or infinity; the text form keeps the value readable.
                    json.WriteStringValue(number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                }

                string text = number.ToString("R", CultureInfo.InvariantCulture);
                json.WriteRawValue(text.Contains('.') || text.Contains('E') ? text : text + ".0");
                break;
            case FactValueKind.Boolean:
                json.WriteBooleanValue(value.AsBoolean()!.Value);
                break;
            case FactValueKind.Text:
                json.WriteStringValue(value.AsText());
                break;
            case FactValueKind.List:
                json.WriteStartArray();
                foreach (FactValue item in value.Items)
                {
                    WriteValue(json, item);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteNullValue();
                break;
        }
    }

    private static Dictionary<string, FactValue> ReadCells(JsonElement cells) =>
        cells.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal);

    private static FactValue FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => FactValue.Of(true),
        JsonValueKind.False => FactValue.Of(false),
        JsonValueKind.String => FactValue.Of(element.GetString()),
        JsonValueKind.Number => IsWhole(element.GetRawText()) && element.TryGetInt64(out long whole)
            ? FactValue.Of(whole)
            : FactValue.Of(element.GetDouble()),
        JsonValueKind.Array => FactValue.Of(element.EnumerateArray().Select(FromJson)),
        _ => FactValue.Null
    };

    private static bool IsWhole(string raw) => raw.IndexOfAny(['.', 'e', 'E']) < 0;
}
