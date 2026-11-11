#region

using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Extensions.RuleCanvas.Views;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The canvas theme file loads from the assembly and carries every Rule* token for both themes, so a
///     kind, a wire or a piece of editor chrome that asks for one resolves in light and in dark, and the
///     Nodify chrome is restyled rather than left on Nodify's own palette.
/// </summary>
public class CanvasThemeTests
{
    private static readonly string[] _tokens =
    [
        "RuleKindEvent", "RuleKindEnrich", "RuleKindContext", "RuleKindStat", "RuleKindCapture", "RuleKindFlag", "RuleKindDerived", "RuleKindShaped",
        "RuleKindHighlight", "RuleKindDefine", "RuleKindSurface", "RuleKindDependency",
        "RuleWireWrite", "RuleWireRead", "RuleWireGate", "RuleWireAnd", "RuleWireOr",
        "RuleCanvasBg", "RuleNodeBg", "RuleNodeBorder", "RuleSelection", "RuleSelectionFill", "RuleCutLine", "RuleMinimapBg",
        "RuleTextKey", "RuleWireHalo", "RuleCheckGlyph", "RuleDiagnosticBadgeText", "RuleDiagnosticWarning", "RuleLiveBadgeBg", "RuleLiveStale",
        "RuleGroupFill", "RuleGroupHeaderBg", "RuleGroupBorder"
    ];

    private static readonly string[] _restyled =
    [
        "NodifyEditor", "ItemContainer", "Node", "NodeInput", "Connection", "PendingConnection", "Minimap", "GroupingNode"
    ];

    internal static Styles LoadTheme()
    {
        // The compiled loader builds the styles without a platform; the public loader wants the asset service.
        System.Reflection.MethodInfo tryLoad = typeof(CanvasTabView).Assembly.GetType("CompiledAvaloniaXaml.!XamlLoader")!
            .GetMethod("TryLoad", [typeof(string)])!;
        return (Styles)tryLoad.Invoke(null, ["avares://DemoViewer.NET.Extensions.RuleCanvas/Views/CanvasTheme.axaml"])!;
    }

    [Test]
    public async Task TheTheme_CarriesEveryTokenForLightAndDark()
    {
        Styles styles = LoadTheme();
        ResourceDictionary resources = (ResourceDictionary)styles.Resources;
        using (Assert.Multiple())
        {
            await Assert.That(resources.ThemeDictionaries.Keys.OfType<ThemeVariant>().Select(v => v.Key?.ToString() ?? ""))
                .IsEquivalentTo(["Light", "Dark"]);
            foreach (ThemeVariant variant in resources.ThemeDictionaries.Keys.OfType<ThemeVariant>())
            {
                foreach (string key in _tokens)
                {
                    await Assert.That(resources.TryGetResource(key, variant, out object? value) && value is ISolidColorBrush)
                        .IsTrue().Because($"{key} in {variant.Key}");
                }
            }

            // The light and dark values differ: a token that is the same in both was not designed for light.
            foreach (string key in _tokens)
            {
                resources.TryGetResource(key, ThemeVariant.Light, out object? light);
                resources.TryGetResource(key, ThemeVariant.Dark, out object? dark);
                await Assert.That(((ISolidColorBrush)light!).Color).IsNotEqualTo(((ISolidColorBrush)dark!).Color).Because(key);
            }
        }
    }

    // The minimap items are the kind tokens, tuned for text on the face; light softens them, dark does not.
    [Test]
    public async Task TheMinimapItems_AreSoftenedInLightOnly()
    {
        Styles styles = LoadTheme();
        ResourceDictionary resources = (ResourceDictionary)styles.Resources;
        using (Assert.Multiple())
        {
            await Assert.That(resources.TryGetResource("RuleMinimapItemOpacity", ThemeVariant.Light, out object? light) && light is double).IsTrue();
            await Assert.That(resources.TryGetResource("RuleMinimapItemOpacity", ThemeVariant.Dark, out object? dark) && dark is double).IsTrue();
            await Assert.That((double)light!).IsEqualTo(0.55);
            await Assert.That((double)dark!).IsEqualTo(1.0);
            Style item = styles.OfType<Style>().Single(s => s.Selector?.ToString().EndsWith("MinimapItem", StringComparison.Ordinal) == true);
            Setter opacity = item.Setters.OfType<Setter>().Single(s => s.Property?.Name == "Opacity");
            await Assert.That(opacity.Value is DynamicResourceExtension { ResourceKey: "RuleMinimapItemOpacity" }).IsTrue()
                .Because("the softening is the theme's, not the template's");
        }
    }

