#region

using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Extensions.Sdk.Ui.Theming;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     A parsed theme file: what the file itself sets, before anything is filled in from the base. Produced by
///     <see cref="ThemeJson" /> from a user drop-in under <c>&lt;config&gt;/themes/</c> or an embedded built-in
///     JSON (High-Contrast / E-Girl). <see cref="ThemePalette.Resolve" /> turns it into a complete palette.
/// </summary>
/// <param name="Id">Stable id, persisted in <c>settings.json</c> and used as the <see cref="ThemeVariant" /> key.</param>
/// <param name="DisplayName">Human-readable name shown in the Settings theme picker.</param>
/// <param name="BaseVariant">The Light or Dark palette every omitted token (and FluentTheme colour) inherits.</param>
/// <param name="Tokens">The colour tokens this theme sets (a subset of <see cref="ThemeTokens.All" />).</param>
public sealed record ThemeDefinition(
    string Id,
    string DisplayName,
    ThemeVariant BaseVariant,
    IReadOnlyDictionary<string, Color> Tokens)
{
    /// <summary>The gradient tokens this theme sets.</summary>
    public IReadOnlyDictionary<string, ThemeGradient> Gradients { get; init; } =
        new Dictionary<string, ThemeGradient>();

    /// <summary>
    ///     Keys this version of the app does not know, with their raw JSON. Kept so a save writes them back
    ///     instead of dropping what a newer app version, or the user, put there.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnknownTokens { get; init; } = new Dictionary<string, string>();

    /// <summary>True when the file sets <paramref name="token" />, as a colour or a gradient.</summary>
    public bool Sets(string token) => Tokens.ContainsKey(token) || Gradients.ContainsKey(token);
}

/// <summary>The outcome of reading one theme file.</summary>
/// <param name="Definition">The theme, or null when the file is not usable.</param>
/// <param name="Error">Why the file was not usable, in words for the UI. Null on success.</param>
/// <param name="BadValues">Known tokens whose value could not be read, and so fall back to the base.</param>
public sealed record ThemeParseResult(ThemeDefinition? Definition, string? Error, IReadOnlyList<string> BadValues);

/// <summary>
///     Safe JSON reader for theme definitions (T3). A theme file is pure DATA:
///     <c>{ id, name, base, tokens{ key: "#RRGGBB" | gradient } }</c>, parsed with no object instantiation or
///     code execution, so loading an untrusted drop-in is never code-exec (unlike runtime AXAML). Malformed
///     input degrades gracefully: an unparseable file yields an error; a bad token value is skipped while the
///     rest of the file loads, and an unknown key is kept aside, never applied.
///     <para>
///         A gradient token is an object: <c>{ "start": "0%,0%", "end": "0%,100%", "stops": [ { "offset": 0,
///         "color": "#C805050F" }, ... ] }</c>.
///     </para>
/// </summary>
public static class ThemeJson
{
    private static readonly JsonDocumentOptions _options = new()
    {
        CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
    };

    private static readonly HashSet<string> _known = new(ThemeTokens.All, StringComparer.Ordinal);

    /// <summary>
    ///     Parses <paramref name="json" /> into a <see cref="ThemeDefinition" />, or returns <c>null</c> if it is
    ///     not a usable theme. <paramref name="sourceLabel" /> names the source in a diagnostic.
    /// </summary>
    public static ThemeDefinition? TryParse(string json, string sourceLabel) => Parse(json, sourceLabel).Definition;

    /// <summary>Parses <paramref name="json" />, saying why when it is not a usable theme.</summary>
    public static ThemeParseResult Parse(string json, string sourceLabel)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Fail("The file is empty.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, _options);
        }
        catch (JsonException e)
        {
            return Fail($"It is not valid JSON (line {e.LineNumber + 1}).");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Fail("It is not a JSON object.");
            }

