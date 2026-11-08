#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The rows and pickers a face carries beyond its facets: the trigger picker, the value row, the gate
///     editor, the actor binding, and every kind's own keys. Each one is driven the way the template drives
///     it and checked against the file, over a lab ruleset that holds one stat of every kind.
/// </summary>
public class CanvasFaceRowsTests
{
    private const string Lab = """
        ruleset: faces_lab
        title: Faces lab
        for: each_player

        define:
          post_plant_kill:
            on: kill
            match: { enemy: true }
            while: round.bomb.was_planted

        stats:
          kills:
            count: kill
            match: { enemy: true }
            per: round
          late_kills:
            count: post_plant_kill
            per: round
          damage:
            sum: enrich.hurt.capped_damage
            on: damage_dealt
            match: { enemy: true }
            per: round
          first_contact:
            capture: event.frame_tick
            on: damage_dealt
            match: { actor: any, enemy: true }
            keep: first
            per: round
          side:
            capture: player.team
            on: raw.round_freeze_end
            per: round
          seen:
            flag:
              when: "kills > 0"
            per: round
          gated_kills:
            count: kill
            while: seen
            per: round
          odd_gate:
            count: kill
            while: round.number > 0
            per: round
          kills_match:
            count: kill
            per: match
          multi_kill:
            tally: kills
            thresholds:
              - { min: 3, target: rounds_3k }
              - { min: 2, target: rounds_2k }
            per: match
          rapid:
            streak: kill
            window: 640
            min_streak: 2
            per: match
          double_burst:
            burst: kill
            match: { enemy: true }
            window: "3s"
            min_streak: 2
          kills_by_weapon:
            bucket: kill
            match: { enemy: true }
            key: event.Weapon
            per: match
          damage_by_weapon:
            bucket: damage_dealt
            match: { enemy: true }
            key: event.Weapon
            value: enrich.hurt.capped_damage
            per: match
          damage_per_kill:
            rate: { of: damage_by_weapon, per: kills_by_weapon }
            per: match
          kpr:
            compute: "kills_match / round.number"
            per: match
            format: F2
            label: KPR

        highlights:
          ace:
            when: "kills >= 5"
            per: round
            title: "Ace"
            kind: funny
            group: multikill
          quad:
            when: "kills >= 4"
            per: round
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.New();
        rules.User("faces_lab", Lab);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "faces_lab";
        vm.OnActivated(null!);
        return (rules, vm);
    }

    private static CanvasNodeViewModel Node(CanvasTabViewModel vm, string id) => vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);

    private static string Text(RulesTree rules) => File.ReadAllText(rules.UserPath("faces_lab"));

    private static CanvasFieldViewModel Field(CanvasTabViewModel vm, string id, string key) => Node(vm, id).Fields.Single(f => f.Key == key);

    private static CanvasChoiceViewModel Choice(CanvasTabViewModel vm, string id, string key) => Node(vm, id).Choices.Single(c => c.Key == key);

    [Test]
    public async Task TheLab_ComposesCleanly_AndEveryKindHasItsRows()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            using (Assert.Multiple())
            {
                await Assert.That(vm.Diagnostics).IsEmpty().Because("the lab must compose, or the faces come from the documents alone");
                await Assert.That(vm.IsEditable).IsTrue();
                await Assert.That(Node(vm, "kills").Trigger!.Key).IsEqualTo("count");
                await Assert.That(Node(vm, "damage").Trigger!.Key).IsEqualTo("on");
                await Assert.That(Node(vm, "multi_kill").Trigger).IsNull().Because("a tally evaluates at round end; the resolver reads no on: for it");
                await Assert.That(Node(vm, "multi_kill").Chips.Any(c => c.StartsWith("on ", StringComparison.Ordinal))).IsFalse()
                    .Because("the round_ended wire says when it evaluates");
                await Assert.That(Node(vm, "rapid").Trigger!.Key).IsEqualTo("streak");
                await Assert.That(Node(vm, "double_burst").Trigger!.Key).IsEqualTo("burst");
                await Assert.That(Node(vm, "kills_by_weapon").Trigger!.Key).IsEqualTo("bucket");
                await Assert.That(Node(vm, "seen").Trigger).IsNull();
                await Assert.That(Node(vm, "kpr").Trigger).IsNull();
                await Assert.That(Node(vm, "damage_per_kill").Trigger).IsNull();
                await Assert.That(Node(vm, "damage").Fields.Select(f => f.Key)).IsEquivalentTo(["sum"]);
                await Assert.That(Node(vm, "first_contact").Fields.Select(f => f.Key)).IsEquivalentTo(["capture"]);
                await Assert.That(Node(vm, "first_contact").Choices.Select(c => c.Key)).IsEquivalentTo(["keep"]);
                await Assert.That(Node(vm, "multi_kill").Fields.Select(f => f.Key)).IsEquivalentTo(["tally"]);
                await Assert.That(Node(vm, "multi_kill").Thresholds!.Rows.Select(r => r.Min + ":" + r.Target)).IsEquivalentTo(["3:rounds_3k", "2:rounds_2k"]);
                await Assert.That(Node(vm, "rapid").Fields.Select(f => f.Key)).IsEquivalentTo(["window", "min_streak"]);
                await Assert.That(Node(vm, "double_burst").Fields.Select(f => f.Original)).IsEquivalentTo(["3s", "2"]);
                await Assert.That(Node(vm, "damage_by_weapon").Fields.Select(f => f.Key)).IsEquivalentTo(["key", "value"]);
                await Assert.That(Node(vm, "damage_by_weapon").Choices.Single().ReadText).IsEqualTo("sum (default)");
                await Assert.That(Node(vm, "kills_by_weapon").Choices.Single().ReadText).IsEqualTo("count (default)");
                await Assert.That(Node(vm, "damage_per_kill").Fields.Select(f => f.Key + "=" + f.Original)).IsEquivalentTo(["rate.of=damage_by_weapon", "rate.per=kills_by_weapon"]);
                await Assert.That(Node(vm, "damage_per_kill").Fields[0].Suggestions).IsEquivalentTo(["kills_by_weapon", "damage_by_weapon"]);
                await Assert.That(Choice(vm, "kpr", "format").Current).IsEqualTo("F2");
                await Assert.That(Choice(vm, "kpr", "live").IsDefault).IsTrue().Because("a scalar compute evaluates at the scope's end");
                await Assert.That(Node(vm, "kpr").Label).IsEqualTo("KPR");
                await Assert.That(Node(vm, "ace").Choices.Single().Current).IsEqualTo("funny");
                await Assert.That(Node(vm, "ace").Fields.Single().Original).IsEqualTo("multikill");
                await Assert.That(Node(vm, "quad").Fields.Single().Suggestions).IsEquivalentTo(["multikill"]);
                await Assert.That(Node(vm, "quad").Defaults).IsEquivalentTo(["score 50"]);
                await Assert.That(Node(vm, "double_burst").Chips).Contains("per round").Because("the mapper fills the scope in, so the face shows it as written");
                await Assert.That(Node(vm, "double_burst").Defaults).IsEmpty();
            }
        }
    }

    [Test]
    public async Task TheTriggerPicker_ListsViewsByEvent_RawEvents_AndDefines_AndShowsTheSpellingWritten()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasTriggerViewModel late = Node(vm, "late_kills").Trigger!;
            CanvasTriggerViewModel side = Node(vm, "side").Trigger!;
            IReadOnlyList<TriggerOption> options = late.Options;
            int kill = options.ToList().FindIndex(o => o.Written == "kill");
            int death = options.ToList().FindIndex(o => o.Written == "death");
            using (Assert.Multiple())
            {
                await Assert.That(late.Spelling).IsEqualTo("post_plant_kill");
                await Assert.That(late.ResolvedView).IsEqualTo("kill");
                await Assert.That(late.Chip).IsEqualTo("on post_plant_kill");
                await Assert.That(late.Tip).Contains("resolves to the view kill");
                await Assert.That(late.Selected!.Group).IsEqualTo("define");
                await Assert.That(Node(vm, "late_kills").Chips).Contains("on post_plant_kill");
                await Assert.That(side.Chip).IsEqualTo("on raw.round_freeze_end");
                await Assert.That(side.Selected!.Group).IsEqualTo("raw");
                await Assert.That(Math.Abs(kill - death)).IsEqualTo(1).Because("the views of one event sit together");
                await Assert.That(options.Single(o => o.Written == "kill").Display).IsEqualTo("kill  (player_death)");
                await Assert.That(options.Count(o => o.Group == "raw")).IsEqualTo(EmbeddedCatalog.Root.Events.Count);
                await Assert.That(options.Where(o => o.Group == "define").Select(o => o.Written)).IsEquivalentTo(["post_plant_kill"]);
            }
        }
    }

    [Test]
    public async Task PickingATrigger_WritesTheKindKeyOrOn_AndTheLoadedPickDoesNot()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = Text(rules);
            CanvasTriggerViewModel kills = Node(vm, "kills").Trigger!;
            kills.Selected = kills.Options.Single(o => o.Written == "kill");
            await Assert.That(Text(rules)).IsEqualTo(before).Because("re-picking what the file says writes nothing");

            kills.Selected = kills.Options.Single(o => o.Written == "death");
            await Assert.That(RulesetEdits.Field(Text(rules), "kills", "count")).IsEqualTo("death");
            await Assert.That(Node(vm, "kills").Facets.Select(f => f.Name)).IsEquivalentTo(["enemy"]).Because("death knows enemy too");

            CanvasTriggerViewModel damage = Node(vm, "damage").Trigger!;
            damage.Selected = damage.Options.Single(o => o.Written == "raw.player_hurt");
            await Assert.That(RulesetEdits.Field(Text(rules), "damage", "on")).IsEqualTo("raw.player_hurt");
            await Assert.That(Node(vm, "damage").Chips).Contains("on raw.player_hurt");

            CanvasTriggerViewModel burst = Node(vm, "double_burst").Trigger!;
            burst.Selected = burst.Options.Single(o => o.Written == "post_plant_kill");
            await Assert.That(RulesetEdits.Field(Text(rules), "double_burst", "burst")).IsEqualTo("post_plant_kill");

            // A tally has no picker: an on: under it is accepted by the loader and ignored by the resolver.
            await Assert.That(CanvasEditSemantics.TriggerKeyFor(Node(vm, "multi_kill"))).IsNull();
            await Assert.That(RulesetEdits.Field(Text(rules), "multi_kill", "on")).IsNull();
        }
    }

    /// <summary>
    ///     <c>compute: { value, live: true }</c> is the engine's live cadence. The face shows it as a row in both
    ///     styles, the formula line writes into the mapping's <c>value:</c> rather than over the mapping, and the
    ///     row turns a scalar compute into the mapping and takes the key away again.
    /// </summary>
    [Test]
    public async Task ALiveCompute_ShowsItsCadence_AndItsFormulaWritesIntoTheMapping()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("live_lab", """
            ruleset: live_lab
            title: Live lab
            for: each_player

