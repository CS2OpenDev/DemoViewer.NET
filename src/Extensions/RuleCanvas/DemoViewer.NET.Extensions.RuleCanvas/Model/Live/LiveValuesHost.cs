#region

using Microsoft.Extensions.Logging;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

/// <summary>
///     What the canvas needs from the host to show live values, as delegates: the pack resolves its context
///     lazily and the view model never holds it. Each accessor answers null where the host has nothing.
/// </summary>
/// <param name="Jobs">The processing queue, or null.</param>
/// <param name="DemoPath">The open demo's path, or null when none is open.</param>
/// <param name="DemoHash">The open demo's content key, or null.</param>
/// <param name="Post">Runs an action on the UI thread.</param>
/// <param name="OnDemoChanged">Subscribes a handler to the shell's demo change, when the host has a shell.</param>
/// <param name="Log">Where a failed run is written; null logs nothing.</param>
public sealed record LiveValuesHost(
    Func<IExtensionJobs?> Jobs,
    Func<string?> DemoPath,
    Func<string?> DemoHash,
    Action<Action> Post,
    Action<Action>? OnDemoChanged = null,
    ILogger? Log = null);
