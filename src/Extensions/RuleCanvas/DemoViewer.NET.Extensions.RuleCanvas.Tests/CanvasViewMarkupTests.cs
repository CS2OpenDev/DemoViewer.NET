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

    // New is on the strip, not behind the overflow menu: a flyout with the id box and both ways to create.
    [Test]
    public async Task TheStrip_OffersNewRuleset_BesideThePicker()
    {
        XDocument view = LoadView();
        XElement button = view.Descendants().Single(e => e.Name.LocalName == "Button" && e.Attribute(_x + "Name")?.Value == "NewRulesetButton");
        List<XElement> inFlyout = [.. button.Descendants()];
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(button, "Content")).IsEqualTo("New");
            await Assert.That(Attribute(button, "IsEnabled")).IsEqualTo("{Binding CanCreateRuleset}");
            await Assert.That(inFlyout.Any(e => e.Name.LocalName == "TextBox" && Attribute(e, "Text") == "{Binding NewRulesetName}")).IsTrue();
            await Assert.That(inFlyout.Count(e => e.Name.LocalName == "Button" && Attribute(e, "Click") == "OnCreateRuleset")).IsEqualTo(2);
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

    // A horizontal StackPanel measures its children unbounded, so the status ran under the right-docked buttons.
    [Test]
    public async Task TheStripStatus_SitsInAStarColumn_AndTrims()
    {
        XDocument view = LoadView();
        XElement status = view.Descendants().Single(e => e.Name.LocalName == "TextBlock" && Attribute(e, "Text") == "{Binding Status}");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(status, "TextTrimming")).IsEqualTo("CharacterEllipsis");
            await Assert.That(Attribute(status, "ToolTip.Tip")).IsEqualTo("{Binding Status}").Because("the trimmed text is still readable");
            XElement? starColumn = status.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Grid" && Attribute(e, "ColumnDefinitions").Contains('*'));
            await Assert.That(starColumn).IsNotNull().Because("the status is bounded by a star column, not a StackPanel");
            XElement cell = status.Ancestors().First(a => a.Parent == starColumn);
            await Assert.That(Attribute(cell, "Grid.Column")).IsEqualTo("1").Because("the fixed group is column 0 (Auto) and the status column 1 (*)");
            await Assert.That(Attribute(starColumn!, "ColumnDefinitions")).IsEqualTo("Auto,*");
            await Assert.That(status.Ancestors().TakeWhile(a => a != starColumn).Any(a => a.Name.LocalName == "StackPanel")).IsFalse()
                .Because("a StackPanel between the status and its column would give it unbounded width again");
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
        await Assert.That(texts.Count).IsEqualTo(4).Because("the strip badge holds two texts, the node badge one, the show node badge one");
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

    // The badge is the one always-visible addition of live values: at the header's right, hidden without a
    // run, its text greyed through the stale class, the whole story on its tooltip.
    [Test]
    public async Task TheLiveBadge_SitsAtTheRightOfTheHeader_HiddenWithoutARun_GreyWhenStale()
    {
        XDocument view = LoadView();
        XElement badge = view.Descendants().Single(e => e.Name.LocalName == "Border" && Attribute(e, "IsVisible") == "{Binding HasLive}");
        XElement text = badge.Elements().Single(e => e.Name.LocalName == "TextBlock");
        XElement header = badge.Ancestors().First(a => a.Name.LocalName == "Grid");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(header, "ColumnDefinitions")).IsEqualTo("3,*,Auto").Because("the stripe, the title group, the badge");
            await Assert.That(Attribute(badge, "Grid.Column")).IsEqualTo("2");
            await Assert.That(Attribute(badge, "ToolTip.Tip")).IsEqualTo("{Binding LiveTip}");
            await Assert.That(Attribute(badge, "Background")).IsEqualTo("{DynamicResource RuleLiveBadgeBg}");
            await Assert.That(Attribute(text, "Text")).IsEqualTo("{Binding LiveBadge}");
            await Assert.That(Attribute(text, "FontFamily")).IsEqualTo("monospace");
            await Assert.That(Attribute(text, "Classes").Split(' ')).Contains("liveBadge").Because("the theme styles the foreground by class, never a literal");
            await Assert.That(Attribute(text, "Classes.stale")).IsEqualTo("{Binding IsLiveStale}");
            await Assert.That(header.Ancestors().Any(a => a.Name.LocalName == "Node.Header")).IsTrue();
        }
    }

    // The wire's hover is a panel: how to edit it (or why not) first, the condition, then the run's fire line
    // only when there is one.
    [Test]
    public async Task TheWireTooltip_IsAPanel_OfTheEditHintTheConditionThenTheLiveLine()
    {
        XDocument view = LoadView();
        XElement wire = view.Descendants().Single(e => e.Name.LocalName == "LabelledConnection");
        XElement? tip = wire.Elements().SingleOrDefault(e => e.Name.LocalName == "ToolTip.Tip");
        await Assert.That(tip).IsNotNull().Because("a property element, so the tooltip can hold several lines");
        List<XElement> lines = [.. tip!.Descendants().Where(e => e.Name.LocalName == "TextBlock")];
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(wire, "ToolTip.Tip")).IsEqualTo("").Because("an attribute would override the panel");
            await Assert.That(lines.Count).IsEqualTo(3);
            await Assert.That(Attribute(lines[0], "Text")).IsEqualTo("{Binding ConditionEditTip}").Because("the first line says double-click to edit, or why not");
            await Assert.That(Attribute(lines[0], "IsVisible")).IsEqualTo("{Binding HasConditionEditTip}");
            await Assert.That(Attribute(lines[1], "Text")).IsEqualTo("{Binding Condition}");
            await Assert.That(Attribute(lines[2], "Text")).IsEqualTo("{Binding LiveTip}");
            await Assert.That(Attribute(lines[2], "IsVisible")).IsEqualTo("{Binding HasLiveTip}");
        }
    }

    // An editable wire says so under the pointer: the class the theme gives a hand cursor, and the menu item
    // that stays visible but disabled, with the reason on its tooltip, on a wire that has no condition.
    [Test]
    public async Task AnEditableWire_CarriesTheEditableClass_AndItsMenuOffersEditCondition()
    {
        XDocument view = LoadView();
        XElement wire = view.Descendants().Single(e => e.Name.LocalName == "LabelledConnection");
        XElement menu = wire.Elements().Single(e => e.Name.LocalName == "LabelledConnection.ContextMenu");
        List<XElement> items = [.. menu.Descendants().Where(e => e.Name.LocalName == "MenuItem")];
        XElement edit = items.Single(i => Attribute(i, "Header") == "Edit condition");
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(wire, "Classes.editable")).IsEqualTo("{Binding CanEditCondition}");
            await Assert.That(items[0]).IsSameReferenceAs(edit).Because("edit where you look: the first item on the wire");
            await Assert.That(Attribute(edit, "Click")).IsEqualTo("OnWireEdit");
            await Assert.That(Attribute(edit, "IsEnabled")).IsEqualTo("{Binding CanEditCondition}");
            await Assert.That(Attribute(edit, "ToolTip.Tip")).IsEqualTo("{Binding ConditionEditTip}");
            await Assert.That(Attribute(edit, "ToolTip.ShowOnDisabled")).IsEqualTo("True").Because("a disabled item gets no pointer events of its own");
        }

        // In the view's styles, not the theme's: a Cursor needs the platform's cursor factory, which the
        // theme loads without in its tests.
        XElement cursor = view.Descendants().Single(e => e.Name.LocalName == "Style" && Attribute(e, "Selector") == "connections|Connection.editable");
        XElement setter = cursor.Elements().Single(e => e.Name.LocalName == "Setter");
        XDocument theme = XDocument.Load(Path.Combine(ViewsFolder(), "CanvasTheme.axaml"));
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(setter, "Property")).IsEqualTo("Cursor");
            await Assert.That(Attribute(setter, "Value")).IsEqualTo("Hand");
            await Assert.That(theme.Descendants().Any(e => Attribute(e, "Property") == "Cursor")).IsFalse();
        }
    }

    // The editor lives over the canvas, not inside the Nodify editor: a layer that takes no pointer of its
    // own, holding one host the view places; the Inspector shows the same editor for a selected wire. Both
    // take the body from WireEditorView, which carries the row, the text and the reason.
    [Test]
    public async Task TheWireEditor_IsAHostOnItsOwnLayer_AndTheInspector_ShowsTheSameBody()
    {
        XDocument view = LoadView();
        XElement layer = view.Descendants().Single(e => e.Name.LocalName == "Canvas" && e.Attribute(_x + "Name")?.Value == "WireEditorLayer");
        XElement host = layer.Elements().Single(e => e.Name.LocalName == "ContentControl");
        XDocument inspector = XDocument.Load(Path.Combine(ViewsFolder(), "InspectorView.axaml"));
        XElement section = inspector.Descendants().Single(e => e.Name.LocalName == "StackPanel" && Attribute(e, "IsVisible") == "{Binding ShowsWire}");
        XDocument body = XDocument.Load(Path.Combine(ViewsFolder(), "WireEditorView.axaml"));
        List<XElement> inside = [.. body.Descendants()];
        using (Assert.Multiple())
        {
            await Assert.That(Attribute(layer, "IsVisible")).IsEqualTo("{Binding HasWireEditor}");
            await Assert.That(Attribute(layer, "Background")).IsEqualTo("").Because("a background would take the pointer from the canvas");
            await Assert.That(layer.Ancestors().Any(a => a.Name.LocalName == "NodifyEditor")).IsFalse().Because("the editor is laid over the canvas, as the toast is");
            await Assert.That(host.Attribute(_x + "Name")?.Value).IsEqualTo("WireEditorHost");
            await Assert.That(Attribute(host, "Content")).IsEqualTo("{Binding WireEditor}");
            await Assert.That(Attribute(host, "KeyDown")).IsEqualTo("OnWireEditorKeyDown");
            await Assert.That(Attribute(host, "LostFocus")).IsEqualTo("OnWireEditorLostFocus");
            await Assert.That(host.Descendants().Count(e => e.Name.LocalName == "WireEditorView")).IsEqualTo(1);
            await Assert.That(section.Descendants().Count(e => e.Name.LocalName == "WireEditorView")).IsEqualTo(1).Because("one body, two hosts");
            await Assert.That(section.Descendants().Any(e => e.Name.LocalName == "ContentControl" && Attribute(e, "Content") == "{Binding Wire}" && Attribute(e, "KeyDown") == "OnWireKeyDown")).IsTrue();
            await Assert.That(inside.Any(e => e.Name.LocalName == "TextBox" && Attribute(e, "Name") == "WireLiteral")).IsTrue();
            await Assert.That(inside.Any(e => e.Name.LocalName == "TextBox" && Attribute(e, "Name") == "WireText" && Attribute(e, "IsVisible") == "{Binding IsText}")).IsTrue();
            await Assert.That(inside.Any(e => e.Name.LocalName == "TextBlock" && Attribute(e, "Text") == "{Binding Reason}" && Attribute(e, "IsVisible") == "{Binding IsDisabled}")).IsTrue();
            await Assert.That(inside.Any(e => e.Name.LocalName == "Button" && Attribute(e, "Command") == "{Binding CancelCommand}")).IsTrue();
            await Assert.That(inside.Any(e => e.Name.LocalName == "Button" && Attribute(e, "Command") == "{Binding CommitCommand}")).IsTrue();
            await Assert.That(inside.Where(e => e.Attribute("Foreground") is not null).All(e => Attribute(e, "Foreground").StartsWith("{DynamicResource", StringComparison.Ordinal))).IsTrue()
                .Because("no literal colours");
        }
    }

    // The strip's live group follows the ruleset picker: the demo chip, the instance picker, the state, Re-run.
    [Test]
    public async Task TheStrip_HasTheLiveGroup_AfterTheRulesetPicker()
    {
        XDocument view = LoadView();
        List<XElement> all = [.. view.Descendants()];
        XElement picker = all.Single(e => e.Name.LocalName == "ComboBox" && Attribute(e, "ItemsSource") == "{Binding Rulesets}");
        XElement chip = all.Single(e => e.Name.LocalName == "Button" && e.Attribute(_x + "Name")?.Value == "DemoChip");
        XElement instances = all.Single(e => e.Name.LocalName == "ComboBox" && Attribute(e, "ItemsSource") == "{Binding Instances}");
        XElement state = all.Single(e => e.Name.LocalName == "TextBlock" && Attribute(e, "Text") == "{Binding LiveState}");
        XElement rerun = all.Single(e => e.Name.LocalName == "Button" && Attribute(e, "Command") == "{Binding RunLiveCommand}");
        using (Assert.Multiple())
        {
            await Assert.That(all.IndexOf(picker)).IsLessThan(all.IndexOf(chip));
            await Assert.That(chip.Parent).IsSameReferenceAs(picker.Parent).Because("the same fixed group, in the Auto column");
            await Assert.That(Attribute(chip, "Content")).IsEqualTo("{Binding DemoChipText}");
            await Assert.That(Attribute(chip, "Command")).IsEqualTo("{Binding OpenLibraryCommand}");
            await Assert.That(Attribute(instances, "SelectedItem")).IsEqualTo("{Binding SelectedInstance}");
            await Assert.That(Attribute(instances, "IsVisible")).IsEqualTo("{Binding HasLiveRun}").Because("no run, nothing to pick");
            await Assert.That(instances.Descendants().Single(e => e.Name.LocalName == "TextBlock").Attribute("Text")?.Value)
                .Contains("LiveInstanceLabel").Because("the game instance has no name of its own");
            await Assert.That(state.Parent).IsSameReferenceAs(picker.Parent);
            await Assert.That(Attribute(rerun, "Content")).IsEqualTo("Re-run");
            await Assert.That(Attribute(rerun, "IsEnabled")).IsEqualTo("").Because("the command's CanExecute drives it");
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
