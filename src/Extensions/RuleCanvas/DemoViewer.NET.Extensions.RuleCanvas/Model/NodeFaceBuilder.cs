#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     The faces of a composition: one per declared node, read from the document the checked model does not
///     carry the text of, then one per engine node of the chain that no declared node already is. Pure:
///     documents and the checked model in, faces out, in the order the canvas draws them.
/// </summary>
public static class NodeFaceBuilder
{
    public static IReadOnlyList<NodeFace> Build(RulesetCorpus.Composed composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ILookup<RulesetDocumentNodeKey, string> diagnostics = DiagnosticsByNode(composed);
        List<NodeFace> faces = new(composed.Graph.Nodes.Count + composed.Chain.Nodes.Count);
        HashSet<RulesetDocumentNodeKey> keys = [];
        string? open = composed.Graph.Nodes.FirstOrDefault(n => n.IsAuthorable)?.Key.Ruleset;
        FaceVocabulary vocabulary = FaceVocabulary.Of(composed, open);
        foreach (RulesetDocumentNode node in composed.Graph.Nodes)
        {
            faces.Add(Declared(node, composed.Documents, [.. diagnostics[node.Key]]) with
            {
                Vocabulary = vocabulary,
                ValueType = node.ValueType.Kind == RulesTypeKind.None ? null : node.ValueType.ToString()
            });
            keys.Add(node.Key);
        }

        // The engine's own nodes come after the declared ones: the events, the enrichments and the context
        // values the chain runs through. A declared node the chain also names is the same node.
        RulesetDoc? openDoc = composed.Open;
        ShowSurface surface = ShowSurface.Of(openDoc);
        foreach (ChainNode node in composed.Chain.Nodes)
        {
            if (node.Kind == ChainNodeKind.Declared || !keys.Add(node.Key))
            {
                continue;
            }

            switch (node.Kind)
            {
                case ChainNodeKind.Show when openDoc is not null:
                    faces.Add(Show(node.Key, surface, [.. diagnostics[node.Key]]) with { Vocabulary = vocabulary });
                    break;
                case ChainNodeKind.Params when openDoc is not null:
                    faces.Add(new ParamsFace(node.Key, ParamsFace.KindName, null, "", "", [.. diagnostics[node.Key]],
                        openDoc.Params.Count > 0 ? openDoc.Params[0].Position.Line : 0, true, openDoc.Params) { Vocabulary = vocabulary });
                    break;
                case ChainNodeKind.Dependency:
                    faces.Add(Dependency(node, composed));
                    break;
                default:
                    faces.Add(new ScaffoldFace(node.Key, CanvasEditSemantics.ScaffoldKind(node.Kind), null, "", "", [], 0, false,
                        node.Kind, node.Name, node.IsPerPlayer) { Events = node.Events, IsPerTeam = node.IsPerTeam, IsRoundFact = node.IsRoundFact });
                    break;
            }
        }

        return faces;
    }

