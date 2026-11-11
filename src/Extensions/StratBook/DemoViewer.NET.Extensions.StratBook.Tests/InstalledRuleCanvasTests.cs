#region

using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The Rule Canvas as an installed first-party extension: packed flat the way pack-extension.sh stages it,
///     signed with an ephemeral key, installed through the updater on a build that does not ship it, and drawn
///     on the shell. It lives here because this is the headless project with neither the canvas nor Nodify
///     beside the test host, so both can only come from the install folder.
/// </summary>
[NotInParallel]
[Category("Render")]
public class InstalledRuleCanvasTests
{
    private const string Id = "net.demoviewer.pack.rulecanvas";
    private const string Assembly = "DemoViewer.NET.Extensions.RuleCanvas";
    private const string HubId = "rulecanvas.hub";

    [Test]
    public async Task Installed_LoadsNodifyFromItsFolder_AndDrawsPlayerRound()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvcanvas-" + Guid.NewGuid().ToString("N"));
        string userRules = Path.Combine(root, "user-rules");
        Directory.CreateDirectory(userRules);
        string? previousRules = Environment.GetEnvironmentVariable("CS2DEMOKIT_USER_RULES_DIR");
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        try
        {
            Environment.SetEnvironmentVariable("CS2DEMOKIT_USER_RULES_DIR", userRules);
            string tree = Stage(Path.Combine(root, "pack"), key);
            string manifestJson = await File.ReadAllTextAsync(Path.Combine(tree, ExtensionManifest.FileName));
            string version = ExtensionManifest.Parse(manifestJson).Version.ToString();
            byte[] zip = ZipDirectory(tree);
            FakeFeeds.Entry entry = new(version, zip);
            string feed = FeedJson(entry, manifestJson);
            FakeFeedClient client = new() { Zips = { [entry.Url] = zip }, Feeds = { [FeedUrl(Id)] = feed } };
            ExtensionUpdateService service = new(root, [], ExtensionHost.Current, TrustPolicy.Nothing, client,
                FeedUrl, installTrust: new SignedTrustPolicy([publicKey]));

            StageResult staged = await service.DownloadAndStageAsync(ExtensionFeed.Parse(feed).Entries.Single());
            ExtensionStartupResult result = ExtensionStartup.Resolve(root, [], ExtensionHost.Current,
                TrustPolicy.Nothing, [publicKey], allowUnverified: false, safeMode: false);

            PackStatus status = result.Statuses.Single();
            IExtension pack = status.Pack;
            AssemblyLoadContext? context = AssemblyLoadContext.GetLoadContext(pack.GetType().Assembly);
            Assembly nodify = context!.LoadFromAssemblyName(new AssemblyName("Nodify.Avalonia"));
            string[] stagedFiles = [.. Directory.EnumerateFiles(staged.Directory!).Select(Path.GetFileName)!];

            using (Assert.Multiple())
            {
                await Assert.That(staged.Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(result.ExternalRejected).IsEmpty();
                await Assert.That(status.Source).IsEqualTo(new PackSource.Installed(staged.Directory!));
                await Assert.That(status.IsCompatible).IsTrue().Because(status.Problem ?? "compatible");
                await Assert.That(pack.Id).IsEqualTo(Id);
                await Assert.That(context).IsTypeOf<ExternalLoadContext>();
                // The set pack-extension.sh stages from the Desktop head's closure; CI's dry run checks the same.
                await Assert.That(stagedFiles.Where(f => f.EndsWith(".dll", StringComparison.Ordinal)))
                    .IsEquivalentTo([Assembly + ".dll", "Nodify.Avalonia.dll", "DemoViewer.NET.RuleAuthoring.dll"]);
                await Assert.That(AssemblyLoadContext.GetLoadContext(nodify)).IsEqualTo(context);
                await Assert.That(nodify.Location).EndsWith(Path.Combine(Id, version, "Nodify.Avalonia.dll"));
                // Nodify is built against an older Avalonia of the same major, which the app's copy binds.
                await Assert.That(ExtensionLoader.FindReferenceMismatch(nodify.GetReferencedAssemblies(),
                    ExtensionLoader.RunningVersion, ReferenceRule.Bindable)).IsNull();
            }

            await HeadlessSession.RunOnUi(async () =>
            {
                ServiceCollection services = new();
                pack.Register(services);
                await using ServiceProvider provider = services.BuildServiceProvider();
                PackContributionSet set = new([pack], provider);
                ModuleRegistry registry = new();
                foreach (IWorkspaceModule module in set.Packs.SelectMany(p => p.Modules))
                {
                    registry.Register(module);
                }

                using MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, new AllOn(), null, null,
                    hubTabs: set.HubTabs);
                vm.RestoreSession();
                Window window = new() { Width = 1280, Height = 800, Content = new MainView { DataContext = vm } };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                List<Visual> all = [.. window.GetVisualDescendants()];
                Visual? editor = all.FirstOrDefault(v => Is(v, "Nodify.Avalonia.NodifyEditor"));
                List<ContentControl> containers =
                    [.. all.OfType<ContentControl>().Where(v => Is(v, "Nodify.Avalonia.ItemContainer"))];
                int connections = all.Count(v => Is(v, "Nodify.Avalonia.Connections.Connection"));
                int canvasWires = all.Count(v => v.GetType().Assembly == pack.GetType().Assembly
                                                 && Is(v, "Nodify.Avalonia.Connections.Connection"));
                List<string> faceTexts =
                [
                    .. containers.SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>())
                        .Select(t => t.Text ?? "").Where(t => t.Length > 0)
                ];
                object? faces = (editor as ItemsControl)?.ItemTemplate;
                window.Close();

                using (Assert.Multiple())
                {
                    await Assert.That(editor).IsNotNull().Because("Nodify's editor is in the tree");
                    await Assert.That(AssemblyLoadContext.GetLoadContext(editor!.GetType().Assembly)).IsEqualTo(context);
                    await Assert.That(containers.Count).IsEqualTo(27);
                    await Assert.That(connections).IsEqualTo(41);
                    await Assert.That(containers.All(c => c.GetVisualChildren().Any())).IsTrue()
                        .Because("every container has its Nodify control template applied");
                    await Assert.That(faceTexts).Contains("round_kast").Because("the canvas's own face template drew the stat");
                    await Assert.That(faces?.GetType().Assembly).IsEqualTo(pack.GetType().Assembly)
                        .Because("the editor's item template is the installed canvas's own selector");
                    await Assert.That(canvasWires).IsEqualTo(41).Because("every wire is the canvas's own connection type");
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("CS2DEMOKIT_USER_RULES_DIR", previousRules);
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // best-effort
            }
            catch (UnauthorizedAccessException)
            {
                // best-effort
            }
        }
    }

