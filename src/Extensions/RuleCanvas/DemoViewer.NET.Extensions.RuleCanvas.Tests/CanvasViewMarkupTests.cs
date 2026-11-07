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
