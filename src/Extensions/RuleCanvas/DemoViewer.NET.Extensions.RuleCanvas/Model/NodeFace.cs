#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     What one node shows, typed per kind and read straight from the document and the checked model. A
///     face is data: the view model that edits it and the template that draws it are built from it, so a
///     kind's fields are named here once and nowhere else.
/// </summary>
/// <param name="Key">The node's identity: the declaring ruleset (or <c>~kind</c> for an engine node) and the id.</param>
/// <param name="Kind">The stat kind (<c>count</c>, <c>sum</c>, <c>compute</c> and so on), <c>highlight</c>, or an engine kind.</param>
/// <param name="Label">The author's <c>label:</c> or a highlight's <c>title:</c> when it differs from the id, or null.</param>
/// <param name="Per">The <c>per:</c> scope as written, or empty.</param>
/// <param name="Gate">The <c>while:</c> gate as written, or empty.</param>
/// <param name="Diagnostics">What the composition said about this entry; empty when nothing.</param>
/// <param name="SourceLine">The line the entry starts on in its file; zero when unknown.</param>
/// <param name="IsAuthorable">Whether the open document declares the node, so an edit can be addressed to it.</param>
public abstract record NodeFace(
    RulesetDocumentNodeKey Key,
    string Kind,
    string? Label,
    string Per,
    string Gate,
    IReadOnlyList<string> Diagnostics,
    int SourceLine,
    bool IsAuthorable)
{
    public string Id => Key.Id;

    public string Ruleset => Key.Ruleset;

    /// <summary>A node the engine made rather than the author: an event, an enrichment, a context value.</summary>
    public bool IsScaffold => Key.Ruleset.StartsWith('~');

    public bool IsHighlight => Kind == "highlight";
}

/// <summary>
///     The trigger of a stat as the document wrote it. <see cref="On" /> is the <c>on:</c> reference, or null when
///     the kind's own argument names the view. <see cref="Match" /> is the <c>match:</c> bindings, in document
///     order. <see cref="Actor" /> is the <c>actor:</c> text, or null. <see cref="Where" /> is the <c>where:</c>
///     text, or null. <see cref="While" /> is the <c>while:</c> gate, or null.
/// </summary>
public sealed record TriggerFace(TriggerRef? On, IReadOnlyList<MatchBinding> Match, string? Actor, string? Where, string? While)
{
    public static TriggerFace? From(TriggerDef? trigger) =>
        trigger is null ? null : new TriggerFace(trigger.On, trigger.Match ?? [], trigger.Actor, trigger.Where, trigger.While);
}

/// <summary>A declared stat. The fields every kind has; the sealed kinds add their own.</summary>
/// <param name="Key">The node's identity.</param>
/// <param name="Kind">The stat kind.</param>
/// <param name="Label">The author's <c>label:</c> when it differs from the id, or null.</param>
/// <param name="Per">The <c>per:</c> scope as written, or empty.</param>
/// <param name="Gate">The <c>while:</c> gate as written, or empty.</param>
/// <param name="Diagnostics">What the composition said about this entry.</param>
/// <param name="SourceLine">The line the entry starts on; zero when unknown.</param>
/// <param name="IsAuthorable">Whether the open document declares the node.</param>
/// <param name="View">The catalog view the trigger resolved to, as the author named it, or null.</param>
/// <param name="Events">The trigger's concrete wire events, for a trigger the resolver gave no view.</param>
/// <param name="CrossRulesetReads">Reads that resolve outside this ruleset, as written.</param>
/// <param name="KindArg">The raw text of the kind key's value: the view, the formula, the condition, the argument.</param>
/// <param name="Trigger">The trigger, or null for a kind that fires on no event.</param>
/// <param name="Form">The typed <c>match:</c> form over the view's facets.</param>
public abstract record StatFace(
    RulesetDocumentNodeKey Key,
    string Kind,
    string? Label,
    string Per,
    string Gate,
    IReadOnlyList<string> Diagnostics,
    int SourceLine,
    bool IsAuthorable,
    string? View,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> CrossRulesetReads,
    string? KindArg,
    TriggerFace? Trigger,
    MatchFacetForm Form) : NodeFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable);

