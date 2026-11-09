#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The clutch_lab rule built from nothing through the calls the canvas gestures make: the New flyout,
///     gallery drops, wires, the wire editor, face pickers, the Inspector and Ctrl+G. The walkthrough test
///     asserts between the steps; the UI capture runs them all to render the result.
/// </summary>
public static class CanvasWalkthrough
{
    public const string RulesetId = "clutch_lab";

    /// <summary>Runs every step in order over a view model whose user rules directory exists.</summary>
    public static void BuildAll(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        NewRuleset(vm);
        KillCount(vm);
        CleanKillDefine(vm);
        DamageSum(vm);
        Highlight(vm);
        ImpactFlag(vm);
        Table(vm);
        Frame(vm);
    }

    /// <summary>1. The strip's New flyout, named, empty.</summary>
    public static bool NewRuleset(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.NewRulesetName = RulesetId;
        return vm.CreateNewRuleset(false);
    }

    /// <summary>2. The kill view dropped on the canvas, renamed, two facets ticked and an event field added as a where row.</summary>
    public static void KillCount(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.PlaceItem(Item(vm, "Views", "kill"), new Point(520, 140));
        Rename(vm, Node(vm, "new_kill"), "enemy_kills");
        Node(vm, "enemy_kills").FacetToAdd = "enemy";
        Node(vm, "enemy_kills").FacetToAdd = "headshot";
        Node(vm, "enemy_kills").FacetToAdd = "event.AttackerBlind";
    }

    /// <summary>3. The gallery define dropped on enemy_kills, renamed, and gated on round.active by dropping the gate on it.</summary>
    public static void CleanKillDefine(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.DropOnNode(Item(vm, "Stats", "define"), Node(vm, "enemy_kills"));
        Rename(vm, Define(vm, "new_define"), "clean_kill");
        vm.DropOnNode(Item(vm, "Gates", "round.active"), Define(vm, "clean_kill"));
    }

    /// <summary>4. The sum stat dropped on the canvas, its trigger and value picked on the face, renamed.</summary>
    public static void DamageSum(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.PlaceItem(Item(vm, "Stats", "sum"), new Point(520, 470));
        CanvasNodeViewModel sum = Node(vm, "new_sum");
        sum.Trigger!.Selected = sum.Trigger.Options.Single(o => o.Written == "damage_dealt");
        CanvasFieldViewModel value = Node(vm, "new_sum").Fields.Single(f => f.Key == "sum");
        value.Picked = "event.DmgHealth";
        value.Commit();
        Rename(vm, Node(vm, "new_sum"), "damage_dealt");
    }

    /// <summary>5. A wire from enemy_kills dropped on empty canvas starts a highlight; the wire editor sets the literal, the face the rest.</summary>
    public static void Highlight(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        DropWire(vm, Node(vm, "enemy_kills"), "New highlight when enemy_kills > 0", new Point(960, 60));
        CanvasConnectionViewModel wire = vm.Connections.Single(c => c.Source.Owner?.Id == "enemy_kills" && c.Target.Owner?.Id == "enemy_kills_highlight");
        vm.BeginEditWire(wire);
        vm.WireEditor!.Row!.Literal = "2";
        vm.WireEditor.Commit();
        Line(vm, "enemy_kills_highlight", "title").Text = "{player.name} clean clutch kills (round {round.number})";
        Line(vm, "enemy_kills_highlight", "title").Commit();
        Line(vm, "enemy_kills_highlight", "score").Text = "70";
        Line(vm, "enemy_kills_highlight", "score").Commit();
        CanvasChoiceViewModel kind = Node(vm, "enemy_kills_highlight").Choices.Single(c => c.Key == "kind");
        kind.Selected = kind.Options.Single(o => o.Value == "funny");
    }

    /// <summary>
    ///     6. A flag from the gallery, renamed; enemy_kills and damage_dealt wired into its when port, the
    ///     connective flipped to or, the placeholder row removed and player.survived added in the row editor.
    /// </summary>
    public static void ImpactFlag(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.PlaceItem(Item(vm, "Stats", "flag"), new Point(960, 400));
        Rename(vm, Node(vm, "new_flag"), "impact");
        WireInto(vm, Node(vm, "enemy_kills"), Node(vm, "impact").LogicPort!);
        WireInto(vm, Node(vm, "damage_dealt"), Node(vm, "impact").LogicPort!);
        // The join button sits above the second row; the remove button on the placeholder row.
        Line(vm, "impact", "when").Rows[1].ToggleConnectiveCommand.Execute(null);
        Line(vm, "impact", "when").Rows.Single(r => r.Path == "player.survived").RemoveCommand.Execute(null);
        CanvasExpressionViewModel when = Line(vm, "impact", "when");
        when.AddRowCommand.Execute(null);
        CanvasConditionRowViewModel added = when.Rows[^1];
        // A new row starts bare; the path and Enter write it.
        added.Path = "player.survived";
        added.CommitCommand.Execute(null);
    }

