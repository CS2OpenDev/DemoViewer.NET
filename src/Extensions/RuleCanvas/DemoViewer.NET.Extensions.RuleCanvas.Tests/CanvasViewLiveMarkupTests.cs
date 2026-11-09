#region

using System.Xml.Linq;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     What the canvas markup promises about the stage 3 live surfaces, read from the file as the other
///     markup suites do: the pinned viewer in the node's footer with every colour a token, the menu items
///     that pin it, and the theme's styles for it.
/// </summary>
public class CanvasViewLiveMarkupTests
{
    private static readonly XNamespace _views = "using:DemoViewer.NET.Extensions.RuleCanvas.Views";

    private static XDocument LoadView() => XDocument.Load(Path.Combine(ViewsFolder(), "CanvasTabView.axaml"));

    private static XDocument LoadTheme() => XDocument.Load(Path.Combine(ViewsFolder(), "CanvasTheme.axaml"));

    private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? "";

    // The footer is null without a pinned viewer, so a node without one draws no footer at all; the panel's
    // sparkline takes the kind token through the node, and its marker and bars are tokens too.
    [Test]
    public async Task TheAuthoredNode_HoldsTheViewerInItsFooter_DrawnFromTokens()
    {
        XDocument view = LoadView();
        XElement authored = view.Descendants().Single(e => e.Name.LocalName == "CanvasNodeTemplateSelector.Authored");
        XElement node = authored.Descendants().First(e => e.Name.LocalName == "Node");
        XElement template = node.Elements().Single(e => e.Name.LocalName == "Node.FooterTemplate");
        XElement panel = template.Descendants().Single(e => e.Name.LocalName == "Border" && Attribute(e, "Classes").Split(' ').Contains("liveViewer"));
        XElement spark = template.Descendants().Single(e => e.Name.LocalName == "Polyline");
        XElement marker = template.Descendants().Single(e => e.Name.LocalName == "Border" && Attribute(e, "IsVisible") == "{Binding HasPlayhead}");
        XElement bars = template.Descendants().Single(e => e.Name.LocalName == "ItemsControl" && Attribute(e, "ItemsSource") == "{Binding Breakdown}");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(node, "Footer")).IsEqualTo("{Binding VisibleViewer}").Because("null footer, no panel");
            await Assert.That(Attribute(node, "FooterBrush")).IsEqualTo("{DynamicResource RuleNodeBg}");
            await Assert.That(Attribute(panel, "Classes.stale")).IsEqualTo("{Binding IsStale}").Because("the panel dims with the badges");
            await Assert.That(Attribute(panel, "PointerPressed")).IsEqualTo("OnFacePressed");
            await Assert.That(Attribute(spark, "Points")).IsEqualTo("{Binding SparkPoints}");
            await Assert.That(spark.Attribute("Stroke")).IsNull().Because("the stroke is the kind token, bound through TokenBrush");
            await Assert.That(spark.Attribute(_views + "TokenBrush.Stroke")?.Value).Contains("RuleKindToken");
            await Assert.That(Attribute(marker, "Margin")).IsEqualTo("{Binding PlayheadMargin}");
            await Assert.That(Attribute(marker, "Background")).IsEqualTo("{DynamicResource RuleSelection}");
            await Assert.That(template.Descendants().Any(e => Attribute(e, "Text") == "{Binding Message}" && Attribute(e, "IsVisible") == "{Binding HasMessage}")).IsTrue();
            await Assert.That(template.Descendants().Any(e => Attribute(e, "Text") == "{Binding Fires}")).IsTrue();
            await Assert.That(template.Descendants().Any(e => Attribute(e, "Text") == "{Binding FinalText}" && Attribute(e, "Classes").Split(' ').Contains("liveBadge"))).IsTrue();
            await Assert.That(template.Descendants().Any(e => Attribute(e, "Text") == "{Binding SampledNote}" && Attribute(e, "IsVisible") == "{Binding IsSampled}")).IsTrue();
            await Assert.That(bars.Descendants().Any(e => e.Name.LocalName == "Border" && Attribute(e, "Classes.selected") == "{Binding IsSelected}" && Attribute(e, "Width") == "{Binding BarWidth}")).IsTrue();
            await Assert.That(Attribute(bars, "IsVisible")).IsEqualTo("{Binding HasBreakdown}");
        }
    }

    [Test]
    public async Task TheMenus_OfferPinViewer_OnTheNodeAndOnTheSelection()
    {
        XDocument view = LoadView();
        XElement theme = view.Descendants().Single(e => e.Name.LocalName == "ControlTheme" && Attribute(e, "TargetType") == "nodify:ItemContainer");
        XElement pin = theme.Descendants().Single(e => e.Name.LocalName == "MenuItem" && Attribute(e, "Click") == "OnToggleViewer");
        List<string> overflow = [.. view.Descendants().Where(e => e.Name.LocalName == "MenuFlyout").SelectMany(f => f.Elements()).Select(e => Attribute(e, "Header"))];
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(pin, "Header")).IsEqualTo("{ReflectionBinding ViewerMenuText}").Because("worded for the state it changes to");
            await Assert.That(overflow).Contains("Pin viewers on selection");
            await Assert.That(overflow).Contains("Unpin viewers on selection");
        }
    }

    [Test]
    public async Task TheTheme_StylesTheViewerClasses()
    {
        XDocument theme = LoadTheme();
        List<string> selectors = [.. theme.Descendants().Where(e => e.Name.LocalName == "Style").Select(e => Attribute(e, "Selector"))];
        using (Assert.Multiple())
        {
            await Assert.That(selectors).Contains("Border.liveViewer.stale");
            await Assert.That(selectors).Contains("Border.viewerBar");
            await Assert.That(selectors).Contains("Border.viewerBar.selected");
            await Assert.That(selectors).Contains("TextBlock.viewerName.selected");
        }
    }

    private static string ViewsFolder()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "Extensions", "RuleCanvas", "DemoViewer.NET.Extensions.RuleCanvas", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("the extension's Views folder is beside the tests in the repo");
    }
}
