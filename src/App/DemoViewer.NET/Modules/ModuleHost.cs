#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Concrete <see cref="IModuleHost" /> handed to each module's <c>CreateTabs</c>. Wraps the
///     runtime <see cref="IModuleContext" />, the granted capability set, and a log sink the shell
///     wires to the Output panel. First-party modules are granted all capabilities; third-party
///     defaults (read-only + Playback.Observe + UI.Contribute) are applied by the composition root
///     when runtime loading lands.
/// </summary>
public sealed class ModuleHost(
    IModuleContext context,
    IEnumerable<string> capabilities,
    Action<ModuleLogLevel, string> log) : IModuleHost
{
    private readonly HashSet<string> _capabilities = new(capabilities, StringComparer.Ordinal);

    /// <summary>The capability set granted to first-party modules (all of them).</summary>
    public static IReadOnlyList<string> FirstPartyCapabilities { get; } =
    [
        "Demo.Read", "Entities.Read", "Analysis.Read", "Playback.Observe", "Playback.Control", "UI.Contribute"
    ];

    public IModuleContext Context { get; } = context;

    public bool HasCapability(string capability) => _capabilities.Contains(capability);

    public void Log(ModuleLogLevel level, string message) => log(level, message);
}
