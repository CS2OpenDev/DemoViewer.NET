#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.Modules;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas;

/// <summary>
///     The Rule Canvas extension: a node-based view of a ruleset, as one hub tab on the main strip. The
///     pack owns its tab and reaches the host only through the SDK; it hooks nothing inside the Rule
///     Workbench.
/// </summary>
public sealed class RuleCanvasPack : IExtension
{
    /// <summary>The pack id. A persisted key; never renamed.</summary>
    public const string PackId = "net.demoviewer.pack.rulecanvas";

    /// <summary>The umbrella gate id. A persisted override key.</summary>
    public const string PackFeatureId = "pack.rulecanvas";

    /// <summary>The hub tab's id, which its sections name as their host. Stored in the user's session.</summary>
    public const string HubId = "rulecanvas.hub";

    // The id is a persisted override key and must never be renamed; the label and description are display
    // text. Off for consumers by default: the canvas is an authoring surface. The one section has no switch
    // of its own: the host gates the hub on this feature and a hub with its only section off would be an
    // empty strip tab. A second section brings per-section switches with it.
    private static readonly ExtensionFeature[] _features =
    [
        new(
            PackFeatureId, ExtensionFeatureKind.Extension, "Rule Canvas extension",
            "A node-based view of a ruleset: every stat and highlight as a node, value references as wires, "
            + "triggers and gates as chips. Off hides the tab.",
            null, AudienceDefaults.PowerUsers)
    ];

    /// <inheritdoc />
    public string Id => PackId;

    /// <inheritdoc />
    public string FeatureId => PackFeatureId;

    /// <inheritdoc />
    public IEnumerable<ExtensionFeature> Features => _features;

    /// <summary>The one job the canvas queues: evaluating the drawn ruleset over the open demo.</summary>
    public IEnumerable<ExtensionJobKind> JobKinds => [LiveValuesService.JobKind];

    /// <summary>
    ///     The hub: one strip tab whose rail lists every section that names <see cref="HubId" />. Order 6 sits
    ///     after Authoring (5), which is where the ruleset this canvas will draw is written today.
    /// </summary>
    public static HubTabContribution HubTab() => new(HubId, "Rule Canvas", 6, "RULE CANVAS", PackFeatureId);

    /// <inheritdoc />
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The host context is resolved inside the delegates, on use, and as a keyed lookup that answers null
        // rather than throwing: a shell test composes the pack in a container that has no host context, and
        // the canvas still has to draw there, with nothing to select and nowhere to keep an arrangement.
        services.AddSingleton(sp => new CanvasTabViewModel(
            tabId => Context(sp)?.Shell.SelectTab(tabId) ?? false,
            static () => RulesetCorpus.Load(RulesetCorpus.DefaultDirectories()),
            () => Context(sp)?.Stores.Own(StoreRoot.Config)?.Path,
            static action => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask(),
            new LiveValuesHost(
                () => Context(sp)?.Jobs,
                () => Context(sp)?.Shell.CurrentDemoPath,
                () => Context(sp)?.Shell.CurrentDemoHash,
                action =>
                {
                    if (Context(sp) is { } context)
                    {
                        context.Post(action);
                    }
                    else
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(action);
                    }
                },
                handler =>
                {
                    if (Context(sp)?.Shell is { } shell)
                    {
                        shell.CurrentDemoChanged += handler;
                    }
                },
                Context(sp)?.CreateLogger("RuleCanvas.Live"),
                () => Context(sp)?.Shell.CurrentTick ?? 0,
                handler =>
                {
                    if (Context(sp)?.Shell is not { } shell)
                    {
                        return EmptyWatch.Instance;
                    }

                    shell.PlayheadChanged += handler;
                    return new PlayheadWatch(shell, handler);
                },
                tick => Context(sp)?.Shell.SeekToTick(tick)),
            Context(sp)?.CreateLogger("RuleCanvas")));
    }

    private static IExtensionContext? Context(IServiceProvider services) =>
        services.GetKeyedService<IExtensionContext>(typeof(RuleCanvasPack));

    private sealed class PlayheadWatch(IExtensionShell shell, Action handler) : IDisposable
    {
        public void Dispose() => shell.PlayheadChanged -= handler;
    }

    private sealed class EmptyWatch : IDisposable
    {
        public static EmptyWatch Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    /// <inheritdoc />
    public void Contribute(IExtensionContributions contributions, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        ArgumentNullException.ThrowIfNull(services);
        contributions.HubTab(HubTab());
        contributions.Tabs(new CanvasModule(services.GetRequiredService<CanvasTabViewModel>));
    }
}
