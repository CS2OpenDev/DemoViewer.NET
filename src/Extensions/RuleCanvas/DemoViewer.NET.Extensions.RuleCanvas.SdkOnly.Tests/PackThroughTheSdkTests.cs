#region

using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using DemoViewer.NET.Extensions.RuleCanvas;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;

#endregion

namespace SdkOnlyHost;

/// <summary>
///     The Rule Canvas pack loaded and composed through the published surface alone: the manifest a host
///     reads, the switches it lists in Settings, and what it hands back from <see cref="IExtension.Contribute" />.
///     The ids are literals, not the pack's constants: a literal is what stored settings and the session
///     file carry, so renaming one is a break and this is where it surfaces.
/// </summary>
[NotInParallel(nameof(PackThroughTheSdkTests))]
public class PackThroughTheSdkTests
{
    private const string PackId = "net.demoviewer.pack.rulecanvas";
    private const string MasterSwitch = "pack.rulecanvas";
    private const string HubId = "rulecanvas.hub";
    private const string SectionId = "rulecanvas.canvas";
    private const string SectionFeature = "tab.rulecanvas";

    /// <summary>
    ///     The loader's own route in: the manifest beside the DLL names an entry type a host can construct. The
    ///     copy carries the id-qualified name because the heads ship more than one extension into one directory.
    /// </summary>
    [Test]
    public async Task Manifest_BesideThePack_NamesAnEntryTypeAHostCanConstruct()
    {
        string manifestPath = Path.Combine(AppContext.BaseDirectory, PackId + ".extension.json");
        await Assert.That(File.Exists(manifestPath)).IsTrue()
            .Because("the SDK's targets copy the stamped manifest into every referencing project's output");
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        JsonElement root = manifest.RootElement;
        string entryType = root.GetProperty("entryType").GetString()!;
        Type? type = typeof(RuleCanvasPack).Assembly.GetType(entryType, throwOnError: false);
        using (Assert.Multiple())
        {
            await Assert.That(root.GetProperty("id").GetString()).IsEqualTo(PackId);
            await Assert.That(root.GetProperty("name").GetString()).IsEqualTo("Rule Canvas");
            await Assert.That(root.GetProperty("assembly").GetString()).IsEqualTo("DemoViewer.NET.Extensions.RuleCanvas.dll");
            await Assert.That(root.GetProperty("requiresHost").GetString()).IsEqualTo("^2.1");
            await Assert.That(root.GetProperty("version").GetString()).IsNotEqualTo("{version}")
                .Because("an unstamped manifest fails to parse host-side on purpose");
            await Assert.That(type).IsNotNull();
            await Assert.That(type!.GetConstructor(Type.EmptyTypes)).IsNotNull();
            await Assert.That(Activator.CreateInstance(type)).IsTypeOf<RuleCanvasPack>();
        }

        IExtension pack = (IExtension)Activator.CreateInstance(type)!;
        await Assert.That(pack.Id).IsEqualTo(root.GetProperty("id").GetString());
    }

    /// <summary>The switches Settings lists: one master, off for consumers, and one section under it.</summary>
    [Test]
    public async Task Features_AreOneMasterSwitch_AndOneSectionUnderIt()
    {
        RuleCanvasPack pack = new();
        List<ExtensionFeature> features = [.. pack.Features];
        ExtensionFeature master = features.Single(f => f.Kind == ExtensionFeatureKind.Extension);
        ExtensionFeature section = features.Single(f => f.Kind == ExtensionFeatureKind.Tab);
        using (Assert.Multiple())
        {
            await Assert.That(pack.FeatureId).IsEqualTo(MasterSwitch);
            await Assert.That(master.Id).IsEqualTo(MasterSwitch);
            await Assert.That(master.ParentId).IsNull();
            await Assert.That(master.Defaults).IsEqualTo(AudienceDefaults.PowerUsers)
                .Because("an authoring surface stays out of a consumer's strip until they ask for it");
            await Assert.That(section.Id).IsEqualTo(SectionFeature);
            await Assert.That(section.ParentId).IsEqualTo(MasterSwitch);
            await Assert.That(features).Count().IsEqualTo(2);
        }
    }

    /// <summary>
    ///     What <see cref="IExtension.Contribute" /> hands a host: the hub declaration with its strip header,
    ///     rail label and order, and one module whose only tab is a section of that hub.
    /// </summary>
    [Test]
    public async Task Contribute_DeclaresTheHub_AndOneSectionOnIt()
    {
        RuleCanvasPack pack = new();
        ServiceCollection services = new();
        pack.Register(services);
        await using ServiceProvider provider = services.BuildServiceProvider();
        RecordedContributions recorded = new(new SdkOnlyContext(PackId, MasterSwitch));
        pack.Contribute(recorded, provider);

        HubTabContribution hub = recorded.HubTabs.Single();
        IWorkspaceModule module = recorded.Modules.Single();
        // The module reads nothing off the host; a null stands in for the module context it would carry.
        WorkspaceTabDescriptor section = module.CreateTabs(null!).Single();
        using (Assert.Multiple())
        {
            await Assert.That(hub.Id).IsEqualTo(HubId);
            await Assert.That(hub.Header).IsEqualTo("Rule Canvas");
            await Assert.That(hub.RailLabel).IsEqualTo("RULE CANVAS");
            await Assert.That(hub.Order).IsEqualTo(6).Because("after Authoring, which sits at 5");
            await Assert.That(hub.FeatureId).IsEqualTo(MasterSwitch);
            await Assert.That(section.TabId).IsEqualTo(SectionId);
            await Assert.That(section.Header).IsEqualTo("Canvas");
            await Assert.That(section.HostId).IsEqualTo(HubId);
            await Assert.That(section.FeatureId).IsEqualTo(SectionFeature);
            await Assert.That(section.ViewModelFactory).IsNotNull();
            await Assert.That(section.ViewFactory).IsNotNull();
            await Assert.That(recorded.Rulesets).IsEmpty();
            await Assert.That(recorded.StatusChips).IsEmpty();
        }
    }

    /// <summary>
    ///     The host's naming rule for a marked view model, reproduced: the view model's full type name with
    ///     every <c>ViewModel</c> replaced by <c>View</c>, in the same assembly, public and constructible.
    /// </summary>
    [Test]
    public async Task NamingRule_FindsAConstructibleView_ForEveryMarkedViewModel()
    {
        Assembly pack = typeof(RuleCanvasPack).Assembly;
        List<Type> marked = [.. pack.GetTypes().Where(t => !t.IsAbstract && typeof(IExtensionViewModel).IsAssignableFrom(t))];
        await Assert.That(marked).IsNotEmpty();
        foreach (Type viewModel in marked)
        {
            string name = viewModel.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
            Type? view = pack.GetType(name, throwOnError: false);
            using (Assert.Multiple())
            {
                await Assert.That(view).IsNotNull().Because($"{viewModel.FullName} needs a view named {name}");
                await Assert.That(typeof(Control).IsAssignableFrom(view)).IsTrue();
                await Assert.That(view!.GetConstructor(Type.EmptyTypes)).IsNotNull();
            }
        }
    }
}
