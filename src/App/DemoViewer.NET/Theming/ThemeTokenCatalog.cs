#region

using System.Text.RegularExpressions;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>What the token catalogue says about one token.</summary>
/// <param name="Family">The catalogue heading the token sits under, e.g. "Text ramp".</param>
/// <param name="Description">The family's summary, plus the token's own role line where the table has one.</param>
public sealed record ThemeTokenInfo(string Family, string Description);

/// <summary>
///     Reads <c>docs/ui/theme-token-catalog.md</c> (embedded) for the theme editor, so a token's description has
///     one home. Each <c>### Family</c> heading's first paragraph describes every token in the table under it; a
///     table with a fourth column adds a per-token role. A token the catalogue leaves out has no entry.
/// </summary>
public static partial class ThemeTokenCatalog
{
    private const string Resource = "DemoViewer.NET.Palette.theme-token-catalog.md";

    private static readonly Lazy<IReadOnlyDictionary<string, ThemeTokenInfo>> _entries = new(Load);

    [GeneratedRegex(@"^###\s+(?<title>[^:`]+?)\s*(?::.*)?$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\|\s*`(?<token>[A-Za-z0-9_]+)`\s*\|(?<rest>.*)$")]
    private static partial Regex Row();

    [GeneratedRegex(@"\*\*|`|\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Markup();

    private static IReadOnlyDictionary<string, ThemeTokenInfo> Entries => _entries.Value;

    public static ThemeTokenInfo? Find(string token) => Entries.GetValueOrDefault(token);

    private static Dictionary<string, ThemeTokenInfo> Load()
    {
        using Stream? stream = typeof(ThemeTokenCatalog).Assembly.GetManifestResourceStream(Resource);
        if (stream is null)
        {
            return new Dictionary<string, ThemeTokenInfo>();
        }

        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }

    private static Dictionary<string, ThemeTokenInfo> Parse(string markdown)
    {
        Dictionary<string, ThemeTokenInfo> entries = new(StringComparer.Ordinal);
        string? family = null;
        List<string> paragraph = [];
        bool paragraphDone = false;
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (Heading().Match(line) is { Success: true } heading)
            {
                family = heading.Groups["title"].Value.Trim();
                paragraph.Clear();
                paragraphDone = false;
                continue;
            }

            if (line.StartsWith('#'))
            {
                family = line.StartsWith("####", StringComparison.Ordinal) ? family : null;
                paragraphDone = true;
                continue;
            }

            if (family is null)
            {
                continue;
            }

            if (Row().Match(line) is { Success: true } row)
            {
                paragraphDone = true;
                string[] cells = row.Groups["rest"].Value.Split('|');
                // cells: Dark, Light, [Role], trailing empty
                string role = cells.Length > 3 ? Clean(cells[2]) : "";
                string summary = Clean(string.Join(" ", paragraph));
                string text = role.Length == 0 ? summary : summary.Length == 0 ? role : $"{role} {summary}";
                entries.TryAdd(row.Groups["token"].Value, new ThemeTokenInfo(family, text));
                continue;
            }

            if (!paragraphDone)
            {
                if (line.Trim().Length == 0)
                {
                    paragraphDone = paragraph.Count > 0;
                }
                else if (!line.StartsWith('|'))
                {
                    paragraph.Add(line.Trim());
                }
            }
        }

        return entries;
    }

    private static string Clean(string text) =>
        Markup().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : "").Trim();
}
