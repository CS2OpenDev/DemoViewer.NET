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
        "RuleKindEvent", "RuleKindEnrich", "RuleKindContext", "RuleKindStat", "RuleKindDerived", "RuleKindHighlight", "RuleKindDefine", "RuleKindSurface", "RuleKindDependency",
        "RuleWireWrite", "RuleWireRead", "RuleWireGate", "RuleWireAnd", "RuleWireOr",
        "RuleCanvasBg", "RuleNodeBg", "RuleNodeBorder", "RuleSelection", "RuleSelectionFill", "RuleCutLine", "RuleMinimapBg",
        "RuleTextKey", "RuleWireHalo", "RuleCheckGlyph", "RuleDiagnosticBadgeText", "RuleDiagnosticWarning", "RuleLiveBadgeBg", "RuleLiveStale",
        "RuleGroupFill", "RuleGroupHeaderBg", "RuleGroupBorder"
    ];

    private static readonly string[] _restyled =
    [
        "NodifyEditor", "ItemContainer", "Node", "NodeInput", "Connection", "PendingConnection", "Minimap", "GroupingNode"
    ];

    private static Styles LoadTheme()
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

            // The halo under a hovered or selected wire is the token, not a literal.
            List<Style> halos = [.. styles.OfType<Style>().Where(s => s.Setters.OfType<Setter>().Any(x => x.Property?.Name == "OutlineBrush"))];
            await Assert.That(halos.Count).IsEqualTo(2).Because("a hover halo and a selection halo");
            foreach (Style halo in halos)
            {
                Setter brush = halo.Setters.OfType<Setter>().Single(x => x.Property?.Name == "OutlineBrush");
                await Assert.That(brush.Value is DynamicResourceExtension { ResourceKey: "RuleWireHalo" }).IsTrue()
                    .Because(halo.Selector?.ToString() ?? "halo");
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
            await Assert.That(RuleKindToken.KeyFor("flag")).IsEqualTo("RuleKindDerived");
            await Assert.That(RuleKindToken.KeyFor("compute")).IsEqualTo("RuleKindDerived");
            await Assert.That(RuleKindToken.KeyFor("highlight")).IsEqualTo("RuleKindHighlight");
            await Assert.That(RuleKindToken.KeyFor("define")).IsEqualTo("RuleKindDefine");
            await Assert.That(RuleKindToken.KeyFor("show")).IsEqualTo("RuleKindSurface");
            await Assert.That(RuleKindToken.KeyFor("params")).IsEqualTo("RuleKindSurface");
            await Assert.That(RuleKindToken.KeyFor("dependency")).IsEqualTo("RuleKindDependency");
            foreach (string kind in new[] { "count", "sum", "capture", "tally", "streak", "bucket", "rate", "burst" })
            {
                await Assert.That(RuleKindToken.KeyFor(kind)).IsEqualTo("RuleKindStat").Because(kind);
            }

            await Assert.That(_tokens).Contains(RuleKindToken.KeyFor(null));
        }
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