            stats:
              kills:
                count: kill
                per: round
              live_ratio:
                compute: { value: "kills * 2", live: true }
                per: round
              end_ratio:
                compute: kills / max(kills, 1)
                per: round
            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "live_lab";
        vm.OnActivated(null!);
        string Text() => File.ReadAllText(rules.UserPath("live_lab"));
        using (Assert.Multiple())
        {
            await Assert.That(vm.Diagnostics).IsEmpty();
            await Assert.That(Choice(vm, "live_ratio", "live").Current).IsEqualTo("true");
            await Assert.That(Choice(vm, "live_ratio", "live").ReadText).IsEqualTo("true");
            await Assert.That(Choice(vm, "end_ratio", "live").IsDefault).IsTrue();
            await Assert.That(Node(vm, "live_ratio").Lines.Single(l => l.Key == "compute").Text).IsEqualTo("kills * 2");
        }

        CanvasExpressionViewModel formula = Node(vm, "live_ratio").Lines.Single(l => l.Key == "compute");
        formula.Text = "kills * 4";
        formula.Commit();
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Text(), "live_ratio", "compute.value")).IsEqualTo("kills * 4");
            await Assert.That(RulesetEdits.Field(Text(), "live_ratio", "compute.live")).IsEqualTo("true").Because("the formula write keeps the cadence");
        }

        CanvasChoiceViewModel end = Choice(vm, "end_ratio", "live");
        end.Selected = end.Options.Single(o => o.Value == "true");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Text(), "end_ratio", "compute.value")).IsEqualTo("kills / max(kills, 1)")
                .Because("the scalar becomes the mapping, quoted where the flow form needs it");
            await Assert.That(RulesetEdits.Field(Text(), "end_ratio", "compute.live")).IsEqualTo("true");
            await Assert.That(vm.Diagnostics).IsEmpty();
        }

        CanvasChoiceViewModel live = Choice(vm, "live_ratio", "live");
        live.Selected = live.Options[0];
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Text(), "live_ratio", "compute.live")).IsNull().Because("the default removes the key");
            await Assert.That(RulesetEdits.Field(Text(), "live_ratio", "compute.value")).IsEqualTo("kills * 4");
            await Assert.That(Choice(vm, "live_ratio", "live").IsDefault).IsTrue();
        }
    }

    /// <summary>
    ///     A flag with <c>on:</c> and <c>off:</c> is a latch. The face shows the deactivator as a second trigger
    ///     row with the same picker, writing <c>off:</c> and leaving <c>on:</c> and the facets alone.
    /// </summary>
    [Test]
    public async Task AFlagWithOff_ShowsADeactivatorRow_ThatWritesOff()
    {
        using RulesTree rules = RulesTree.New();
        rules.User("latch", """
            ruleset: latch
            title: Latch
            for: each_player

            stats:
              planted:
                flag: true
                on: bomb_planted
                off: round_ended
                per: round
              seen:
                flag:
                  when: "planted"
                per: round
            """);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "latch";
        vm.OnActivated(null!);
        string Text() => File.ReadAllText(rules.UserPath("latch"));
        CanvasNodeViewModel planted = Node(vm, "planted");
        using (Assert.Multiple())
        {
            await Assert.That(vm.Diagnostics).IsEmpty();
            await Assert.That(planted.Chips).Contains("on bomb_planted");
            await Assert.That(planted.Chips).Contains("off round_ended");
            await Assert.That(planted.ChipTips["off round_ended"]).StartsWith("round_ended");
            await Assert.That(planted.Trigger!.Key).IsEqualTo("on");
            await Assert.That(planted.OffTrigger!.Key).IsEqualTo("off");
            await Assert.That(planted.OffTrigger.Spelling).IsEqualTo("round_ended");
            await Assert.That(planted.OffTrigger.Options.Select(o => o.Written)).IsEquivalentTo(planted.Trigger.Options.Select(o => o.Written));
            await Assert.That(planted.ShowOffTriggerPicker).IsTrue();
            await Assert.That(planted.VisibleChips).DoesNotContain("off round_ended").Because("the picker stands in for the chip");
            await Assert.That(Node(vm, "seen").OffTrigger).IsNull();
            await Assert.That(Node(vm, "seen").ShowOffTriggerPicker).IsFalse();
        }

        planted.OffTrigger.Selected = planted.OffTrigger.Options.Single(o => o.Written == "bomb_defused");
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.Field(Text(), "planted", "off")).IsEqualTo("bomb_defused");
            await Assert.That(RulesetEdits.Field(Text(), "planted", "on")).IsEqualTo("bomb_planted");
            await Assert.That(Node(vm, "planted").Chips).Contains("off bomb_defused");
            await Assert.That(Node(vm, "planted").Chips).Contains("on bomb_planted");
        }
    }

    [Test]
    public async Task TheValueRow_WritesTheKindKey_RefusesEmpty_AndOffersThePathsOfTheView()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasFieldViewModel sum = Field(vm, "damage", "sum");
            using (Assert.Multiple())
            {
                await Assert.That(sum.Suggestions).Contains("enrich.hurt.capped_damage");
                await Assert.That(sum.Suggestions).Contains("event.DmgHealth");
                await Assert.That(sum.Suggestions).Contains("player.health");
                await Assert.That(Field(vm, "multi_kill", "tally").Suggestions).Contains("kills_match");
                await Assert.That(Field(vm, "multi_kill", "tally").Suggestions).DoesNotContain("multi_kill");
                await Assert.That(Field(vm, "multi_kill", "tally").Suggestions).DoesNotContain("ace");
                await Assert.That(Node(vm, "damage").Outputs.Count == 0 || Node(vm, "damage").Outputs[0].Title == "int").IsTrue();
                await Assert.That(Node(vm, "damage").OutputTitle).IsEqualTo("int");
            }

            sum.Text = "event.DmgHealth";
            sum.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "damage", "sum")).IsEqualTo("event.DmgHealth");

            CanvasFieldViewModel picked = Field(vm, "damage", "sum");
            picked.Picked = "player.health";
            await Assert.That(RulesetEdits.Field(Text(rules), "damage", "sum")).IsEqualTo("event.DmgHealth").Because("a pick lands in the editor; the commit writes");
            await Assert.That(picked.Text).IsEqualTo("player.health");
            picked.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "damage", "sum")).IsEqualTo("player.health");

            string before = Text(rules);
            CanvasFieldViewModel again = Field(vm, "damage", "sum");
            again.Text = "";
            again.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).IsEqualTo(before);
                await Assert.That(vm.EditStatus).Contains("needs a value");
            }

            CanvasFieldViewModel tally = Field(vm, "multi_kill", "tally");
            tally.Text = "kills_match";
            tally.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "multi_kill", "tally")).IsEqualTo("kills_match");
        }
    }

    [Test]
    public async Task AKeystrokeThatEqualsASuggestion_DoesNotCommit_TheTypedValueDoes()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = Text(rules);
            // An editable combo selects the item whose text equals the typed text, before the text binding
            // updates: typing 25 into a window field with the suggestion 2 arrives as Picked = 2 first.
            CanvasFieldViewModel window = Field(vm, "rapid", "window");
            window.Picked = "2";
            await Assert.That(Text(rules)).IsEqualTo(before).Because("the pick alone writes nothing, so the editor survives the keystroke");
            window.Picked = null;
            window.Text = "25";
            window.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "rapid", "window")).IsEqualTo("25");

            before = Text(rules);
            CanvasNodeViewModel odd = Node(vm, "odd_gate");
            odd.GateChoice = "seen";
            await Assert.That(Text(rules)).IsEqualTo(before).Because("a listed gate that prefixes the typed one must not write mid-keystroke");
            odd.GateChoice = null;
            odd.GateText = "seen_twice";
            odd.CommitGate();
            await Assert.That(RulesetEdits.Field(Text(rules), "odd_gate", "while")).IsEqualTo("seen_twice");
        }
    }

    [Test]
    public async Task TheGateEditor_ListsContextPathsAndFlags_KeepsAnUnlistedValue_AndWritesOnlyOnCommit()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            string before = Text(rules);
            CanvasNodeViewModel odd = Node(vm, "odd_gate");
            CanvasNodeViewModel gated = Node(vm, "gated_kills");
            using (Assert.Multiple())
            {
                await Assert.That(odd.GateChoices).Contains("round.active");
                await Assert.That(odd.GateChoices).Contains("seen").Because("a sibling flag can gate");
                await Assert.That(odd.GateChoices).DoesNotContain("round.number > 0");
                await Assert.That(odd.Gate).IsEqualTo("round.number > 0");
                await Assert.That(odd.GateText).IsEqualTo("round.number > 0");
                await Assert.That(odd.GateChoice).IsNull();
                await Assert.That(odd.IsGateListed).IsFalse();
                await Assert.That(odd.VisibleChips).Contains("while round.number > 0").Because("an unlisted gate keeps its chip beside the editor");
                await Assert.That(gated.GateChoice).IsEqualTo("seen");
                await Assert.That(gated.VisibleChips).DoesNotContain("while seen").Because("a listed gate is shown by the picker");
                await Assert.That(Text(rules)).IsEqualTo(before).Because("loading the gate never writes it back");
            }

            odd.GateText = "round.number > 3";
            await Assert.That(Text(rules)).IsEqualTo(before).Because("typing does not write; the commit does");
            odd.CommitGate();
            await Assert.That(RulesetEdits.Field(Text(rules), "odd_gate", "while")).IsEqualTo("round.number > 3");

            CanvasNodeViewModel pickedGate = Node(vm, "odd_gate");
            pickedGate.GateChoice = "player.alive";
            await Assert.That(RulesetEdits.Field(Text(rules), "odd_gate", "while")).IsEqualTo("round.number > 3").Because("a pick lands in the editor; the commit writes");
            await Assert.That(pickedGate.GateText).IsEqualTo("player.alive");
            pickedGate.CommitGate();
            await Assert.That(RulesetEdits.Field(Text(rules), "odd_gate", "while")).IsEqualTo("player.alive");

            CanvasNodeViewModel cleared = Node(vm, "odd_gate");
            cleared.GateText = "";
            cleared.CommitGate();
            await Assert.That(RulesetEdits.Field(Text(rules), "odd_gate", "while")).IsNull();

            Node(vm, "kills").GateChoice = "seen";
            Node(vm, "kills").CommitGate();
            await Assert.That(RulesetEdits.Field(Text(rules), "kills", "while")).IsEqualTo("seen");
        }
    }

    [Test]
    public async Task TheActorBinding_IsAChip_AndTheToggleWritesActorAny()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel kills = Node(vm, "kills");
            CanvasNodeViewModel contact = Node(vm, "first_contact");
            using (Assert.Multiple())
            {
                await Assert.That(kills.Chips).Contains("as killer");
                await Assert.That(kills.BindsActor == true).IsTrue();
                await Assert.That(kills.ShowActorToggle).IsTrue();
                await Assert.That(kills.FixedRows).IsEquivalentTo(["not a suicide"]);
                await Assert.That(contact.Chips).Contains("any player");
                await Assert.That(contact.Chips).DoesNotContain("as attacker");
                await Assert.That(contact.BindsActor == false).IsTrue();
                await Assert.That(contact.Facets.Select(f => f.Name)).IsEquivalentTo(["enemy"]).Because("actor is the chip, not a facet row");
                await Assert.That(Node(vm, "side").ShowActorToggle).IsFalse().Because("a raw event binds nobody and has nothing to switch");
                await Assert.That(Node(vm, "side").Chips).DoesNotContain("binds nobody");
                await Assert.That(Node(vm, "seen").ChipTips).IsEmpty();
            }

            kills.BindsActor = false;
            await Assert.That(RulesetEdits.Field(Text(rules), "kills", "match.actor")).IsEqualTo("any");
            await Assert.That(Node(vm, "kills").Chips).Contains("any player");

            Node(vm, "kills").BindsActor = true;
            await Assert.That(RulesetEdits.Field(Text(rules), "kills", "match.actor")).IsNull();
            await Assert.That(Text(rules)).Contains("match: { enemy: true }");
            await Assert.That(Node(vm, "kills").Chips).Contains("as killer");
        }
    }

    [Test]
    public async Task TheKindPickers_WriteKeepFormatAndReduce_AndTheDefaultEntryRemovesTheKey()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasChoiceViewModel keep = Choice(vm, "first_contact", "keep");
            await Assert.That(keep.Current).IsEqualTo("first");
            keep.Selected = keep.Options.Single(o => o.Value == "list");
            await Assert.That(RulesetEdits.Field(Text(rules), "first_contact", "keep")).IsEqualTo("list");

            keep = Choice(vm, "first_contact", "keep");
            keep.Selected = keep.Options[0];
            await Assert.That(RulesetEdits.Field(Text(rules), "first_contact", "keep")).IsNull();
            await Assert.That(Choice(vm, "first_contact", "keep").ReadText).IsEqualTo("last (default)");

            CanvasChoiceViewModel format = Choice(vm, "kpr", "format");
            format.Selected = format.Options.Single(o => o.Value == "F0");
            await Assert.That(RulesetEdits.Field(Text(rules), "kpr", "format")).IsEqualTo("F0");

            CanvasChoiceViewModel reduce = Choice(vm, "damage_by_weapon", "reduce");
            reduce.Selected = reduce.Options.Single(o => o.Value == "max");
            await Assert.That(RulesetEdits.Field(Text(rules), "damage_by_weapon", "reduce")).IsEqualTo("max");

            CanvasChoiceViewModel kind = Choice(vm, "quad", "kind");
            await Assert.That(kind.ReadText).IsEqualTo("highlight (default)");
            kind.Selected = kind.Options.Single(o => o.Value == "hidden");
            await Assert.That(RulesetEdits.HighlightField(Text(rules), "quad", "kind")).IsEqualTo("hidden");
        }
    }

    [Test]
    public async Task TheLabel_IsEditedInTheHeader_AndEmptyRemovesIt()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasNodeViewModel kpr = Node(vm, "kpr");
            kpr.BeginEditLabelCommand.Execute(null);
            await Assert.That(kpr.IsEditingLabel).IsTrue();
            await Assert.That(kpr.LabelText).IsEqualTo("KPR");
            kpr.LabelText = "Kills per round";
            kpr.CommitLabel();
            await Assert.That(RulesetEdits.Field(Text(rules), "kpr", "label")).IsEqualTo("Kills per round");
            await Assert.That(Node(vm, "kpr").Label).IsEqualTo("Kills per round");

            CanvasNodeViewModel kills = Node(vm, "kills");
            await Assert.That(kills.Label).IsNull();
            kills.BeginEditLabelCommand.Execute(null);
            kills.LabelText = "Frags";
            kills.CommitLabel();
            await Assert.That(RulesetEdits.Field(Text(rules), "kills", "label")).IsEqualTo("Frags");

            CanvasNodeViewModel again = Node(vm, "kills");
            again.BeginEditLabelCommand.Execute(null);
            again.LabelText = "";
            again.CommitLabel();
            await Assert.That(RulesetEdits.Field(Text(rules), "kills", "label")).IsNull();
            await Assert.That(Node(vm, "ace").CanEditLabel).IsFalse().Because("a highlight's title has its own line");
        }
    }

    [Test]
    public async Task StreakBurstBucketRateAndGroup_WriteTheirKeys_AndEmptyRemovesTheOptionalOnes()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasFieldViewModel window = Field(vm, "rapid", "window");
            window.Text = "5s";
            window.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "rapid", "window")).IsEqualTo("5s");

            CanvasFieldViewModel min = Field(vm, "double_burst", "min_streak");
            min.Text = "3";
            min.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "double_burst", "min_streak")).IsEqualTo("3");

            min = Field(vm, "double_burst", "min_streak");
            min.Text = "";
            min.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "double_burst", "min_streak")).IsNull();
            await Assert.That(Field(vm, "double_burst", "min_streak").ReadText).IsEqualTo("engine (default)");

            CanvasFieldViewModel key = Field(vm, "kills_by_weapon", "key");
            key.Text = "event.Headshot";
            key.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "kills_by_weapon", "key")).IsEqualTo("event.Headshot");

            CanvasFieldViewModel value = Field(vm, "damage_by_weapon", "value");
            value.Text = "event.DmgHealth";
            value.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "damage_by_weapon", "value")).IsEqualTo("event.DmgHealth");

            CanvasFieldViewModel of = Field(vm, "damage_per_kill", "rate.of");
            of.Text = "kills_by_weapon";
            of.Commit();
            await Assert.That(RulesetEdits.Field(Text(rules), "damage_per_kill", "rate.of")).IsEqualTo("kills_by_weapon");

            string before = Text(rules);
            CanvasFieldViewModel per = Field(vm, "damage_per_kill", "rate.per");
            per.Text = "";
            per.Commit();
            await Assert.That(Text(rules)).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("both buckets");

            CanvasFieldViewModel group = Field(vm, "quad", "group");
            group.Picked = "multikill";
            group.Commit();
            await Assert.That(RulesetEdits.HighlightField(Text(rules), "quad", "group")).IsEqualTo("multikill");

            group = Field(vm, "ace", "group");
            group.Text = "";
            group.Commit();
            await Assert.That(RulesetEdits.HighlightField(Text(rules), "ace", "group")).IsNull();
        }
    }

    [Test]
    public async Task TheThresholdsTable_WritesTheWholeList_AddsAndRemovesRows_AndKeepsTheLastOne()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            CanvasThresholdsViewModel table = Node(vm, "multi_kill").Thresholds!;
            table.Rows[0].Min = "5";
            table.Rows[0].Target = "rounds_5k";
            table.Rows[0].CommitCommand.Execute(null);
            await Assert.That(Text(rules)).Contains("thresholds:\n      - { min: 5, target: rounds_5k }\n      - { min: 2, target: rounds_2k }");

            table = Node(vm, "multi_kill").Thresholds!;
            table.AddCommand.Execute(null);
            await Assert.That(Text(rules)).Contains("- { min: 6, target: at_least_6 }");
            await Assert.That(Node(vm, "multi_kill").Thresholds!.Rows.Count).IsEqualTo(3);

            table = Node(vm, "multi_kill").Thresholds!;
            table.Rows[2].RemoveCommand.Execute(null);
            table = Node(vm, "multi_kill").Thresholds!;
            table.Rows[1].RemoveCommand.Execute(null);
            await Assert.That(Text(rules)).DoesNotContain("at_least_6");
            await Assert.That(Text(rules)).DoesNotContain("rounds_2k");

            table = Node(vm, "multi_kill").Thresholds!;
            string before = Text(rules);
            table.Rows[0].RemoveCommand.Execute(null);
            await Assert.That(Text(rules)).IsEqualTo(before).Because("a tally keeps at least one threshold");

            table.Rows[0].Target = "";
            table.Rows[0].CommitCommand.Execute(null);
            await Assert.That(Text(rules)).IsEqualTo(before);
            await Assert.That(vm.EditStatus).Contains("threshold");
        }
    }

    [Test]
    public async Task ABlankCellInAMultiRowTable_IsRefused_NotDroppedFromTheFile()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            Node(vm, "multi_kill").Thresholds!.AddCommand.Execute(null);
            string before = Text(rules);
            CanvasThresholdsViewModel table = Node(vm, "multi_kill").Thresholds!;
            await Assert.That(table.Rows.Count).IsEqualTo(3);

            table.Rows[1].Min = "";
            table.Rows[1].CommitCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).IsEqualTo(before).Because("a cleared minimum is a refusal, not a deleted threshold");
                await Assert.That(Text(rules)).Contains("rounds_2k");
                await Assert.That(vm.EditStatus).Contains("minimum");
            }

            table = Node(vm, "multi_kill").Thresholds!;
            table.Rows[1].Min = "2";
            table.Rows[1].Target = "";
            table.Rows[1].CommitCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(Text(rules)).IsEqualTo(before).Because("a cleared target is refused the same way");
                await Assert.That(vm.EditStatus).Contains("target");
                await Assert.That(CanvasThresholdsViewModel.Parse("5 rounds_5k\n rounds_4k\n3 ")).IsEquivalentTo([("5", "rounds_5k"), ("", "rounds_4k"), ("3", "")]);
                await Assert.That(CanvasThresholdsViewModel.Parse("")).IsEmpty();
            }
        }
    }

    [Test]
    public async Task AShippedRuleset_CarriesEveryNewEditorSwitchedOff()
    {
        using RulesTree rules = RulesTree.New();
        rules.Shipped("faces_lab", Lab);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "faces_lab";
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.ShippedPath("faces_lab"));
        CanvasNodeViewModel kills = Node(vm, "kills");
        kills.Trigger!.Selected = kills.Trigger.Options.Single(o => o.Written == "death");
        kills.BindsActor = false;
        kills.GateText = "seen";
        kills.CommitGate();
        kills.GateChoice = "seen";
        Field(vm, "damage", "sum").Text = "1";
        Field(vm, "damage", "sum").Commit();
        Choice(vm, "kpr", "format").Selected = Choice(vm, "kpr", "format").Options[0];
        Node(vm, "multi_kill").Thresholds!.AddCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(kills.ShowTriggerPicker).IsFalse();
            await Assert.That(kills.ShowActorToggle).IsFalse();
            await Assert.That(kills.VisibleChips).Contains("on kill");
            await Assert.That(File.ReadAllText(rules.ShippedPath("faces_lab"))).IsEqualTo(before);
        }
    }

    [Test]
    public async Task EveryCorpusRuleset_RendersAFaceForEveryKind_WithoutThrowing()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        CanvasNodeFactory factory = new((_, _, _) => { }, (_, _, _) => { });
        HashSet<string> kinds = [];
        int nodes = 0;
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            RulesetCorpus.Composed composed = corpus.Compose(entry.Id);
            foreach (NodeFace face in NodeFaceBuilder.Build(composed))
            {
                CanvasNodeViewModel node = factory.Create(face);
                node.IsEditable = face.IsAuthorable;
                nodes++;
                if (face is StatFace stat)
                {
                    kinds.Add(stat.Kind);
                    await Assert.That(node.Trigger is null == !stat.HasTrigger).IsTrue().Because(face.Key.ToString());
                    await Assert.That(stat.ActorText is null == (FaceVocabulary.ViewOf(stat.View) is null)).IsTrue().Because(face.Key.ToString());
                }

                if (face is TallyFace)
                {
                    await Assert.That(node.Thresholds!.Rows.Count).IsGreaterThan(0).Because(face.Key.ToString());
                }

                _ = NodeSizeEstimator.Estimate(node);
            }
        }

        using (Assert.Multiple())
        {
            await Assert.That(corpus.Entries.Count).IsEqualTo(21);
            await Assert.That(nodes).IsGreaterThan(300);
            await Assert.That(kinds).Contains("count");
            await Assert.That(kinds).Contains("sum");
            await Assert.That(kinds).Contains("capture");
            await Assert.That(kinds).Contains("flag");
            await Assert.That(kinds).Contains("compute");
            await Assert.That(kinds).Contains("tally");
            await Assert.That(kinds).Contains("streak");
            await Assert.That(kinds).Contains("bucket");
            await Assert.That(kinds).Contains("burst");
        }
    }
}
