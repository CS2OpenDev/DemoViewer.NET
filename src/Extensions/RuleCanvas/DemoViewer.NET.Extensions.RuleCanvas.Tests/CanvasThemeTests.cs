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
        "RuleKindEvent", "RuleKindEnrich", "RuleKindContext", "RuleKindStat", "RuleKindDerived", "RuleKindHighlight",
        "RuleWireWrite", "RuleWireRead", "RuleWireGate", "RuleWireAnd", "RuleWireOr",
        "RuleCanvasBg", "RuleNodeBg", "RuleNodeBorder", "RuleSelection", "RuleSelectionFill", "RuleCutLine", "RuleMinimapBg",
        "RuleTextKey", "RuleWireHalo", "RuleCheckGlyph"
    ];

    private static readonly string[] _restyled =
    [
        "NodifyEditor", "ItemContainer", "Node", "NodeInput", "Connection", "PendingConnection", "Minimap"
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

    [Test]
    public async Task TheTheme_HoldsTheOnlyHex()
    {
        // Every colour on the canvas is a token: the view files name tokens, never a hex value or a named colour.
        string root = Path.GetDirectoryName(typeof(CanvasThemeTests).Assembly.Location)!;
        string? views = FindViews(root);
        await Assert.That(views).IsNotNull().Because("the extension's Views folder is beside the tests in the repo");
        foreach (string file in Directory.EnumerateFiles(views!, "*.axaml").Where(f => !f.EndsWith("CanvasTheme.axaml", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(text, "=\"#[0-9A-Fa-f]{6,8}\""))
                .IsFalse().Because(Path.GetFileName(file) + " holds a hex colour");
            await Assert.That(text.Contains("DodgerBlue", StringComparison.Ordinal) || text.Contains("\"Orange\"", StringComparison.Ordinal))
                .IsFalse().Because(Path.GetFileName(file) + " names a Nodify default");
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
