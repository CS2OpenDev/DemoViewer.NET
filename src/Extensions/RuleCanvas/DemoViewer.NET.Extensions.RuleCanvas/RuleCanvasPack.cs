#region

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

    // Every id is a persisted override key and must never be renamed; labels and descriptions are display
    // text. Off for consumers by default: the canvas is an authoring surface.
    private static readonly ExtensionFeature[] _features =
    [
        new(
            PackFeatureId, ExtensionFeatureKind.Extension, "Rule Canvas extension",
            "A node-based view of a ruleset: every stat and highlight as a node, value references as wires, "
            + "triggers and gates as chips. Off hides the tab.",
            null, AudienceDefaults.PowerUsers),
        new(
            CanvasModule.TabFeatureId, ExtensionFeatureKind.Tab, "Canvas",
            "The canvas section of the Rule Canvas tab.",
            PackFeatureId, AudienceDefaults.PowerUsers)
    ];

    /// <inheritdoc />
    public string Id => PackId;

    /// <inheritdoc />
    public string FeatureId => PackFeatureId;

    /// <inheritdoc />
    public IEnumerable<ExtensionFeature> Features => _features;

    /// <summary>
    ///     The hub: one strip tab whose rail lists every section that names <see cref="HubId" />. Order 6 sits
    ///     after Authoring (5), which is where the ruleset this canvas will draw is written today.
    /// </summary>
    public static HubTabContribution HubTab() => new(HubId, "Rule Canvas", 6, "RULE CANVAS", PackFeatureId);

    /// <inheritdoc />
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // The shell lookup stays inside the delegate: the context is resolved on the click, not at
        // construction, so the view model builds in a container that has no host context.
        services.AddSingleton(sp => new CanvasTabViewModel(
            tabId => sp.GetExtensionContext<RuleCanvasPack>().Shell.SelectTab(tabId)));
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