    /// <summary>
    ///     The face of one folded dependency: its title, its entry count, the entries read, the two files when
    ///     the user's copy overrides the shipped one, and every diagnostic raised inside that ruleset.
    /// </summary>
    public static DependencyFace Dependency(ChainNode node, RulesetCorpus.Composed composed)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(composed);
        string id = node.Key.Id;
        composed.Documents.TryGetValue(id, out RulesetDoc? doc);
        string title = string.IsNullOrWhiteSpace(doc?.Title) ? id : doc!.Title;
        (string UserPath, string ShippedPath)? files = composed.Overrides.TryGetValue(id, out (string UserPath, string ShippedPath) found) ? found : null;
        List<string> diagnostics = [.. composed.PositionedDiagnostics.Where(d => d.RulesetId == id).Select(d => d.Message).Distinct(StringComparer.Ordinal)];
        return new DependencyFace(node.Key, DependencyFace.KindName, string.Equals(title, id, StringComparison.Ordinal) ? null : title, "", "",
            diagnostics, 0, false, title, node.EntryCount, node.Outputs, files?.UserPath, files?.ShippedPath);
    }

    /// <summary>The face of one show node: a table's columns and grain, or the scoreboard's entries.</summary>
    public static ShowFace Show(RulesetDocumentNodeKey key, ShowSurface surface, IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (key.IsScoreboard)
        {
            return new ShowFace(key, ShowFace.KindName, null, "", "", diagnostics, surface.ScoreboardLine, true,
                RulesetDocumentNodeKey.ScoreboardName, null, surface.Scoreboard);
        }

        ShowTable? table = surface.Table(key.Name);
        return new ShowFace(key, ShowFace.KindName, null, table?.Per ?? "", "", diagnostics, table?.SourceLine ?? 0, true,
            key.Name, table?.On, table?.Columns ?? []);
    }

    /// <summary>The face of one declared node, from its document entry when the document holds one under its id.</summary>
    public static NodeFace Declared(RulesetDocumentNode node, IReadOnlyDictionary<string, RulesetDoc> documents,
        IReadOnlyList<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(documents);
        documents.TryGetValue(node.Key.Ruleset, out RulesetDoc? doc);
        string kind = node.Kind.ToString().ToLowerInvariant();
        string? label = string.Equals(node.Label, node.Id, StringComparison.Ordinal) ? null : node.Label;
        int line = node.Position.Line;

        if (node.IsDefine)
        {
            (DefineDef Define, TriggerDef Trigger)? declared = doc is null ? null : RulesetDocumentGraph.TriggerDefine(doc, node.Key.Name);
            return declared is { } found
                ? Define(node.Key, found.Define, found.Trigger, node.View, node.Events, diagnostics, node.IsAuthorable)
                : new UndeclaredFace(node.Key, DefineFace.KindName, null, "", "", diagnostics, line, node.IsAuthorable, node.View,
                    node.Events, []);
        }

        if (node.IsHighlight)
        {
            HighlightDef? highlight = doc?.Highlights.FirstOrDefault(h => h.Id == node.Id);
            return highlight is null
                ? new UndeclaredFace(node.Key, kind, label, "", "", diagnostics, line, node.IsAuthorable, node.View, node.Events,
                    node.CrossRulesetReads)
                : new HighlightFace(node.Key, kind, label, PerText(highlight.Per), "", diagnostics, line, node.IsAuthorable,
                    node.CrossRulesetReads, highlight.When, highlight.Title, highlight.Score, highlight.Kind, highlight.Group);
        }

        StatDef? stat = doc?.Stats.FirstOrDefault(s => s.Id == node.Id);
        if (stat is null)
        {
            return new UndeclaredFace(node.Key, kind, label, "", "", diagnostics, line, node.IsAuthorable, node.View, node.Events,
                node.CrossRulesetReads);
        }

        string per = PerText(stat.Per);
        string gate = string.IsNullOrWhiteSpace(stat.Trigger?.While) ? "" : stat.Trigger.While;
        TriggerFace? trigger = TriggerFace.From(stat.Trigger);
        // The typed form: every facet the view offers, the bound ones as rows, the rest offered by the
        // picker; a key the view does not know is kept as a row the resolver already flagged.
        MatchFacetForm form = MatchFacetForm.For(EmbeddedCatalog.Root, node.View, stat.Trigger?.Match);
        StatFace face = kind switch
        {
            "sum" => new SumFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View, node.Events,
                node.CrossRulesetReads, stat.KindArg, trigger, form, stat.Keep),
            "capture" => new CaptureFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View,
                node.Events, node.CrossRulesetReads, stat.KindArg, trigger, form, stat.Keep),
            "flag" => new FlagFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View, node.Events,
                node.CrossRulesetReads, stat.KindArg, trigger, form, TriggerFace.From(stat.OffTrigger)),
            "compute" => new ComputeFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View,
                node.Events, node.CrossRulesetReads, stat.KindArg, trigger, form, stat.Format, stat.Live),
            "tally" => new TallyFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View, node.Events,
                node.CrossRulesetReads, stat.KindArg, trigger, form, stat.Thresholds ?? []),
            "streak" => new StreakFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View,
                node.Events, node.CrossRulesetReads, stat.KindArg, trigger, form, stat.StreakWindow, stat.StreakMinStreak),
            "bucket" => new BucketFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View,
                node.Events, node.CrossRulesetReads, stat.KindArg, trigger, form, stat.BucketKey, stat.BucketKeys, stat.BucketValue,
                stat.BucketReduce),
            "rate" => new RateFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View, node.Events,
                node.CrossRulesetReads, stat.KindArg, trigger, form, stat.RateOf, stat.RatePer),
            "burst" => new BurstFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View, node.Events,
                node.CrossRulesetReads, stat.KindArg, trigger, form, stat.StreakWindow, stat.StreakMinStreak),
            _ => new CountFace(node.Key, kind, label, per, gate, diagnostics, line, node.IsAuthorable, node.View, node.Events,
                node.CrossRulesetReads, stat.KindArg, trigger, form)
        };

        // The resolver splices a define's body into the trigger; the face names the define it fires through.
        (DefineDef Define, TriggerDef Trigger)? define = face.TriggerSpelling is { } spelling ? RulesetDocumentGraph.TriggerDefine(doc!, spelling) : null;
        return define is not { } named
            ? face
            : face with { Define = Define(RulesetDocumentNodeKey.Define(doc!.Id, named.Define.Name), named.Define, named.Trigger, node.View, node.Events, [], node.IsAuthorable) };
    }

    /// <summary>The face of one trigger-bodied define, from its body; the view and events come from the graph node.</summary>
    public static DefineFace Define(RulesetDocumentNodeKey key, DefineDef define, TriggerDef trigger, string? view,
        IReadOnlyList<string> events, IReadOnlyList<string> diagnostics, bool authorable)
    {
        ArgumentNullException.ThrowIfNull(define);
        ArgumentNullException.ThrowIfNull(trigger);
        string gate = string.IsNullOrWhiteSpace(trigger.While) ? "" : trigger.While;
        return new DefineFace(key, DefineFace.KindName, null, "", gate, diagnostics, define.Position.Line, authorable, define.Name,
            TriggerFace.From(trigger)!, view, events, MatchFacetForm.For(EmbeddedCatalog.Root, view, trigger.Match));
    }

    private static string PerText(PerScope per) => per == PerScope.None ? "" : per.ToString().ToLowerInvariant();

    /// <summary>
    ///     Files each composition diagnostic under the node whose entry contains its line. An entry runs
    ///     from its start line to the next thing the document declares: the next stat, highlight or trigger
    ///     define, or a list define, a param, a cadence or the <c>show:</c> block, which closes the section. A diagnostic
    ///     outside every entry, before the first or inside another block, stays at the document level, as
    ///     does one inside an entry the graph does not draw.
    /// </summary>
    public static ILookup<RulesetDocumentNodeKey, string> DiagnosticsByNode(RulesetCorpus.Composed composed)
    {
        ArgumentNullException.ThrowIfNull(composed);
        HashSet<RulesetDocumentNodeKey> drawn = [.. composed.Graph.Nodes.Select(n => n.Key)];
        Dictionary<string, List<(int Line, RulesetDocumentNodeKey? Opens)>> marks = new(StringComparer.Ordinal);
        List<(RulesetDocumentNodeKey Key, string Message)> filed = [];
        foreach ((string rulesetId, int line, string message) in composed.PositionedDiagnostics)
        {
            if (!marks.TryGetValue(rulesetId, out List<(int Line, RulesetDocumentNodeKey? Opens)>? lines))
            {
                lines = composed.Documents.TryGetValue(rulesetId, out RulesetDoc? doc) ? Marks(doc) : [];
                marks[rulesetId] = lines;
            }

            // The last mark at or before the line says what the line is inside: an entry, or something else.
            int at = lines.FindLastIndex(m => m.Line <= line);
            if (at >= 0 && lines[at].Opens is { } key && drawn.Contains(key))
            {
                filed.Add((key, message));
            }
        }

        return filed.ToLookup(f => f.Key, f => f.Message);
    }

    // Every line the document opens something on, in file order: an entry with its key, anything else with none.
    private static List<(int Line, RulesetDocumentNodeKey? Opens)> Marks(RulesetDoc doc)
    {
        List<(int Line, RulesetDocumentNodeKey? Opens)> marks = [];
        foreach (StatDef stat in doc.Stats)
        {
            marks.Add((stat.Position.Line, new RulesetDocumentNodeKey(doc.Id, stat.Id)));
        }

        foreach (HighlightDef highlight in doc.Highlights)
        {
            marks.Add((highlight.Position.Line, new RulesetDocumentNodeKey(doc.Id, highlight.Id)));
        }

        // A trigger define is an entry of its own when it is drawn; DiagnosticsByNode drops the key otherwise.
        foreach (DefineDef define in doc.Defines)
        {
            marks.Add((define.Position.Line, define.Body is TriggerDefineBody ? RulesetDocumentNodeKey.Define(doc.Id, define.Name) : null));
        }

        foreach (ParamDef param in doc.Params)
        {
            marks.Add((param.Position.Line, null));
        }

        foreach (CadenceDef cadence in doc.Cadences)
        {
            marks.Add((cadence.Position.Line, null));
        }

        if (doc.Show is { } show)
        {
            marks.Add((show.Position.Line, null));
        }

        marks.RemoveAll(m => m.Line <= 0);
        marks.Sort((a, b) => a.Line.CompareTo(b.Line));
        return marks;
    }
}