    [Test]
    public async Task TheTheme_RestylesEveryPieceOfNodifyChrome()
    {
        Styles styles = LoadTheme();
        List<string> selectors = [.. styles.OfType<Style>().Select(s => s.Selector?.ToString() ?? "")];
        ResourceDictionary resources = (ResourceDictionary)styles.Resources;
        using (Assert.Multiple())
        {
            foreach (string control in _restyled)
            {
                await Assert.That(selectors.Any(s => s.Contains(control, StringComparison.Ordinal))).IsTrue().Because(control);
            }

            foreach (string wire in new[] { "read", "gate", "and", "or" })
            {
                await Assert.That(selectors.Any(s => s.EndsWith("Connection." + wire, StringComparison.Ordinal)))
                    .IsTrue().Because("the " + wire + " wire style lives with the palette");
            }

            // A checked face checkbox is restyled off Fluent's accent: the box and its tick.
            await Assert.That(selectors.Any(s => s.Contains("CheckBox.face:checked", StringComparison.Ordinal) && s.EndsWith("Border#NormalRectangle", StringComparison.Ordinal)))
                .IsTrue().Because("the checked box takes RuleSelection");
            await Assert.That(selectors.Any(s => s.Contains("CheckBox.face:checked", StringComparison.Ordinal) && s.EndsWith("Path#CheckGlyph", StringComparison.Ordinal)))
                .IsTrue().Because("the tick takes RuleCheckGlyph");

            // The halo under a hovered or selected wire is the token, not a literal; the step flash is the third halo, in its own token.
            List<Style> halos = [.. styles.OfType<Style>().Where(s => s.Setters.OfType<Setter>().Any(x => x.Property?.Name == "OutlineBrush"))];
            await Assert.That(halos.Count).IsEqualTo(3).Because("a hover halo, a selection halo and the step flash");
            foreach (Style halo in halos)
            {
                Setter brush = halo.Setters.OfType<Setter>().Single(x => x.Property?.Name == "OutlineBrush");
                string selector = halo.Selector?.ToString() ?? "halo";
                string token = selector.EndsWith(".flash", StringComparison.Ordinal) ? "RuleWireFlash" : "RuleWireHalo";
                await Assert.That(brush.Value is DynamicResourceExtension extension && (string?)extension.ResourceKey == token).IsTrue().Because(selector);
            }

            foreach (string theme in new[] { "NodifyEditor.SelectionRectangleTheme", "NodifyEditor.CuttingLineTheme", "NodifyEditor.PushedAreaTheme" })
            {
                await Assert.That(resources.TryGetResource(theme, null, out object? value) && value is ControlTheme)
                    .IsTrue().Because(theme);
            }
        }
    }

    // A quoted #rgb, #argb, #rrggbb or #aarrggbb, in markup or in a C# string; #region and the like have no quote.
    private static readonly System.Text.RegularExpressions.Regex _hex =
        new("[\"']#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})[\"']", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] _scanned = ["Model", "ViewModels", "Views"];

