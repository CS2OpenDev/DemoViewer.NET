#region

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>One group of tokens, in palette order, as the theme files and the editor show them.</summary>
/// <param name="Title">The section's comment line.</param>
/// <param name="Tokens">Its tokens, in palette order.</param>
public sealed record ThemeSection(string Title, IReadOnlyList<string> Tokens);

/// <summary>
///     Writes a <see cref="ThemePalette" /> as a theme file: base variant plus every token in palette order,
///     grouped under section comments, with tokens the theme inherits marked <c>// base</c>. The same text is a
///     user drop-in, a customized copy's starting point, and the reference files in <c>samples/Themes</c>, which
///     are regenerated with <c>UiCapture export-themes</c> and held equal to this output by a test.
/// </summary>
public static class ThemeExport
{
    // Each section starts at its first token; every token after it, in palette order, belongs to it until the
    // next start. A token added to the palette later joins the section it is placed in.
    private static readonly (string FirstToken, string Title)[] _starts =
    [
        ("ShellBg", "Surfaces and shell backgrounds"),
        ("BorderSubtle", "Borders and dividers"),
        ("TextHexGutter", "Text ramp, dimmest first. TextOnAccent is text drawn on an accent fill"),
        ("AccentInteractive", "Accents"),
        ("StatPositive", "Stats board: heat ramp, in-cell bar, composition slots. Retint a family whole or not at all"),
        ("PrimaryButtonHover", "Button hover and the changed-row wash"),
        ("HexSwatchSelected", "Hex view highlight swatches (alpha matters)"),
        ("ClassifierBlue", "Classifier accents: message, log and badge categories"),
        ("AccentInfo", "Single-use accents. MapAccentRef is read for saturation and value only; its hue is ignored"),
        ("LibraryCardScrim", "Library cards, drawn over the baked radar thumbnail"),
        ("Pb2dPanelBg", "2D playback HUD chrome"),
        ("Pb2dTeamCt", "2D playback game colours: teams, kill-feed badges, status"),
        ("MsgHeaderNet", "Message card header tints, per message category"),
        ("WtVarintBg", "Wire-type badges on expanded message rows"),
        ("SyntaxComment", "Rule editor syntax highlighting"),
        ("Pb2dCanvasBg", "2D radar canvas"),
        ("GraphCanvasBg", "Analysis graph")
    ];

    private static readonly Lazy<IReadOnlyList<ThemeSection>> _sections = new(BuildSections);

    private const int KeyWidth = 31;
    private const int ValueWidth = 13;

    /// <summary>Every token, grouped into sections, in palette order.</summary>
    public static IReadOnlyList<ThemeSection> Sections => _sections.Value;

    private static List<ThemeSection> BuildSections()
    {
        Dictionary<string, string> titles =
            _starts.ToDictionary(s => s.FirstToken, s => s.Title, StringComparer.Ordinal);
        List<ThemeSection> sections = [];
        string title = _starts[0].Title;
        List<string> current = [];
        foreach (string token in ThemeTokens.All)
        {
            if (titles.TryGetValue(token, out string? next) && current.Count > 0)
            {
                sections.Add(new ThemeSection(title, current));
                current = [];
            }

            if (next is not null)
            {
                title = next;
            }

            current.Add(token);
        }

        if (current.Count > 0)
        {
            sections.Add(new ThemeSection(title, current));
        }

        return sections;
    }