/// <summary>A <c>count:</c> of a view's events.</summary>
public sealed record CountFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>sum:</c> of an event value. <see cref="Keep" /> is the <c>keep:</c> mode, or null for the default.
/// </summary>
public sealed record SumFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, KeepMode? Keep)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>capture:</c> of a value when the event fires. <see cref="Keep" /> is the <c>keep:</c> mode, or null
///     for the default.
/// </summary>
public sealed record CaptureFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, KeepMode? Keep)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>flag:</c>, true while its condition holds or between its <c>on:</c> and <c>off:</c>. <see
///     cref="OffTrigger" /> is the deactivator from <c>off:</c>, or null.
/// </summary>
public sealed record FlagFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, TriggerFace? OffTrigger)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>compute:</c> formula over sibling stats. <see cref="Format" /> is the <c>format:</c> string, or null
///     for the planner's default. <see cref="Live" /> is whether the formula evaluates on every tick rather than
///     at the scope's end.
/// </summary>
public sealed record ComputeFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, string? Format, bool Live)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>tally:</c> of the scopes in which a sibling reached each threshold. <see cref="Thresholds" /> is the
///     <c>thresholds:</c>, in document order.
/// </summary>
public sealed record TallyFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, IReadOnlyList<TallyThreshold> Thresholds)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>streak:</c> of consecutive events within a window. <see cref="StreakWindow" /> is the <c>window:</c>
///     text, or null for the resolver's default. <see cref="StreakMinStreak" /> is the <c>min_streak:</c>, or
///     null.
/// </summary>
public sealed record StreakFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, string? StreakWindow, int? StreakMinStreak)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>bucket:</c> keyed by an expression, reducing a value per key. <see cref="BucketKey" /> is the scalar
///     <c>key:</c>, or null when it is a list. <see cref="BucketKeys" /> is the <c>key:</c> as a list, or null
///     when it is a scalar. <see cref="BucketValue" /> is the <c>value:</c> expression, or null for a count per
///     key. <see cref="BucketReduce" /> is the <c>reduce:</c> name, or null for the implicit default.
/// </summary>
public sealed record BucketFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, string? BucketKey, IReadOnlyList<string>? BucketKeys,
    string? BucketValue, string? BucketReduce)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A <c>rate:</c> of one sibling bucket over another, per key. <see cref="RateOf" /> is the numerator bucket's
///     id. <see cref="RatePer" /> is the denominator bucket's id.
/// </summary>
public sealed record RateFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form, string? RateOf, string? RatePer)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>A <c>burst:</c> of events in quick succession.</summary>
public sealed record BurstFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads,
    string? KindArg, TriggerFace? Trigger, MatchFacetForm Form)
    : StatFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable, View, Events, CrossRulesetReads, KindArg, Trigger, Form);

/// <summary>
///     A declared highlight: a <c>when:</c> that fires once per scope on its rising edge. <see
///     cref="CrossRulesetReads" /> is reads that resolve outside this ruleset, as written. <see cref="When" /> is
///     the <c>when:</c> expression text. <see cref="Title" /> is the <c>title:</c> template, or null. <see
///     cref="Score" /> is the <c>score:</c>, or null for the resolver's default. <see cref="HighlightKind" /> is
///     the <c>kind:</c> text (<c>highlight</c>, <c>funny</c>, <c>lowlight</c>), or null. <see cref="Group" /> is
///     the <c>group:</c> supersession family, or null.
/// </summary>
public sealed record HighlightFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, IReadOnlyList<string> CrossRulesetReads, string? When, string? Title, int? Score,
    string? HighlightKind, string? Group)
    : NodeFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable);

/// <summary>
///     A node of the checked model whose entry the document does not hold under this id: a <c>for_each:</c>
///     expansion, declared under its template id. Drawn from the checked model alone, with nothing to edit.
///     <see cref="View" /> is the view the trigger resolved to, or null; <see cref="Events" /> the trigger's
///     concrete wire events; <see cref="CrossRulesetReads" /> the reads that resolve outside this ruleset.
/// </summary>
public sealed record UndeclaredFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, string? View, IReadOnlyList<string> Events, IReadOnlyList<string> CrossRulesetReads)
    : NodeFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable);

/// <summary>
///     An engine node of the lowered chain: an event, an enrichment, a context value, an entity pull. Nothing on
///     it is editable. <see cref="ChainKind" /> is what part of the engine it is. <see cref="Name" /> is the
///     engine's name, or the event name. <see cref="IsPerPlayer" /> is whether it materializes once per player.
/// </summary>
public sealed record ScaffoldFace(
    RulesetDocumentNodeKey Key, string Kind, string? Label, string Per, string Gate, IReadOnlyList<string> Diagnostics,
    int SourceLine, bool IsAuthorable, ChainNodeKind ChainKind, string Name, bool IsPerPlayer)
    : NodeFace(Key, Kind, Label, Per, Gate, Diagnostics, SourceLine, IsAuthorable);