    /// <summary>7. The gallery table dropped on the canvas, three stats wired into it, two column labels set in the Inspector.</summary>
    public static void Table(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.PlaceItem(vm.Gallery.Single(g => g.Title == "Surfacing").Items.Single(i => i.Kind == GalleryItemKind.Table), new Point(1400, 220));
        foreach (string id in new[] { "enemy_kills", "damage_dealt", "impact" })
        {
            // A wire released on the node's body rather than a port, which is how a column is added.
            vm.ConnectCommand.Execute(((object)Node(vm, id).Outputs[0], (object?)Show(vm)));
        }

        Label(vm, "enemy_kills", "Clean kills");
        Label(vm, "damage_dealt", "Damage");
    }

    /// <summary>8. enemy_kills and clean_kill framed with Ctrl+G and the frame titled kills.</summary>
    public static void Frame(CanvasTabViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.SelectedNodes.Clear();
        vm.SelectedNodes.Add(Node(vm, "enemy_kills"));
        vm.SelectedNodes.Add(Define(vm, "clean_kill"));
        vm.GroupSelectionCommand.Execute(null);
        CanvasGroupViewModel frame = vm.Groups.Single();
        frame.BeginEditTitleCommand.Execute(null);
        frame.TitleText = "kills";
        frame.CommitTitle();
        vm.SelectedNodes.Clear();
    }

    /// <summary>What the editor reports for a wire released on empty canvas, then the pick from the menu the view offers.</summary>
    public static void DropWire(CanvasTabViewModel vm, CanvasNodeViewModel source, string optionTitle, Point at)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(source);
        CanvasNodeViewModel? offered = null;
        void Offer(CanvasNodeViewModel node) => offered = node;
        vm.WireDropped += Offer;
        try
        {
            vm.ConnectCommand.Execute(((object)source.Outputs[0], (object?)null));
        }
        finally
        {
            vm.WireDropped -= Offer;
        }

        if (offered is null)
        {
            throw new InvalidOperationException($"no menu was offered for a wire from {source.Id}: {vm.Toast?.Message}");
        }

        vm.PlaceItem(vm.WireDropOptions(offered).Single(o => o.Title == optionTitle), at);
    }

    /// <summary>What the editor reports for a wire from <paramref name="source" />'s output released on <paramref name="port" />.</summary>
    public static void WireInto(CanvasTabViewModel vm, CanvasNodeViewModel source, CanvasConnectorViewModel port)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(source);
        vm.ConnectCommand.Execute(((object)source.Outputs[0], (object?)port));
    }

    private static void Rename(CanvasTabViewModel vm, CanvasNodeViewModel node, string id)
    {
        vm.SelectedNodes.Clear();
        vm.SelectedNodes.Add(node);
        vm.Inspector.Id.Text = id;
        vm.Inspector.Id.Commit();
    }

    private static void Label(CanvasTabViewModel vm, string stat, string label)
    {
        vm.SelectedNodes.Clear();
        vm.SelectedNodes.Add(Show(vm));
        ShowEntryViewModel entry = vm.Inspector.Entries.Single(e => e.Stat == stat);
        entry.Label.Text = label;
        entry.Label.Commit();
    }

    private static GalleryItem Item(CanvasTabViewModel vm, string group, string name) =>
        vm.Gallery.Single(g => g.Title == group).Items.Single(i => i.Name == name);

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && n.IsEntry && !n.IsDefine);

    private static CanvasNodeViewModel Define(CanvasTabViewModel vm, string name) => vm.Nodes.Single(n => n.IsDefine && n.Title == name);

    private static CanvasNodeViewModel Show(CanvasTabViewModel vm) => vm.Nodes.Single(n => n.IsShow);

    private static CanvasExpressionViewModel Line(CanvasTabViewModel vm, string id, string key) => Node(vm, id).Lines.Single(l => l.Key == key);
}
