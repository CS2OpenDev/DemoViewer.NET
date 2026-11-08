#region

using System.Xml.Linq;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     What the canvas view's markup promises about input, read from the file: the project has no headless
///     platform, so the overlays and templates are checked as XML. A card laid over the editor must not take
///     the pointer where the editor should; an editor that appears must say it wants focus; a wire binds
///     every class the theme styles.
/// </summary>
public class CanvasViewMarkupTests
{
    private static readonly XNamespace _x = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace _views = "using:DemoViewer.NET.Extensions.RuleCanvas.Views";

    private static XDocument LoadView() => XDocument.Load(Path.Combine(ViewsFolder(), "CanvasTabView.axaml"));

    private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? "";

    [Test]
    public async Task TheEmptyRulesetCard_LeavesThePointerToTheEditor_ExceptOnItsButtons()
    {
        XDocument view = LoadView();
        XElement card = view.Descendants().Single(e => e.Name.LocalName == "Border" && Attribute(e, "IsVisible") == "{Binding IsEmptyRuleset}");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(card, "Background")).IsEqualTo("{x:Null}")
                .Because("a Transparent brush is a hit surface, and the card is a sibling of the editor, not inside it");
            foreach (XElement text in card.Descendants().Where(e => e.Name.LocalName == "TextBlock"))
            {
                await Assert.That(Attribute(text, "IsHitTestVisible")).IsEqualTo("False").Because(Attribute(text, "Text"));
            }

            await Assert.That(card.Descendants().Count(e => e.Name.LocalName == "Button")).IsEqualTo(3)
                .Because("the quick starts stay clickable");
        }
    }

    // The pencil only makes the editor visible; Enter and commit-on-blur both need focus inside it.
    [Test]
    public async Task TheLabelEditor_AsksForFocusWhenItAppears()
    {
        XDocument view = LoadView();
        XElement editor = view.Descendants().Single(e => e.Name.LocalName == "TextBox" && Attribute(e, "IsVisible") == "{Binding ShowLabelEditor}");
        using (Assert.Multiple())
        {
            await Assert.That(editor.Attribute(_views + "FaceBehaviors.FocusWhenShown")?.Value).IsEqualTo("True")
                .Because("the pencil is one gesture, not two");
            await Assert.That(editor.Attribute(_views + "FaceBehaviors.CommitOnLostFocus")?.Value).IsEqualTo("{Binding CommitLabelCommand}");
        }
    }

    // The theme styles four wire classes; a class nothing binds is a style that never matches.
    [Test]
    public async Task TheWireTemplate_BindsEveryClassTheThemeStyles()
    {
        XDocument view = LoadView();
        XElement wire = view.Descendants().Single(e => e.Name.LocalName == "LabelledConnection");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(wire, "Classes.read")).IsEqualTo("{Binding IsRead}");
            await Assert.That(Attribute(wire, "Classes.gate")).IsEqualTo("{Binding IsGate}");
            await Assert.That(Attribute(wire, "Classes.and")).IsEqualTo("{Binding IsAnd}").Because("Connection.and has a style and no setter");
            await Assert.That(Attribute(wire, "Classes.or")).IsEqualTo("{Binding IsOr}").Because("Connection.or has a style and no setter");
        }
    }

    // TextOnAccent is 3.30:1 on the light error red; the badge text is the token measured for both badges.
    [Test]
    public async Task EveryDiagnosticsBadge_UsesTheBadgeTextToken()
    {
        XDocument view = LoadView();
        List<XElement> badges = [.. view.Descendants().Where(e => e.Name.LocalName == "Border"
            && (Attribute(e, "Background") == "{DynamicResource AccentError}"
                || (e.Attribute(_views + "TokenBrush.Background")?.Value ?? "").Contains("DiagnosticSeverityToken", StringComparison.Ordinal)))];
        List<XElement> texts = [.. badges.SelectMany(b => b.Descendants().Where(e => e.Name.LocalName == "TextBlock"))];
        await Assert.That(texts.Count).IsEqualTo(3).Because("the strip badge holds two texts, the node badge one");
        foreach (XElement text in texts)
        {
            await Assert.That(Attribute(text, "Foreground")).IsEqualTo("{DynamicResource RuleDiagnosticBadgeText}").Because(Attribute(text, "Text"));
        }

        await Assert.That(view.Descendants().Any(e => Attribute(e, "Foreground") == "{DynamicResource TextOnAccent}")).IsFalse()
            .Because("TextOnAccent is under 4.5:1 on every accent the canvas fills with");
    }

    // The theme restyles CheckBox.face; a face checkbox without the class keeps Fluent's accent fill.
    [Test]
    public async Task EveryFaceCheckBox_CarriesTheFaceClass()
    {
        foreach (string file in new[] { "CanvasTabView.axaml", "ConditionRowsView.axaml" })
        {
            XDocument view = XDocument.Load(Path.Combine(ViewsFolder(), file));
            List<XElement> boxes = [.. view.Descendants().Where(e => e.Name.LocalName == "CheckBox")];
            await Assert.That(boxes).IsNotEmpty().Because(file);
            foreach (XElement box in boxes)
            {
                await Assert.That(Attribute(box, "Classes").Split(' ')).Contains("face").Because(file + ": " + Attribute(box, "IsChecked"));
            }
        }
    }

    // The converter hides a label under 0.7 zoom; a Text bound straight to the view model never reaches it.
    [Test]
    public async Task TheWireLabel_GoesThroughTheZoomConverter()
    {
        XDocument view = LoadView();
        XElement wire = view.Descendants().Single(e => e.Name.LocalName == "LabelledConnection");
        XElement? text = wire.Elements().SingleOrDefault(e => e.Name.LocalName == "LabelledConnection.Text");
        await Assert.That(text).IsNotNull().Because("the label is a property element, not a plain binding");
        XElement multi = text!.Elements().Single(e => e.Name.LocalName == "MultiBinding");
        List<string> paths = [.. multi.Elements().Where(e => e.Name.LocalName == "Binding").Select(e => Attribute(e, "Path"))];
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(wire, "Text")).IsEqualTo("").Because("an attribute would override the property element");
            await Assert.That(Attribute(multi, "Converter")).IsEqualTo("{x:Static views:WireLabelAtZoom.Instance}");
            await Assert.That(paths.Count).IsEqualTo(2);
            await Assert.That(paths[0]).IsEqualTo("Text").Because("the converter reads the text first");
            await Assert.That(paths[1]).EndsWith("ViewportZoom").Because("and the editor's zoom second");
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
