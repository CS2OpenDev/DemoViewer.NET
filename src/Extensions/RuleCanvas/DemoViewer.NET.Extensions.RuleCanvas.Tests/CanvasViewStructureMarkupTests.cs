#region

using System.Text.RegularExpressions;
using System.Xml.Linq;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     What the canvas view's markup promises about structure, read from the file as
///     <see cref="CanvasViewMarkupTests" /> does: the frame template, the fold chevron, the header-only
///     face, the search box, the dimming, and that nothing on the canvas names a colour outside the theme.
/// </summary>
public partial class CanvasViewStructureMarkupTests
{
    private static readonly XNamespace _x = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace _views = "using:DemoViewer.NET.Extensions.RuleCanvas.Views";

    [GeneratedRegex("#[0-9A-Fa-f]{3,8}\\b")]
    private static partial Regex HexColour();

    private static XDocument LoadView() => XDocument.Load(Path.Combine(ViewsFolder(), "CanvasTabView.axaml"));

    private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? "";

    [Test]
    public async Task TheEditor_DrawsTheItems_AndHandsItsZoomToTheViewModel()
    {
        XDocument view = LoadView();
        XElement editor = view.Descendants().Single(e => e.Name.LocalName == "NodifyEditor");
        XElement minimap = view.Descendants().Single(e => e.Name.LocalName == "Minimap");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(editor, "ItemsSource")).IsEqualTo("{Binding Items}").Because("frames and nodes are one item list, frames first");
            await Assert.That(Attribute(editor, "ViewportZoom")).IsEqualTo("{Binding ViewportZoom, Mode=OneWayToSource}")
                .Because("one property carries the zoom; the view model fans the crossing out");
            await Assert.That(Attribute(minimap, "ItemsSource")).IsEqualTo("{Binding Nodes}").Because("the minimap draws nodes, not frames");
        }
    }

    [Test]
    public async Task TheFrameTemplate_IsAGroupingNodeThatMovesItsContents_WithAnInPlaceTitle()
    {
        XDocument view = LoadView();
        XElement template = view.Descendants().Single(e => e.Name.LocalName == "CanvasNodeTemplateSelector.Group");
        XElement group = template.Descendants().Single(e => e.Name.LocalName == "GroupingNode");
        XElement header = group.Elements().Single(e => e.Name.LocalName == "GroupingNode.Header");
        XElement title = header.Descendants().Single(e => e.Name.LocalName == "TextBox");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(group, "MovementMode")).IsEqualTo("Group").Because("dragging the frame drags what is inside it");
            await Assert.That(Attribute(group, "IsContentHitTestVisible")).IsEqualTo("False").Because("a press inside the frame reaches the nodes under it");
            await Assert.That(Attribute(group, "ActualSize")).IsEqualTo("{Binding Size, Mode=TwoWay}").Because("a resize is written to the sidecar");
            await Assert.That(Attribute(group, "ResizeCompletedCommand")).Contains("SaveLayoutCommand");
            await Assert.That(Attribute(group, "CanResize")).IsEqualTo("True");
            await Assert.That(Attribute(title, "Text")).IsEqualTo("{Binding TitleText}");
            await Assert.That(title.Attribute(_views + "FaceBehaviors.FocusWhenShown")?.Value).IsEqualTo("True");
            await Assert.That(title.Attribute(_views + "FaceBehaviors.CommitOnLostFocus")?.Value).IsEqualTo("{Binding CommitTitleCommand}");
            await Assert.That(header.Descendants().Any(e => Attribute(e, "PointerPressed") == "OnFacePressed")).IsTrue()
                .Because("a left press on the title editor must not start the frame's drag");
            await Assert.That(group.Attributes().Any(a => a.Name.LocalName is "Background" or "HeaderBrush" or "BorderBrush")).IsFalse()
                .Because("the frame's brushes are the theme's, not the template's");
        }
    }

    [Test]
    public async Task TheAuthoredHeader_CarriesTheFoldChevron_AndTheFaceHidesWithIt()
    {
        XDocument view = LoadView();
        XElement authored = view.Descendants().Single(e => e.Name.LocalName == "CanvasNodeTemplateSelector.Authored");
        XElement header = authored.Descendants().Single(e => e.Name.LocalName == "Node.Header");
        XElement grid = header.Elements().Single();
        XElement chevron = header.Descendants().Single(e => e.Name.LocalName == "Button" && Attribute(e, "Click") == "OnCollapseToggle");
        XElement face = authored.Descendants().Single(e => e.Name.LocalName == "StackPanel" && Attribute(e, "IsVisible") == "{Binding ShowsFace}");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(grid, "DoubleTapped")).IsEqualTo("OnHeaderDoubleTapped");
            await Assert.That(Attribute(chevron, "Content")).IsEqualTo("{Binding CollapseGlyph}");
            await Assert.That(Attribute(chevron, "ToolTip.Tip")).IsEqualTo("{Binding CollapseTip}");
            await Assert.That(chevron.Ancestors().Any(a => Attribute(a, "PointerPressed") == "OnFacePressed")).IsTrue()
                .Because("the chevron is a button on the header; the container must not take its press");
            await Assert.That(Attribute(face, "PointerPressed")).IsEqualTo("OnFacePressed").Because("the whole face folds, chips included");
            await Assert.That(face.Parent?.Name.LocalName).IsEqualTo("Node").Because("the face is the node's content, under the header");
        }
    }

    [Test]
    public async Task TheContainerTheme_DimsByTheSearch_AndOffersGroupAndTidy()
    {
        XDocument view = LoadView();
        XElement theme = view.Descendants().Single(e => e.Name.LocalName == "ControlTheme" && Attribute(e, "TargetType") == "nodify:ItemContainer");
        XElement opacity = theme.Elements().Single(e => e.Name.LocalName == "Setter" && Attribute(e, "Property") == "Opacity");
        List<string> headers = [.. theme.Descendants().Where(e => e.Name.LocalName == "MenuItem").Select(e => Attribute(e, "Header"))];
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(opacity, "Value")).IsEqualTo("{ReflectionBinding DimOpacity}")
                .Because("frames and nodes share the theme, so the property has to exist on both");
            await Assert.That(headers).Contains("Group selection");
            await Assert.That(headers).Contains("Tidy selection");
        }
    }

    [Test]
    public async Task TheStrip_HasTheSearchBox_WithItsCountAndClear()
    {
        XDocument view = LoadView();
        XElement box = view.Descendants().Single(e => e.Name.LocalName == "TextBox" && e.Attribute(_x + "Name")?.Value == "SearchBox");
        XElement summary = view.Descendants().Single(e => e.Name.LocalName == "TextBlock" && Attribute(e, "Text") == "{Binding SearchSummary}");
        XElement clear = view.Descendants().Single(e => e.Name.LocalName == "Button" && Attribute(e, "Command") == "{Binding ClearSearchCommand}");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(box, "Text")).IsEqualTo("{Binding CanvasSearch}");
            await Assert.That(Attribute(box, "KeyDown")).IsEqualTo("OnSearchKeyDown").Because("Enter frames, Escape clears");
            await Assert.That(Attribute(summary, "IsVisible")).IsEqualTo("{Binding IsSearching}");
            await Assert.That(Attribute(clear, "IsVisible")).IsEqualTo("{Binding IsSearching}");
            await Assert.That(box.Parent).IsSameReferenceAs(summary.Parent);
        }
    }

    // Every colour on the canvas is a token. A literal would break one theme or the other silently.
    [Test]
    public async Task TheCanvasViews_NameNoLiteralColour()
    {
        string[] brushes = ["Background", "BorderBrush", "Foreground", "Fill", "Stroke", "HeaderBrush", "ContentBrush", "TextBackground", "PlaceholderForeground"];
        foreach (string file in new[] { "CanvasTabView.axaml", "ConditionRowsView.axaml", "InspectorView.axaml" })
        {
            XDocument view = XDocument.Load(Path.Combine(ViewsFolder(), file));
            foreach (XElement element in view.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    string value = attribute.Value;
                    await Assert.That(HexColour().IsMatch(value)).IsFalse().Because($"{file}: {element.Name.LocalName}.{attribute.Name.LocalName}={value}");
                    if (brushes.Contains(attribute.Name.LocalName) && !value.StartsWith('{'))
                    {
                        await Assert.That(value).IsEqualTo("Transparent").Because($"{file}: {element.Name.LocalName}.{attribute.Name.LocalName} names a colour outside the theme");
                    }
                }
            }
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