    private static bool Is(object visual, string fullName)
    {
        for (Type? t = visual.GetType(); t is not null; t = t.BaseType)
        {
            if (t.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    // pack-extension.sh's layout from the canvas's build output: its assembly, the stamped manifest under the
    // bare name, and every library that is neither beside this test host nor in the shared framework.
    private static string Stage(string directory, ECDsa key)
    {
        string configuration = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Name;
        string output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", Assembly, configuration));
        string manifest = Path.Combine(output, Id + "." + ExtensionManifest.FileName);
        if (!File.Exists(manifest))
        {
            // The project builds the canvas, so a missing output is a broken build, not a reason to skip.
            throw new InvalidOperationException($"no {Path.GetFileName(manifest)} in {output}");
        }

        Directory.CreateDirectory(directory);
        File.Copy(manifest, Path.Combine(directory, ExtensionManifest.FileName));
        foreach (string file in Directory.EnumerateFiles(output, "*.dll"))
        {
            string name = Path.GetFileName(file);
            if (name == Assembly + ".dll"
                || (!File.Exists(Path.Combine(AppContext.BaseDirectory, name)) && !Framework(Path.GetFileNameWithoutExtension(name))))
            {
                File.Copy(file, Path.Combine(directory, name));
            }
        }

        File.WriteAllText(Path.Combine(directory, ExtensionSignature.FileName), ExtensionSignature.Sign(directory, key));
        return directory;
    }

    private static bool Framework(string simpleName) =>
        AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string list
        && list.Split(Path.PathSeparator).Any(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), simpleName, StringComparison.OrdinalIgnoreCase));

    private static string FeedJson(FakeFeeds.Entry entry, string manifestJson) =>
        $$"""
          {
            "id": "{{Id}}",
            "entries": [
              {
                "version": "{{entry.Version}}",
                "manifest": {{manifestJson}},
                "url": "{{entry.Url}}",
                "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(entry.Bytes!))}}",
                "size": {{entry.Bytes!.LongLength}}
              }
            ]
          }
          """;

    private sealed class AllOn : IFeatureGate
    {
        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
