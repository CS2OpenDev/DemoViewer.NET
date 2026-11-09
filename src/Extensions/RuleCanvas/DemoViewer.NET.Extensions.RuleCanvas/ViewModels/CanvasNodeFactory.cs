#region

using System.Globalization;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Builds the node view model for a face: the trigger, actor binding, gate and scope as chips; the
///     <c>match:</c> form as facet rows written the way the author wrote them; the kind's own keys as rows
///     and pickers; the <c>compute:</c>, flag, <c>where:</c> or <c>when:</c> text as the expression line
///     and, for an authorable node, as editors. The writers given here are what every row and picker
///     writes through, with the node as the first argument.
/// </summary>
/// <param name="writeFacet">Writes a facet of the node: a value to set, or null to remove it. Null writes nowhere.</param>
/// <param name="writeField">Writes a field of the node: a value to set, or null to remove it. Null writes nowhere.</param>
public sealed class CanvasNodeFactory(
    Action<CanvasNodeViewModel, string, string?>? writeFacet = null,
    Action<CanvasNodeViewModel, string, string?>? writeField = null)
{
    /// <summary>The <c>keep:</c> modes a capture or a sum can name.</summary>
    public static IReadOnlyList<string> KeepOptions { get; } = ["first", "last", "list", "min", "max"];

    /// <summary>The <c>format:</c> strings a compute commonly uses; the picker keeps any other as written.</summary>
    public static IReadOnlyList<string> FormatOptions { get; } = ["F0", "F1", "F2", "R"];

    /// <summary>The <c>live:</c> cadence of a compute; the omitted key evaluates at the scope's end.</summary>
    public static IReadOnlyList<string> LiveOptions { get; } = ["true", "false"];

    /// <summary>The <c>reduce:</c> names a bucket can use.</summary>
    public static IReadOnlyList<string> ReduceOptions { get; } = ["sum", "count", "min", "max", "last", "first"];

    /// <summary>The <c>kind:</c> values a highlight can carry.</summary>
    public static IReadOnlyList<string> HighlightKindOptions { get; } = ["highlight", "funny", "lowlight", "hidden"];

    /// <summary>The window spellings a streak or a burst commonly uses.</summary>
    public static IReadOnlyList<string> WindowOptions { get; } = ["2", "640", "3s", "5s", "7s", "9s"];

    /// <summary>A factory whose nodes write nowhere, for a canvas that is only drawn.</summary>
    public static CanvasNodeFactory ReadOnly { get; } = new();

    public CanvasNodeViewModel Create(NodeFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        List<string> chips = [];
        Dictionary<string, string> tips = new(StringComparer.Ordinal);
        List<CanvasFacetViewModel> facets = [];
        List<string> available = [];
        Dictionary<string, RulesTypeKind> availableKinds = new(StringComparer.Ordinal);
        List<string> fixedRows = [];
        Dictionary<string, string> fixedTips = new(StringComparer.Ordinal);
        List<string> expression = [];
        List<CanvasExpressionViewModel> lines = [];
        List<CanvasFieldViewModel> fields = [];
        List<CanvasChoiceViewModel> choices = [];
        List<string> defaults = [];
        CanvasTriggerViewModel? trigger = null;
        CanvasTriggerViewModel? off = null;
        CanvasThresholdsViewModel? thresholds = null;
        string? viewActor = null;
        bool? bindsActor = null;
        CanvasNodeViewModel? built = null;
        // Rows write through the node they belong to; the node writes through the factory's writers.
        void Facet(string facet, string? value) => built?.WriteFacet(facet, value);
        void Field(string key, string? value) => built?.WriteField(key, value);

        List<CanvasColumnViewModel> columns = [];
        switch (face)
        {
            case ShowFace show:
                chips.Add(show.Grain);
                tips[show.Grain] = show.IsScoreboard
                    ? "The scoreboard: one column per entry, grouped as the entries say"
                    : "The table's grain, which must match the ruleset's for:";
                if (show.On is { } on)
                {
                    chips.Add("on " + on);
                    tips["on " + on] = "An event table: one row per firing of this trigger";
                }

                columns.AddRange(show.Columns.Select(c => new CanvasColumnViewModel(c)));
                break;
            case ParamsFace parameters:
                foreach (ParamDef param in parameters.Params)
                {
                    fields.Add(new CanvasFieldViewModel("param." + param.Name, ParamLabel(param), ParamText(param.Default), [], null, Field));
                }

                break;
            case DependencyFace dependency:
                chips.Add(dependency.EntryCountText);
                tips[dependency.EntryCountText] = $"use: [ {dependency.RulesetId} ] folded to one node; Expand draws its entries, Open {dependency.RulesetId} switches to it";
                if (dependency.IsUserCopy)
                {
                    chips.Add("your copy");
                    tips["your copy"] = $"Your copy overrides the shipped ruleset of this id.\nyours: {dependency.UserPath}\nshipped: {dependency.ShippedPath}";
                }

                break;
            case ScaffoldFace scaffold:
                if (scaffold.IsPerPlayer)
                {
                    chips.Add("each player");
                }
                else if (scaffold.IsPerTeam)
                {
                    chips.Add("each team");
                }

                if (scaffold.IsRoundFact)
                {
                    chips.Add("round fact");
                    tips["round fact"] = "Read once at the plant and held to the next freeze end; no trigger drives it";
                }

                // A merged event node names the wire events it stands for; a single event is the node's name.
                if (scaffold.Events.Count > 1)
                {
                    string merged = $"{scaffold.Events.Count} events";
                    chips.Add(merged);
                    tips[merged] = string.Join('\n', scaffold.Events);
                }

                break;
            case HighlightFace highlight:
                AddScope(chips, highlight.Per);
                expression.Add("when " + highlight.When);
                lines.Add(new CanvasExpressionViewModel("when", "when", highlight.When ?? "", Field));
                lines.Add(new CanvasExpressionViewModel("title", "title", highlight.Title ?? "", Field));
                lines.Add(new CanvasExpressionViewModel("score", "score", highlight.Score?.ToString(CultureInfo.InvariantCulture) ?? "", Field));
                choices.Add(new CanvasChoiceViewModel("kind", "kind", HighlightKindOptions, highlight.HighlightKind, "highlight", Field));
                fields.Add(new CanvasFieldViewModel("group", "group", highlight.Group, face.Vocabulary.Groups, null, Field));
                if (highlight.Score is null)
                {
                    defaults.Add("score 50");
                }

                AddReads(chips, highlight.CrossRulesetReads);
                break;
            case UndeclaredFace undeclared:
                if (!undeclared.IsHighlight)
                {
                    AddTrigger(chips, undeclared.View, undeclared.Events);
                }

                AddReads(chips, undeclared.CrossRulesetReads);
                break;
            case DefineFace named:
                // The same editors a count has, less the actor toggle and the label: a define binds no player of
                // its own and has no label key. Its picker offers no define, since a define cannot fire on one.
                trigger = new CanvasTriggerViewModel("on", named.TriggerSpelling, named.View, named.Events,
                    FaceVocabulary.TriggersForDefine(named.TriggerSpelling), Field);
                if (trigger.Chip.Length > 0)
                {
                    chips.Add(trigger.Chip);
                    tips[trigger.Chip] = trigger.Tip;
                }

                if (named.SuppressesActorBinding)
                {
                    chips.Add("any player");
                    tips["any player"] = "match: { actor: any } switches the view's per-player binding off for every stat firing on this define";
                }

                if (named.Gate.Length > 0)
                {
                    chips.Add("while " + named.Gate);
                }

                AddFacetRows(named.Form, facets, available, availableKinds, Facet);
                if (named.NotASuicide)
                {
                    fixedRows.Add("not a suicide");
                    fixedTips["not a suicide"] = "Baked into the view: event.Attacker != event.UserId";
                }

                if (!string.IsNullOrWhiteSpace(named.Trigger.Where))
                {
                    expression.Add("where " + named.Trigger.Where);
                }

                lines.Add(new CanvasExpressionViewModel("where", "where", named.Trigger.Where ?? "", Field));
                break;
            case StatFace stat:
                trigger = AddStatTrigger(stat, chips, tips, Field);
                if (stat is FlagFace { OffTrigger.On: not null } latch)
                {
                    off = AddOffTrigger(latch, chips, tips, Field);
                }

                if (stat.ActorText is { } actor)
                {
                    chips.Add(actor);
                    tips[actor] = ActorTip(stat);
                    viewActor = stat.Binding;
                    bindsActor = viewActor is null ? null : !stat.SuppressesActorBinding;
                }

                if (stat.Gate.Length > 0)
                {
                    chips.Add("while " + stat.Gate);
                }

                // The mapper fills per: round in before the face sees the entry, so the scope is never a default here.
                AddScope(chips, stat.Per);
                AddFacetRows(stat.Form, facets, available, availableKinds, Facet);
                if (stat.NotASuicide)
                {
                    fixedRows.Add("not a suicide");
                    fixedTips["not a suicide"] = "Baked into the view: event.Attacker != event.UserId";
                }

                if (stat.ViewResult is { } result && !stat.SuppressesActorBinding)
                {
                    string row = $"round {result} by this player's team";
                    fixedRows.Add(row);
                    fixedTips[row] = "The view's result, lowered to " + stat.TeamBindingTest;
                }

                // The define's facets are its node's rows; the resolver refuses the same key on the define and
                // the site, so the picker stops offering them here.
                foreach (MatchBinding bound in stat.Define?.Trigger.Match ?? [])
                {
                    available.Remove(bound.Key);
                    availableKinds.Remove(bound.Key);
                }

                AddKindRows(stat, expression, lines, fields, choices, defaults, out thresholds, Field);

                if (!string.IsNullOrWhiteSpace(stat.Trigger?.Where))
                {
                    expression.Add("where " + stat.Trigger.Where);
                }

                if (stat is not ComputeFace)
                {
                    lines.Add(new CanvasExpressionViewModel("where", "where", stat.Trigger?.Where ?? "", Field));
                }

                AddReads(chips, stat.CrossRulesetReads);
                break;
        }

        if (!face.IsAuthorable && !face.IsScaffold && !face.IsDependency)
        {
            chips.Add("from " + face.Ruleset);
        }

        // The event's own fields come after the catalog facets; picking one adds a where row, so the
        // picker is the one place to find what an event carries without a catalog change.
        string? fieldView = face switch { StatFace s => s.View, DefineFace d => d.View, _ => null };
        if (fieldView is not null && lines.Any(l => l.Key == "where"))
        {
            foreach ((string path, RulesTypeKind kind) in ConditionVocabulary.EventFieldsFor(EmbeddedCatalog.Root, fieldView))
            {
                available.Add(path);
                availableKinds[path] = kind;
            }
        }

        IReadOnlyList<string> gateChoices = face is StatFace or DefineFace
            ? [.. CanvasNodeViewModel.GateOptions, .. face.Vocabulary.Flags.Where(f => f != face.Id)]
            : CanvasNodeViewModel.GateOptions;
        built = new CanvasNodeViewModel
        {
            Face = face,
            Id = face.Id,
            Ruleset = face.Ruleset,
            Kind = face.Kind,
            Label = face.Label,
            View = (face as StatFace)?.View ?? (face as UndeclaredFace)?.View ?? (face as DefineFace)?.View,
            ValueType = face.ValueType,
            Events = face switch
            {
                StatFace stat => stat.Events,
                DefineFace define => define.Events,
                UndeclaredFace undeclared => undeclared.Events,
                ScaffoldFace scaffold => scaffold.Events,
                _ => []
            },
            Chips = chips,
            ChipTips = tips,
            Facets = facets,
            AvailableFacets = available,
            AvailableFacetKind = name => availableKinds.GetValueOrDefault(name, RulesTypeKind.None),
            FixedRows = fixedRows,
            FixedRowTips = fixedTips,
            Expression =expression.Count == 0 ? null : string.Join('\n', expression),
            Lines = lines,
            Trigger = trigger,
            OffTrigger = off,
            Fields = fields,
            Choices = choices,
            Thresholds = thresholds,
            Defaults = defaults,
            Columns = columns,
            DependencyEntries = (face as DependencyFace)?.Entries ?? [],
            ViewActor = viewActor,
            BindsActor = bindsActor,
            Per = face.Per,
            Gate = face.Gate,
            GateText = face.Gate,
            GateChoice = gateChoices.Contains(face.Gate, StringComparer.Ordinal) ? face.Gate : null,
            GateChoices = gateChoices,
            LabelText = face.Label ?? "",
            SourceLine = face.SourceLine,
            IsAuthorable = face.IsAuthorable,
            Diagnostics = face.Diagnostics
        };
        CanvasNodeViewModel node = built;
        node.AttachWriters((facet, value) => writeFacet?.Invoke(node, facet, value), (key, value) => writeField?.Invoke(node, key, value));
        return node;
    }

    // The trigger chip carries the spelling the author wrote; the resolved view and the events are its tip.
    // A face that fires on nothing (a compute, an expression flag) shows its wire events, if any, as before.
    private static CanvasTriggerViewModel? AddStatTrigger(StatFace stat, List<string> chips, Dictionary<string, string> tips,
        Action<string, string?> write)
    {
        if (!stat.HasTrigger || StatFace.TriggerKeyOf(stat.Kind) is not { } key)
        {
            AddTrigger(chips, stat.View, stat.Events);
            return null;
        }

        CanvasTriggerViewModel trigger = new(key, stat.TriggerSpelling, stat.View, stat.Events, stat.Vocabulary.TriggersFor(stat.TriggerSpelling), write)
        {
            IsDefine = stat.Define is not null
        };
        if (trigger.Chip.Length > 0)
        {
            chips.Add(trigger.Chip);
            tips[trigger.Chip] = trigger.Tip;
        }
        else
        {
            AddTrigger(chips, stat.View, stat.Events);
        }

        return trigger;
    }

    // A flag's deactivator: the same picker as the on: row, written under off:. The checked model does not
    // carry the off: view, so the chip's tip comes from the catalog by the spelling alone.
    private static CanvasTriggerViewModel AddOffTrigger(FlagFace flag, List<string> chips, Dictionary<string, string> tips,
        Action<string, string?> write)
    {
        string spelling = flag.OffTrigger!.Spelling!;
        IReadOnlyList<TriggerOption> options = flag.Vocabulary.TriggersFor(spelling);
        TriggerOption? known = options.FirstOrDefault(o => string.Equals(o.Written, spelling, StringComparison.Ordinal));
        CanvasTriggerViewModel off = new("off", spelling, FaceVocabulary.ViewOf(spelling)?.Name, known?.Events ?? [], options, write, "off");
        chips.Add(off.Chip);
        tips[off.Chip] = off.Tip;
        return off;
    }

    // The typed form as rows: the bound facets as editors, the unbound ones offered by the picker, and a key
    // the view does not know as a row the resolver already flagged. actor: is the engine's reserved key, shown
    // as the binding chip rather than as a facet.
    private static void AddFacetRows(MatchFacetForm form, List<CanvasFacetViewModel> facets, List<string> available,
        Dictionary<string, RulesTypeKind> availableKinds, Action<string, string?> write)
    {
        foreach (MatchFacetRow row in form.Rows)
        {
            if (row.IsSet)
            {
                facets.Add(new CanvasFacetViewModel(row.Name, row.Type.Kind, row.Value!.ToMatchValue(), write));
            }
            else
            {
                available.Add(row.Name);
                availableKinds[row.Name] = row.Type.Kind;
            }
        }

        foreach (MatchBinding unknown in form.Unknown)
        {
            if (!string.Equals(unknown.Key, "actor", StringComparison.Ordinal))
            {
                facets.Add(new CanvasFacetViewModel(unknown.Key, RulesTypeKind.None, MatchFacetValue.From(unknown.Test)?.ToMatchValue() ?? "", write));
            }
        }
    }

    private static string ActorTip(StatFace stat)
    {
        if (stat.SuppressesActorBinding)
        {
            return "match: { actor: any } switches the view's per-player binding off; every player's event counts";
        }

        if (stat.ViewActor is { } actor)
        {
            return $"the view binds its {actor} to this player; only this player's events count";
        }

        return stat.ViewResult is not null
            ? $"the view binds the round's result to this player's team: {stat.TeamBindingTest}"
            : "the view binds no role to a player; the event counts for everyone";
    }

    // Each kind's own keys, named here once: the view model rows a template draws and the semantics write.
    private static void AddKindRows(StatFace stat, List<string> expression, List<CanvasExpressionViewModel> lines,
        List<CanvasFieldViewModel> fields, List<CanvasChoiceViewModel> choices, List<string> defaults,
        out CanvasThresholdsViewModel? thresholds, Action<string, string?> write)
    {
        thresholds = null;
        IReadOnlyList<string> values = [.. FaceVocabulary.ValuePathsFor(stat.View), .. FaceVocabulary.PlayerContextPaths];
        switch (stat)
        {
            case ComputeFace compute:
                if (stat.KindArg is { } formula)
                {
                    expression.Add("= " + formula);
                }

                lines.Add(new CanvasExpressionViewModel("compute", "=", stat.KindArg ?? "", write));
                choices.Add(new CanvasChoiceViewModel("format", "format", FormatOptions, compute.Format, "F1", write));
                // The cadence is a row in both styles, so a live compute is told from a round-end one while editing too.
                choices.Add(new CanvasChoiceViewModel("live", "live", LiveOptions, compute.Live ? "true" : null, "false", write));
                break;
            case FlagFace:
                if (stat.Trigger?.On is null && stat.KindArg is { } condition && !string.Equals(condition.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                {
                    expression.Add("when " + condition);
                }

                if (stat.Trigger?.On is null)
                {
                    lines.Add(new CanvasExpressionViewModel("when", "when", stat.KindArg ?? "", write));
                }

                break;
            case SumFace sum:
                AddValue(stat, "sum", values, expression, fields, write);
                if (sum.Keep is { } keep && keep != KeepMode.None)
                {
                    choices.Add(new CanvasChoiceViewModel("keep", "keep", KeepOptions, KeepText(keep), "last", write));
                }

                break;
            case CaptureFace capture:
                AddValue(stat, "capture", values, expression, fields, write);
                choices.Add(new CanvasChoiceViewModel("keep", "keep", KeepOptions, KeepText(capture.Keep), "last", write));
                break;
            case TallyFace tally:
                AddValue(stat, "tally", stat.Vocabulary.Stats.Where(s => s != stat.Id).ToList(), expression, fields, write);
                thresholds = new CanvasThresholdsViewModel(
                    [.. tally.Thresholds.Select(t => (TallyMinText(t.Min), t.Target))], write);
                break;
            case StreakFace streak:
                AddWindow(streak.StreakWindow, streak.StreakMinStreak, expression, fields, write);
                break;
            case BurstFace burst:
                AddWindow(burst.Window, burst.MinStreak, expression, fields, write);
                break;
            case BucketFace bucket:
                string? key = bucket.BucketKey ?? (bucket.BucketKeys is { Count: > 0 } keys ? "[" + string.Join(", ", keys) + "]" : null);
                if (key is not null)
                {
                    expression.Add("key " + key);
                }

                fields.Add(new CanvasFieldViewModel("key", "key", key, values, null, write,
                    bucket.BucketKeys is { Count: > 0 }, "a list key is edited in the source pane"));
                if (bucket.BucketValue is not null)
                {
                    expression.Add("value " + bucket.BucketValue);
                }

                fields.Add(new CanvasFieldViewModel("value", "value", bucket.BucketValue, values, "count per key", write));
                choices.Add(new CanvasChoiceViewModel("reduce", "reduce", ReduceOptions, bucket.BucketReduce,
                    bucket.BucketValue is null ? "count" : "sum", write));
                break;
            case RateFace rate:
                if (rate.RateOf is not null || rate.RatePer is not null)
                {
                    expression.Add($"{rate.RateOf ?? "?"} / {rate.RatePer ?? "?"}");
                }

                fields.Add(new CanvasFieldViewModel("rate.of", "of", rate.RateOf, stat.Vocabulary.Buckets, null, write));
                fields.Add(new CanvasFieldViewModel("rate.per", "per", rate.RatePer, stat.Vocabulary.Buckets, null, write));
                break;
        }
    }

    private static void AddValue(StatFace stat, string key, IReadOnlyList<string> suggestions, List<string> expression,
        List<CanvasFieldViewModel> fields, Action<string, string?> write)
    {
        if (!string.IsNullOrWhiteSpace(stat.KindArg))
        {
            expression.Add("= " + stat.KindArg);
        }

        fields.Add(new CanvasFieldViewModel(key, "=", stat.KindArg, suggestions, null, write));
    }

    private static void AddWindow(string? window, int? minStreak, List<string> expression, List<CanvasFieldViewModel> fields,
        Action<string, string?> write)
    {
        if (window is not null || minStreak is not null)
        {
            expression.Add($"window {window ?? "default"}, min streak {minStreak?.ToString(CultureInfo.InvariantCulture) ?? "default"}");
        }

        fields.Add(new CanvasFieldViewModel("window", "window", window, WindowOptions, "engine", write));
        fields.Add(new CanvasFieldViewModel("min_streak", "min streak", minStreak?.ToString(CultureInfo.InvariantCulture), ["2", "3", "4", "5"],
            "engine", write));
    }

    private static string? KeepText(KeepMode? keep) => keep is null or KeepMode.None ? null : keep.Value.ToString().ToLowerInvariant();

    // The row's label names the param and its type, with the bounds when the file sets any: `min_kills (int 2..5)`.
    private static string ParamLabel(ParamDef param)
    {
        string type = param.Type.ToString().ToLowerInvariant();
        string bounds = param.Min is null && param.Max is null
            ? ""
            : $" {param.Min?.ToString(CultureInfo.InvariantCulture) ?? ""}..{param.Max?.ToString(CultureInfo.InvariantCulture) ?? ""}";
        return $"{param.Name} ({type}{bounds})";
    }

    /// <summary>A param's default as the file would spell it: <c>60</c>, <c>0.5</c>, <c>true</c>, <c>10s</c>; null for none.</summary>
    public static string? ParamText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string TallyMinText(TallyMin min) => min switch
    {
        TallyMinLiteral literal => literal.Value.ToString(CultureInfo.InvariantCulture),
        TallyMinParam param => param.RawText,
        _ => min.ToString() ?? ""
    };

    // The view as the resolver named it; only a trigger the resolver gave no view shows its wire events.
    private static void AddTrigger(List<string> chips, string? view, IReadOnlyList<string> events)
    {
        if (!string.IsNullOrEmpty(view))
        {
            chips.Add("on " + view);
            return;
        }

        foreach (string eventName in events)
        {
            chips.Add("on " + eventName);
        }
    }

    private static void AddScope(List<string> chips, string per)
    {
        if (per.Length > 0)
        {
            chips.Add("per " + per);
        }
    }

    private static void AddReads(List<string> chips, IReadOnlyList<string> reads)
    {
        foreach (string read in reads)
        {
            chips.Add("reads " + read);
        }
    }
}
