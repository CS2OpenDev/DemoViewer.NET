namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>
///     What one evaluation of the open ruleset over one demo left behind, reduced to what the canvas
///     shows: per declared entry and per instance, how often it fired, when first and last, and what it
///     held at the end. Holds no engine object, so it can cross to the UI thread whole.
/// </summary>
/// <param name="DemoPath">The demo evaluated.</param>
/// <param name="ProfileId">The demo source profile the build resolved.</param>
/// <param name="TickRate">The demo's tick rate.</param>
/// <param name="FirstTick">The first message tick the evaluation saw, server clock.</param>
/// <param name="LastTick">The last message tick, server clock.</param>
/// <param name="Instances">The game instance first, then one per materialised player in slot order.</param>
/// <param name="Nodes">One track per declared entry key and instance that has an engine node.</param>
/// <param name="Unbound">Declared entry keys the run has no engine node for.</param>
/// <param name="BuildTime">Time to lower the rulesets for this demo.</param>
/// <param name="EvaluateTime">Time to replay the demo through the chain with snapshots on.</param>
/// <param name="ReduceTime">Time to reduce the run to this record.</param>
/// <param name="PeakBytes">Working set growth across build, evaluate and reduce.</param>
public sealed record LiveValuesRun(
    string DemoPath,
    string ProfileId,
    int TickRate,
    int FirstTick,
    int LastTick,
    IReadOnlyList<LiveInstance> Instances,
    IReadOnlyDictionary<(RulesetDocumentNodeKey Key, int Instance), NodeTrack> Nodes,
    IReadOnlyList<RulesetDocumentNodeKey> Unbound,
    TimeSpan BuildTime,
    TimeSpan EvaluateTime,
    TimeSpan ReduceTime,
    long PeakBytes);

/// <summary>One copy of the chain: the game, or one player's.</summary>
/// <param name="Index">Position in <see cref="LiveValuesRun.Instances" />; the game is 0.</param>
/// <param name="Slot">The player's slot, or null for the game.</param>
/// <param name="Name">The player's name as the demo recorded it, or null for the game.</param>
public sealed record LiveInstance(int Index, int? Slot, string? Name);

/// <summary>One entry's record in one instance.</summary>
/// <param name="FireCount">How many times an edge wrote the node, summed over its incoming edges.</param>
/// <param name="FirstTick">The server tick of the first applied fire, or null when it never fired.</param>
/// <param name="LastTick">The server tick of the last applied fire, or null when it never fired.</param>
/// <param name="FinalDisplay">The node's display value at the end of the demo.</param>
/// <param name="FinalNumeric">The node's numeric value at the end of the demo, when it has one.</param>
/// <param name="FinalActive">Whether the node was active at the end of the demo.</param>
public sealed record NodeTrack(int FireCount, int? FirstTick, int? LastTick, string? FinalDisplay, float? FinalNumeric, bool FinalActive);