            string? id = StringProperty(root, "id")?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                return Fail("It has no \"id\".");
            }

            string? name = StringProperty(root, "name")?.Trim();
            ThemeVariant baseVariant = ParseBase(StringProperty(root, "base"));

            Dictionary<string, Color> tokens = new(StringComparer.Ordinal);
            Dictionary<string, ThemeGradient> gradients = new(StringComparer.Ordinal);
            Dictionary<string, string> unknown = new(StringComparer.Ordinal);
            List<string> bad = [];
            if (Property(root, "tokens") is { ValueKind: JsonValueKind.Object } tokenObject)
            {
                foreach (JsonProperty entry in tokenObject.EnumerateObject())
                {
                    string key = entry.Name.Trim();
                    if (key.Length == 0)
                    {
                        continue;
                    }

                    if (!_known.Contains(key))
                    {
                        unknown[key] = entry.Value.GetRawText();
                        continue;
                    }

                    if (entry.Value.ValueKind == JsonValueKind.String
                        && TryParseColor(entry.Value.GetString()!, out Color color))
                    {
                        tokens[key] = color;
                    }
                    else if (entry.Value.ValueKind == JsonValueKind.Object
                             && TryParseGradient(entry.Value, out ThemeGradient? gradient))
                    {
                        gradients[key] = gradient;
                    }
                    else
                    {
                        bad.Add(key);
                    }
                }
            }

            ThemeDefinition def = new(id, string.IsNullOrEmpty(name) ? id : name, baseVariant, tokens)
            {
                Gradients = gradients, UnknownTokens = unknown
            };
            return new ThemeParseResult(def, null, bad);
        }

        static ThemeParseResult Fail(string reason) => new(null, reason, []);
    }

    // Case-insensitive property lookup: the old reader bound with PropertyNameCaseInsensitive, so "Id" or
    // "Tokens" in a hand-written file must keep working.
    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    private static string? StringProperty(JsonElement obj, string name) =>
        Property(obj, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    // "light" → Light, anything else (incl. null / "dark") → Dark. Dark is the app's canonical base, so an
    // unspecified or unrecognized base defaults to it.
    private static ThemeVariant ParseBase(string? baseName) =>
        string.Equals(baseName?.Trim(), "light", StringComparison.OrdinalIgnoreCase)
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

    /// <summary>The file spelling of a base variant: <c>"light"</c> or <c>"dark"</c>.</summary>
    public static string BaseName(ThemeVariant baseVariant) => baseVariant == ThemeVariant.Light ? "light" : "dark";

    // Color.Parse throws on a bad string; wrap it so one malformed token never fails the whole file.
    public static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            color = Color.Parse(hex.Trim());
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            color = default;
            return false;
        }
    }

    /// <summary><c>#RRGGBB</c> for an opaque colour, <c>#AARRGGBB</c> otherwise.</summary>
    public static string FormatColor(Color c) =>
        c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryParseGradient(JsonElement obj,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ThemeGradient? gradient)
    {
        gradient = null;
        try
        {
            RelativePoint start = RelativePoint.Parse(StringProperty(obj, "start") ?? "0%,0%");
            RelativePoint end = RelativePoint.Parse(StringProperty(obj, "end") ?? "0%,100%");
            if (Property(obj, "stops") is not { ValueKind: JsonValueKind.Array } stops)
            {
                return false;
            }

            List<ThemeGradientStop> list = [];
            foreach (JsonElement stop in stops.EnumerateArray())
            {
                if (stop.ValueKind != JsonValueKind.Object
                    || Property(stop, "offset") is not { ValueKind: JsonValueKind.Number } offset
                    || StringProperty(stop, "color") is not { } hex
                    || !TryParseColor(hex, out Color color))
                {
                    return false;
                }

                list.Add(new ThemeGradientStop(offset.GetDouble(), color));
            }

            if (list.Count < 2)
            {
                return false;
            }

            gradient = new ThemeGradient(start, end, list);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Formats a relative point the way a theme file spells it (<c>"0%,100%"</c>).</summary>
    public static string FormatPoint(RelativePoint p) =>
        p.Unit == RelativeUnit.Relative
            ? string.Create(CultureInfo.InvariantCulture, $"{p.Point.X * 100:0.##}%,{p.Point.Y * 100:0.##}%")
            : string.Create(CultureInfo.InvariantCulture, $"{p.Point.X:0.##},{p.Point.Y:0.##}");
}