    /// <summary>
    ///     The theme file for <paramref name="palette" />. <paramref name="id" /> and <paramref name="name" />
    ///     replace the palette's own, for a copy or a sample. With <paramref name="includeInherited" /> false the
    ///     tokens the theme inherits are left out, so they stay missing (and keep following the base) after a save.
    /// </summary>
    public static string ToJson(ThemePalette palette, string? id = null, string? name = null,
        bool includeInherited = true,
        string? title = null)
    {
        id ??= palette.Id;
        name ??= palette.DisplayName;
        string baseName = ThemeJson.BaseName(palette.BaseVariant);

        StringBuilder sb = new();
        sb.Append("// ").Append(title ?? name).Append(", written out in full.\n");
        sb.Append("//\n");
        sb.Append("// A complete user theme: every colour token the app reads, with the value this theme\n");
        sb.Append("// resolves to. Copy it, change the id and name, and edit what you like. Delete a line and\n");
        sb.Append("// that token falls back to the base palette instead. Lines marked \"base\" are the\n");
        sb.Append("// ").Append(baseName).Append(" palette's own value, which this theme inherits rather than sets.\n");
        sb.Append("//\n");
        sb.Append("// Colours are \"#RRGGBB\" (opaque) or \"#AARRGGBB\", where AA is the alpha. Several overlay\n");
        sb.Append("// and canvas tokens are translucent on purpose. A gradient is an object with start, end\n");
        sb.Append("// and stops. Token meanings are listed in docs/ui/theme-token-catalog.md.\n");
        sb.Append("//\n");
        sb.Append("// Install: put this file in the themes folder (Windows %APPDATA%\\DemoViewer.NET\\themes\\,\n");
        sb.Append(
            "// macOS ~/Library/Application Support/DemoViewer.NET/themes/, Linux ~/.config/DemoViewer.NET/themes/)\n");
        sb.Append("// and press Settings > Theme > Reload themes. An id that matches a built-in theme is ignored.\n");
        sb.Append("{\n");
        sb.Append("  \"id\": ").Append(Quote(id)).Append(",\n");
        sb.Append("  \"name\": ").Append(Quote(name)).Append(",\n");
        sb.Append("  // \"dark\" or \"light\": the palette any token left out comes from, and the look of the\n");
        sb.Append("  // stock controls (scroll bars, list selection) that no token covers.\n");
        sb.Append("  \"base\": ").Append(Quote(baseName)).Append(",\n");
        sb.Append("  \"tokens\": {\n");

        // Lines are gathered first so the comma after the last entry can be left off.
        List<(string? Comment, string[] Entry, bool Base)> lines = [];
        foreach (ThemeSection section in Sections)
        {
            bool first = true;
            foreach (string token in section.Tokens)
            {
                if (!includeInherited && palette.Inherited.Contains(token))
                {
                    continue;
                }

                string[] entry;
                if (palette.Colors.TryGetValue(token, out Avalonia.Media.Color color))
                {
                    entry = [Pad(Quote(token) + ":", KeyWidth) + Quote(ThemeJson.FormatColor(color))];
                }
                else if (palette.Gradients.TryGetValue(token, out ThemeGradient? gradient))
                {
                    entry = GradientLines(token, gradient);
                }
                else
                {
                    continue;
                }

                lines.Add((first ? section.Title : null, entry, palette.Inherited.Contains(token)));
                first = false;
            }
        }

        bool firstUnknown = true;
        foreach ((string key, string raw) in palette.UnknownTokens.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            lines.Add((firstUnknown ? "Not used by this version of the app, kept as written" : null,
                [Pad(Quote(key) + ":", KeyWidth) + raw], false));
            firstUnknown = false;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            (string? comment, string[] entry, bool inherited) = lines[i];
            if (comment is not null)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }

                sb.Append("    // ").Append(comment).Append('\n');
            }

            bool last = i == lines.Count - 1;
            for (int j = 0; j < entry.Length; j++)
            {
                string text = entry[j];
                bool tail = j == entry.Length - 1;
                if (tail && !last)
                {
                    text += ",";
                }

                if (tail && inherited)
                {
                    text = entry.Length == 1 ? PadValue(text) + "// base" : text + " // base";
                }

                sb.Append("    ").Append(text).Append('\n');
            }
        }

        sb.Append("  }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>The file name of a built-in's reference copy in <c>samples/Themes</c>.</summary>
    public static string SampleFileName(ThemePalette builtIn) => builtIn.Id + ".json";

    /// <summary>
    ///     A built-in's reference copy for <c>samples/Themes</c>: its full export under the id
    ///     <c>&lt;id&gt;-json</c> and the name "&lt;name&gt; (JSON)", so it loads beside the built-in it copies.
    /// </summary>
    public static string SampleFile(ThemePalette builtIn) =>
        ToJson(builtIn, builtIn.Id + "-json", builtIn.DisplayName + " (JSON)", title: builtIn.DisplayName);

    // The value column is padded so the "// base" markers line up.
    private static string PadValue(string line)
    {
        int valueStart = Math.Min(KeyWidth, line.Length);
        string key = line[..valueStart];
        string value = line[valueStart..];
        return key + Pad(value, ValueWidth);
    }

    private static string[] GradientLines(string token, ThemeGradient g)
    {
        List<string> lines =
        [
            Quote(token) + ": {",
            "  \"start\": " + Quote(ThemeJson.FormatPoint(g.Start)) + ",",
            "  \"end\": " + Quote(ThemeJson.FormatPoint(g.End)) + ",",
            "  \"stops\": ["
        ];
        for (int i = 0; i < g.Stops.Count; i++)
        {
            ThemeGradientStop s = g.Stops[i];
            lines.Add("    { \"offset\": " + s.Offset.ToString("0.###", CultureInfo.InvariantCulture)
                                           + ", \"color\": " + Quote(ThemeJson.FormatColor(s.Color)) + " }"
                                           + (i < g.Stops.Count - 1 ? "," : ""));
        }

        lines.Add("  ]");
        lines.Add("}");
        return [.. lines];
    }

    private static string Pad(string s, int width) => s.Length >= width ? s + " " : s.PadRight(width);

    private static readonly JsonSerializerOptions _quote = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Quote(string s) => JsonSerializer.Serialize(s, _quote);
}
