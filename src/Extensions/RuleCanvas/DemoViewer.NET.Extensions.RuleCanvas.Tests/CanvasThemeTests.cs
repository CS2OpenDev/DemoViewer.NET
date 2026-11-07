#region

using Avalonia.Controls;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The canvas theme file loads from the assembly and carries the Nodify palette for both themes, so a
///     palette key the editor's chrome asks for resolves in light and in dark.
/// </summary>
public class CanvasThemeTests
{
    private static readonly string[] _paletteKeys =
    [
        "NodifyEditor.BackgroundColor", "NodifyEditor.ForegroundColor", "Node.BackgroundColor", "Node.ContentColor",
        "Node.HeaderColor", "Node.BorderColor", "ItemContainer.BorderColor", "ItemContainer.SelectedColor",
        "Connector.BackgroundColor", "Connector.BorderColor", "Connection.StrokeColor", "PendingConnection.StrokeColor"
    ];

    [Test]
    public async Task TheTheme_CarriesThePaletteForLightAndDark()
    {
        // The compiled loader builds the styles without a platform; the public loader wants the asset service.
        System.Reflection.MethodInfo tryLoad = typeof(Views.CanvasTabView).Assembly.GetType("CompiledAvaloniaXaml.!XamlLoader")!
            .GetMethod("TryLoad", [typeof(string)])!;
        Styles styles = (Styles)tryLoad.Invoke(null, ["avares://DemoViewer.NET.Extensions.RuleCanvas/Views/CanvasTheme.axaml"])!;
        ResourceDictionary resources = (ResourceDictionary)styles.Resources;
        using (Assert.Multiple())
        {
            await Assert.That(resources.ThemeDictionaries.Keys.OfType<ThemeVariant>().Select(v => v.Key?.ToString() ?? ""))
                .IsEquivalentTo(["Light", "Dark"]);
            foreach (ThemeVariant variant in resources.ThemeDictionaries.Keys.OfType<ThemeVariant>())
            {
                foreach (string key in _paletteKeys)
                {
                    await Assert.That(resources.TryGetResource(key, variant, out object? value) && value is Avalonia.Media.Color)
                        .IsTrue().Because($"{key} in {variant.Key}");
                }
            }

            await Assert.That(styles.OfType<Style>().Any(s => s.Selector?.ToString().Contains("read", StringComparison.Ordinal) == true))
                .IsTrue().Because("the read-wire style lives with the palette");
        }
    }
}
