#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     What a port carries. Inputs: a trigger (<c>on:</c>), a gate (<c>while:</c>), a read (a value the
///     body names) or a logic input (a <c>when:</c> term). Outputs: an event or define fires, a context flag
///     holds, a stat or enrichment is a value.
/// </summary>
public enum PortKind
{
    Trigger,
    Gate,
    Read,
    Logic,
    Fire,
    Flag,
    Value
}

/// <summary>
///     An endpoint on a node face. The connector control writes <see cref="Anchor" /> in graph coordinates
///     as the node moves; a connection binds to the anchors of its two endpoints, never to copies of them.
/// </summary>
public sealed partial class CanvasConnectorViewModel : ObservableObject
{
    /// <summary>The label beside the endpoint: the input word, or the value type of an output.</summary>
    public required string Title { get; init; }

    /// <summary>The node the endpoint sits on, so a wire drawn between two endpoints names two nodes.</summary>
    public CanvasNodeViewModel? Owner { get; init; }

    public PortKind Kind { get; init; } = PortKind.Value;

    public bool IsInput => Kind is PortKind.Trigger or PortKind.Gate or PortKind.Read or PortKind.Logic;

    /// <summary>The theme token the ring is drawn in: the wire kind the port takes or gives.</summary>
    public string Token => Kind switch
    {
        PortKind.Trigger or PortKind.Fire => "RuleWireWrite",
        PortKind.Gate or PortKind.Flag => "RuleWireGate",
        PortKind.Logic => "RuleWireAnd",
        _ => "RuleWireRead"
    };

    /// <summary>False while a wire is held from a source this port cannot take; the ring dims.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private bool _isCompatible = true;

    public double Opacity => IsCompatible ? 1 : 0.25;

    /// <summary>The input word for a kind, as the face spells the key it stands for.</summary>
    public static string TitleOf(PortKind kind) => kind switch
    {
        PortKind.Trigger => "on",
        PortKind.Gate => "while",
        PortKind.Read => "reads",
        PortKind.Logic => "when",
        PortKind.Fire => "fires",
        PortKind.Flag => "holds",
        _ => ""
    };

    /// <summary>
    ///     Whether an input of <paramref name="input" /> takes a wire from <paramref name="source" />: a
    ///     trigger takes what fires, a gate takes what holds or an authored bool stat (a sibling flag), a read
    ///     or a logic input takes a value or a flag.
    /// </summary>
    public static bool Accepts(PortKind input, CanvasConnectorViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return input switch
        {
            PortKind.Trigger => source.Kind == PortKind.Fire,
            PortKind.Gate => source.Kind == PortKind.Flag || (source.Kind == PortKind.Value && source.Owner is { ValueType: "bool", IsScaffold: false }),
            PortKind.Read or PortKind.Logic => source.Kind is PortKind.Value or PortKind.Flag,
            _ => false
        };
    }

    /// <summary>Written by the control as the node moves.</summary>
    [ObservableProperty]
    private Point _anchor;

    /// <summary>Must stay true for the anchor to track the node; every sample connector has a wire.</summary>
    [ObservableProperty]
    private bool _isConnected = true;
}

