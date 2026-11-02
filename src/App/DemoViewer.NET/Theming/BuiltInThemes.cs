#region

using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     The four built-in themes, Dark, Light, High Contrast and E-Girl, each as a complete
///     <see cref="ThemePalette" />. Nothing outside this class can change them: the palettes are frozen, and the
///     registry refuses a user file that reuses a built-in id.
///     <para>
///         Dark and Light are read from <c>Styles/DarkPalette.axaml</c>, embedded a second time as plain XML, the
///         same file the app compiles and the SDK generates <c>ThemeTokens</c> from. So the palette has one source
///         and no copy to drift. High Contrast and E-Girl stay what they were authored as: a short JSON override
///         list over Dark (<c>Themes/*.json</c>), filled out here to every token. Reading XML instead of the
///         compiled dictionary keeps this hermetic: no running app or UI thread is needed.
///     </para>
/// </summary>
public static class BuiltInThemes
{
    private const string PaletteResource = "DemoViewer.NET.Palette.DarkPalette.axaml";

    private static readonly XNamespace _x = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly Lazy<(ThemePalette Dark, ThemePalette Light)> _bases = new(LoadBases);

    private static readonly Lazy<IReadOnlyList<ThemeDefinition>> _customDefs = new(() => LoadCustomDefinitions());

    private static readonly Lazy<IReadOnlyList<ThemePalette>> _all = new(() =>
        [Dark, Light, .. _customDefs.Value.Select(ThemePalette.Resolve)]);

    public static ThemePalette Dark => _bases.Value.Dark;

    public static ThemePalette Light => _bases.Value.Light;

    /// <summary>Dark, Light, then the embedded custom built-ins in file order.</summary>
    public static IReadOnlyList<ThemePalette> All => _all.Value;

    /// <summary>The embedded override files (High Contrast, E-Girl) as authored: only what each one sets.</summary>
    public static IReadOnlyList<ThemeDefinition> CustomDefinitions => _customDefs.Value;

    /// <summary>The built-in palette for <paramref name="id" />, or null. <c>system</c> has none: it follows the OS.</summary>
    public static ThemePalette? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The palette a custom theme on <paramref name="baseVariant" /> inherits from.</summary>
    public static ThemePalette BaseFor(ThemeVariant baseVariant) => baseVariant == ThemeVariant.Light ? Light : Dark;

    private static (ThemePalette, ThemePalette) LoadBases()
    {
        using Stream stream = typeof(BuiltInThemes).Assembly.GetManifestResourceStream(PaletteResource)
                              ?? throw new InvalidOperationException($"{PaletteResource} is not embedded.");
        XDocument doc = XDocument.Load(stream);
        return (Read(doc, "Dark", "dark", "Dark", ThemeVariant.Dark), Read(doc, "Light", "light", "Light", ThemeVariant.Light));
    }

    private static ThemePalette Read(XDocument doc, string key, string id, string name, ThemeVariant variant)
    {
        XElement dict = doc.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(_x + "Key") == key)
                        ?? throw new InvalidOperationException($"The palette has no {key} dictionary.");
        Dictionary<string, Color> colors = new(StringComparer.Ordinal);
        Dictionary<string, ThemeGradient> gradients = new(StringComparer.Ordinal);
        foreach (XElement entry in dict.Elements())
        {
            if ((string?)entry.Attribute(_x + "Key") is not { } token)
            {
                continue;
            }

            switch (entry.Name.LocalName)
            {
                case "SolidColorBrush":
                    colors[token] = Color.Parse((string)entry.Attribute("Color")!);
                    break;
                case "LinearGradientBrush":
                    gradients[token] = new ThemeGradient(
                        RelativePoint.Parse((string?)entry.Attribute("StartPoint") ?? "0%,0%"),
                        RelativePoint.Parse((string?)entry.Attribute("EndPoint") ?? "100%,100%"),
                        [
                            .. entry.Elements().Where(s => s.Name.LocalName == "GradientStop").Select(s =>
                                new ThemeGradientStop(
                                    double.Parse((string?)s.Attribute("Offset") ?? "0", CultureInfo.InvariantCulture),
                                    Color.Parse((string)s.Attribute("Color")!)))
                        ]);
                    break;
            }
        }

        return new ThemePalette(id, name, variant, colors, gradients, []);
    }

    // The embedded custom built-ins (Themes/NN-*.json), via the same parser as user drop-ins. Loaded in filename
    // order (the NN- prefix orders the picker). A broken one is skipped, never fatal.
    private static List<ThemeDefinition> LoadCustomDefinitions()
    {
        Assembly asm = typeof(BuiltInThemes).Assembly;
        List<ThemeDefinition> defs = [];
        foreach (string resource in asm.GetManifestResourceNames()
                     .Where(n => n.Contains(".Themes.", StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            try
            {
                using Stream? stream = asm.GetManifestResourceStream(resource);
                if (stream is null)
                {
                    continue;
                }

                using StreamReader reader = new(stream);
                if (ThemeJson.TryParse(reader.ReadToEnd(), resource) is { } def)
                {
                    defs.Add(def);
                }
            }
            catch (IOException)
            {
                // A shipped built-in that fails to read is simply not offered.
            }
        }

        return defs;
    }
}