    private static readonly System.Text.RegularExpressions.Regex _named =
        new(@"\b(?:Colors|Brushes)\.[A-Z][A-Za-z]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    [Test]
    public async Task TheTheme_HoldsTheOnlyHex()
    {
        // Every colour on the canvas is a token: the model, the view models and the views name tokens, never a
        // hex value or a named colour, in markup or in code.
        string root = Path.GetDirectoryName(typeof(CanvasThemeTests).Assembly.Location)!;
        string? views = FindViews(root);
        await Assert.That(views).IsNotNull().Because("the extension's Views folder is beside the tests in the repo");
        string extension = Path.GetDirectoryName(views!)!;
        List<string> files =
        [
            .. _scanned
                .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(extension, folder), "*.*", SearchOption.AllDirectories))
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                .Where(f => !f.EndsWith("CanvasTheme.axaml", StringComparison.Ordinal))
        ];
        await Assert.That(files.Count).IsGreaterThan(40).Because("the three folders are scanned, not one");
        using (Assert.Multiple())
        {
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                await Assert.That(_hex.Match(text).Value).IsEqualTo("").Because(Path.GetFileName(file) + " holds a hex colour");
                await Assert.That(_named.Match(text).Value).IsEqualTo("").Because(Path.GetFileName(file) + " names a colour");
                await Assert.That(text.Contains("DodgerBlue", StringComparison.Ordinal) || text.Contains("\"Orange\"", StringComparison.Ordinal))
                    .IsFalse().Because(Path.GetFileName(file) + " names a Nodify default");
            }
        }

        // The scanner itself: the four hex widths and a named colour are caught, a #region and a token are not.
        using (Assert.Multiple())
        {
            foreach (string literal in new[] { "\"#FFF\"", "\"#8FFF\"", "\"#5050A0\"", "'#265050A0'" })
            {
                await Assert.That(_hex.IsMatch("x=" + literal)).IsTrue().Because(literal);
            }

            await Assert.That(_hex.IsMatch("#region\n\"#12345\"")).IsFalse();
            await Assert.That(_named.IsMatch("Fill = Brushes.Transparent;")).IsTrue();
            await Assert.That(_named.IsMatch("Colors.DodgerBlue")).IsTrue();
            await Assert.That(_named.IsMatch("{DynamicResource RuleTextKey}")).IsFalse();
        }
    }

    [Test]
    public async Task EveryKind_MapsToAKindToken()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RuleKindToken.KeyFor("event")).IsEqualTo("RuleKindEvent");
            await Assert.That(RuleKindToken.KeyFor("enrich")).IsEqualTo("RuleKindEnrich");
            await Assert.That(RuleKindToken.KeyFor("context")).IsEqualTo("RuleKindContext");
            await Assert.That(RuleKindToken.KeyFor("entity")).IsEqualTo("RuleKindEnrich");
            await Assert.That(RuleKindToken.KeyFor("count")).IsEqualTo("RuleKindStat");
            await Assert.That(RuleKindToken.KeyFor("sum")).IsEqualTo("RuleKindStat");
            await Assert.That(RuleKindToken.KeyFor("capture")).IsEqualTo("RuleKindCapture");
            await Assert.That(RuleKindToken.KeyFor("flag")).IsEqualTo("RuleKindFlag");
            await Assert.That(RuleKindToken.KeyFor("compute")).IsEqualTo("RuleKindDerived");
            await Assert.That(RuleKindToken.KeyFor("rate")).IsEqualTo("RuleKindDerived");
            foreach (string kind in new[] { "tally", "streak", "bucket", "burst" })
            {
                await Assert.That(RuleKindToken.KeyFor(kind)).IsEqualTo("RuleKindShaped").Because(kind);
            }

            await Assert.That(RuleKindToken.KeyFor("highlight")).IsEqualTo("RuleKindHighlight");
            await Assert.That(RuleKindToken.KeyFor("define")).IsEqualTo("RuleKindDefine");
            await Assert.That(RuleKindToken.KeyFor("show")).IsEqualTo("RuleKindSurface");
            await Assert.That(RuleKindToken.KeyFor("params")).IsEqualTo("RuleKindSurface");
            await Assert.That(RuleKindToken.KeyFor("dependency")).IsEqualTo("RuleKindDependency");
            await Assert.That(_tokens).Contains(RuleKindToken.KeyFor(null));

            // Every family token is reached by some kind, so none is dead.
            List<string> families = [.. _tokens.Where(t => t.StartsWith("RuleKind", StringComparison.Ordinal))];
            await Assert.That(RuleKindToken.Kinds.Select(RuleKindToken.KeyFor).Distinct()).IsEquivalentTo(families);
        }
    }

    private static Color ColorOf(ResourceDictionary resources, string key, ThemeVariant variant)
    {
        resources.TryGetResource(key, variant, out object? value);
        return ((ISolidColorBrush)value!).Color;
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Color Over(Color top, Color bottom, double alpha) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + bottom.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + bottom.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + bottom.B * (1 - alpha)));

    private static double HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d == 0)
        {
            return 0;
        }

        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    private static double HueDistance(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return Math.Min(d, 360 - d);
    }

    // The pill text is the family token on the face and on the family's own header tint; the stripe is the
    // same token beside RuleNodeBg. WCAG 2: 4.5:1 for the 9px pill text.
    [Test]
    public async Task EveryKind_PillText_HoldsFourAndAHalfToOne_OnTheFaceAndItsTint()
    {
        ResourceDictionary resources = (ResourceDictionary)LoadTheme().Resources;
        using (Assert.Multiple())
        {
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Color face = ColorOf(resources, "RuleNodeBg", variant);
                await Assert.That(resources.TryGetResource("RuleKindTintOpacity", variant, out object? tintValue) && tintValue is double).IsTrue();
                double tint = (double)tintValue!;
                await Assert.That(tint).IsBetween(0.04, 0.15).Because("a faint band, not a fill");
                foreach (string kind in RuleKindToken.Kinds)
                {
                    Color family = ColorOf(resources, RuleKindToken.KeyFor(kind), variant);
                    string why = $"{kind} in {variant.Key}";
                    await Assert.That(Contrast(family, face)).IsGreaterThanOrEqualTo(4.5).Because(why + " on RuleNodeBg");
                    await Assert.That(Contrast(family, Over(family, face, tint))).IsGreaterThanOrEqualTo(4.5).Because(why + " on its header tint");
                }
            }
        }
    }

    // The saturated families are told apart by hue: 25 degrees between any two, and from the wire, selection and
    // cut-line hues a node could be mistaken for. The muted families sit under a chroma ceiling instead.
    [Test]
    public async Task TheSaturatedFamilies_StandApartInHue_FromEachOtherAndTheWires()
    {
        string[] saturated = ["RuleKindEvent", "RuleKindStat", "RuleKindCapture", "RuleKindFlag", "RuleKindDerived", "RuleKindShaped", "RuleKindHighlight", "RuleKindDefine"];
        string[] muted = ["RuleKindEnrich", "RuleKindContext", "RuleKindSurface", "RuleKindDependency"];
        string[] reserved = ["RuleWireGate", "RuleWireAnd", "RuleWireOr", "RuleSelection", "RuleCutLine"];
        ResourceDictionary resources = (ResourceDictionary)LoadTheme().Resources;
        using (Assert.Multiple())
        {
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                for (int i = 0; i < saturated.Length; i++)
                {
                    double hue = HueOf(ColorOf(resources, saturated[i], variant));
                    for (int j = i + 1; j < saturated.Length; j++)
                    {
                        await Assert.That(HueDistance(hue, HueOf(ColorOf(resources, saturated[j], variant))))
                            .IsGreaterThanOrEqualTo(24.5).Because($"{saturated[i]} and {saturated[j]} in {variant.Key}");
                    }

                    foreach (string wire in reserved)
                    {
                        await Assert.That(HueDistance(hue, HueOf(ColorOf(resources, wire, variant))))
                            .IsGreaterThanOrEqualTo(12).Because($"{saturated[i]} and {wire} in {variant.Key}");
                    }
                }

                foreach (string key in muted)
                {
                    Color c = ColorOf(resources, key, variant);
                    double chroma = (Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B))) / 255.0;
                    await Assert.That(chroma).IsLessThan(0.16).Because($"{key} in {variant.Key} is a quiet family");
                }
            }
        }
    }

    // The selection ring is the container's border, outside the face: it must read on the canvas, and stand
    // apart from the family stripe it meets at the node's left edge.
    [Test]
    public async Task TheSelectionRing_ReadsOnTheCanvas_AndBesideEveryStripe()
    {
        ResourceDictionary resources = (ResourceDictionary)LoadTheme().Resources;
        using (Assert.Multiple())
        {
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Color ring = ColorOf(resources, "RuleSelection", variant);
                await Assert.That(Contrast(ring, ColorOf(resources, "RuleCanvasBg", variant))).IsGreaterThanOrEqualTo(3.0).Because(variant.Key?.ToString() ?? "");
                double ringHue = HueOf(ring);
                foreach (string kind in RuleKindToken.Kinds)
                {
                    string key = RuleKindToken.KeyFor(kind);
                    Color stripe = ColorOf(resources, key, variant);
                    bool apart = HueDistance(ringHue, HueOf(stripe)) >= 20 || Contrast(ring, stripe) >= 1.5;
                    await Assert.That(apart).IsTrue().Because($"{key} in {variant.Key} would read as part of the ring");
                }
            }
        }
    }

    internal static IReadOnlyList<double> DashOf(Styles styles, string selectorEnd)
    {
        Style style = styles.OfType<Style>().Single(s => s.Selector?.ToString().EndsWith(selectorEnd, StringComparison.Ordinal) == true);
        Setter dash = style.Setters.OfType<Setter>().Single(s => s.Property?.Name == "StrokeDashArray");
        return [.. ((IEnumerable<double>)dash.Value!)];
    }

    // The two dashed wires must not read alike at the opening fit (zoom 0.6): a dotted read, a long-dashed gate.
    [Test]
    public async Task TheReadAndGateWires_DifferInDashShape_AtTheOpeningZoom()
    {
        Styles styles = LoadTheme();
        IReadOnlyList<double> read = DashOf(styles, "Connection.read");
        IReadOnlyList<double> gate = DashOf(styles, "Connection.gate");
        WireStroke(out double stroke);
        const double fit = 0.6;
        double readDash = read[0] * stroke * fit, gateDash = gate[0] * stroke * fit;
        using (Assert.Multiple())
        {
            await Assert.That(readDash).IsLessThanOrEqualTo(1.5).Because("a read is dotted on screen");
            await Assert.That(gateDash / readDash).IsGreaterThanOrEqualTo(4).Because("a gate's dash is several dots long");
            await Assert.That(read[1] / read[0]).IsGreaterThan(1).Because("a read's gap outruns its dot");
            await Assert.That(gate[1] / gate[0]).IsLessThan(1).Because("a gate's dash outruns its gap");
            foreach (string wire in new[] { "Connection.read", "Connection.gate" })
            {
                Style style = styles.OfType<Style>().Single(s => s.Selector?.ToString().EndsWith(wire, StringComparison.Ordinal) == true);
                await Assert.That(style.Setters.OfType<Setter>().Any(s => s.Property?.Name == "StrokeThickness")).IsFalse()
                    .Because("the template's local StrokeThickness wins over a style setter, so one here is dead");
            }
        }
    }

    // The wire template's stroke, which the dash units multiply.
    private static void WireStroke(out double stroke)
    {
        System.Xml.Linq.XDocument view = System.Xml.Linq.XDocument.Load(Path.Combine(FindViews(Path.GetDirectoryName(typeof(CanvasThemeTests).Assembly.Location)!)!, "CanvasTabView.axaml"));
        System.Xml.Linq.XElement wire = view.Descendants().Single(e => e.Name.LocalName == "LabelledConnection");
        stroke = double.Parse(wire.Attribute("StrokeThickness")!.Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? FindViews(string start)
    {
        DirectoryInfo? dir = new(start);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Extensions", "RuleCanvas", "DemoViewer.NET.Extensions.RuleCanvas", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