/// <summary>A wire from one node's output to another's input.</summary>
/// <param name="source">The output.</param>
/// <param name="target">The input.</param>
/// <param name="text">What the wire shows: the event that fires it, what it does, or the logic test.</param>
/// <param name="condition">The lowered condition, shown on hover, or null.</param>
/// <param name="isRead">A value read rather than a write; drawn dashed.</param>
/// <param name="isGate">A gate holding the target open while it is active; drawn as a long dash.</param>
/// <param name="isAnd">A logic input the target conjoins; drawn in the and colour.</param>
/// <param name="isOr">A logic input the target disjoins; drawn in the or colour.</param>
public sealed partial class CanvasConnectionViewModel(CanvasConnectorViewModel source, CanvasConnectorViewModel target,
    string text = "", string? condition = null, bool isRead = false, bool isGate = false, bool isAnd = false, bool isOr = false)
    : ObservableObject
{
    /// <summary>The hint the hover panel opens with on a wire whose condition can be edited in place.</summary>
    public const string EditHint = "double-click to edit";

    private Model.WireEdit? _conditionEdit;

    /// <summary>
    ///     The last live run's line for the hover panel, from the target node's track for now: how often it
    ///     fired and when last. Empty when there is no run. Set by the tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLiveTip))]
    private string _liveTip = "";

    public bool HasLiveTip => LiveTip.Length > 0;

    /// <summary>The menu's seek items, filled by the tab when the menu opens.</summary>
    public CanvasSeekMenuViewModel SeekMenu { get; } = new();

    /// <summary>What the wire carries, as the live run keys its track.</summary>
    public Model.Live.LiveWireKind LiveKind => IsGate ? Model.Live.LiveWireKind.Gate
        : IsRead ? Model.Live.LiveWireKind.Read
        : IsAnd || IsOr ? Model.Live.LiveWireKind.Logic
        : Model.Live.LiveWireKind.Trigger;
    /// <summary>
    ///     What the wire's condition editor would edit, as the semantics decided from the wire alone; null
    ///     until the builder sets it. Whether it may write follows the target node's editability.
    /// </summary>
    public Model.WireEdit? ConditionEdit
    {
        get => _conditionEdit;
        set
        {
            if (ReferenceEquals(_conditionEdit, value))
            {
                return;
            }

            if (_conditionEdit?.Target is { } was)
            {
                was.PropertyChanged -= OnTargetChanged;
            }

            _conditionEdit = value;
            if (value?.Target is { } target)
            {
                target.PropertyChanged += OnTargetChanged;
            }

            NotifyConditionEdit();
        }
    }

    /// <summary>The wire's condition can be edited in place: the semantics allow it and the target writes.</summary>
    public bool CanEditCondition => ConditionEdit is { IsEnabled: true, Target.IsEditable: true };

    /// <summary>
    ///     The first line of the hover panel and the Edit condition item's tooltip: the hint while editable,
    ///     the semantics' reason when the wire has no condition, read-only when the target does not write.
    /// </summary>
    public string ConditionEditTip => ConditionEdit switch
    {
        null => "",
        { IsEnabled: false, Reason: { } reason } => reason,
        { Target.IsEditable: true } => EditHint,
        _ => "a shipped ruleset is read only here"
    };

    public bool HasConditionEditTip => ConditionEditTip.Length > 0;

    private void OnTargetChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CanvasNodeViewModel.IsEditable))
        {
            NotifyConditionEdit();
        }
    }

    private void NotifyConditionEdit()
    {
        OnPropertyChanged(nameof(ConditionEdit));
        OnPropertyChanged(nameof(CanEditCondition));
        OnPropertyChanged(nameof(ConditionEditTip));
        OnPropertyChanged(nameof(HasConditionEditTip));
    }

    /// <summary>A gate holding the target open while it is active; drawn dashed, labelled while.</summary>
    public bool IsGate { get; } = isGate;

    /// <summary>A logic input the target conjoins; the wire template sets the and class from it.</summary>
    public bool IsAnd { get; } = isAnd;

    /// <summary>A logic input the target disjoins; the wire template sets the or class from it.</summary>
    public bool IsOr { get; } = isOr;

    public CanvasConnectorViewModel Source { get; } = source;

    public CanvasConnectorViewModel Target { get; } = target;

    public string Text { get; } = text;

    public string? Condition { get; } = condition;

    public bool IsRead { get; } = isRead;

    public bool HasText => Text.Length > 0;

    /// <summary>
    ///     The hover panel's second line: the engine's lowering of the wire (<c>engine: value &gt; 2</c>) when
    ///     it differs from what the label says; empty when there is no condition or the label already spells it.
    /// </summary>
    public string EngineLine => Condition is { } lowered && Squash(Operand(Text)) != Squash(lowered) ? "engine: " + lowered : "";

    public bool HasEngineLine => EngineLine.Length > 0;

    // The label's operand without its connective: "and round_kills > 2" and "while match.live" both name their test.
    private static string Operand(string label) =>
        label.StartsWith("and ", StringComparison.Ordinal) ? label[4..]
        : label.StartsWith("or ", StringComparison.Ordinal) ? label[3..]
        : label.StartsWith("while ", StringComparison.Ordinal) ? label[6..]
        : label;

    private static string Squash(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
