#region

using System.Reflection;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The reference rule for an extension the app does not ship: a shipped package referenced at a lower
///     version of the same major binds to the app's copy and is accepted; a higher one, another major, or a
///     0.x package at another minor is refused; the SDK stays exact. The binding half is what the runtime
///     does for <see cref="ExternalLoadContext" />, which defers every shipped name to the default context.
/// </summary>
public class ExtensionLoaderReferenceRuleTests
{
    private static readonly Dictionary<string, Version> Running = new(StringComparer.Ordinal)
    {
        ["Avalonia.Base"] = new Version(12, 1, 4, 0),
        ["SkiaSharp"] = new Version(3, 119, 0, 0),
        ["CS2DemoKit.Parser"] = new Version(0, 14, 2, 0),
        ["DemoViewer.NET.Extensions.Sdk"] = new Version(2, 1, 0, 0)
    };

    private static (string, Version, Version)? Bindable(string reference) =>
        ExtensionLoader.FindReferenceMismatch([new AssemblyName(reference)],
            n => Running.TryGetValue(n, out Version? v) ? v : null, ReferenceRule.Bindable);

    [Test]
    public async Task Bindable_AcceptsALowerVersionOfTheSameMajor_RefusesHigherOrAnotherMajor()
    {
        using (Assert.Multiple())
        {
            await Assert.That(Bindable("Avalonia.Base, Version=12.1.3.0")).IsNull();
            await Assert.That(Bindable("SkiaSharp, Version=3.116.0.0")).IsNull();
            await Assert.That(Bindable("Avalonia.Base, Version=12.1.4.0")).IsNull();
            await Assert.That(Bindable("Avalonia.Base, Version=12.2.0.0")?.Item1).IsEqualTo("Avalonia.Base");
            await Assert.That(Bindable("Avalonia.Base, Version=11.3.0.0")?.Item1).IsEqualTo("Avalonia.Base");
            await Assert.That(Bindable("Avalonia.Base, Version=13.0.0.0")?.Item1).IsEqualTo("Avalonia.Base");
        }
    }

    [Test]
    public async Task Bindable_TreatsA0xMinorAsTheMajor_AndKeepsTheSdkExact()
    {
        using (Assert.Multiple())
        {
            await Assert.That(Bindable("CS2DemoKit.Parser, Version=0.14.0.0")).IsNull();
            await Assert.That(Bindable("CS2DemoKit.Parser, Version=0.13.0.0")?.Item1).IsEqualTo("CS2DemoKit.Parser");
            await Assert.That(Bindable("DemoViewer.NET.Extensions.Sdk, Version=2.0.0.0")?.Item1)
                .IsEqualTo("DemoViewer.NET.Extensions.Sdk");
            await Assert.That(ExtensionLoader.FindReferenceMismatch([new AssemblyName("Avalonia.Base, Version=12.1.3.0")],
                    n => Running.TryGetValue(n, out Version? v) ? v : null)?.Item1)
                .IsEqualTo("Avalonia.Base").Because("the shipped path stays exact");
        }
    }

    // What the runtime does with each shape, through the context external extensions load in.
    [Test]
    public async Task TheRuntime_BindsALowerRequestToTheAppsCopy_AndRefusesAHigherOne()
    {
        Assembly app = typeof(Avalonia.AvaloniaObject).Assembly;
        Version running = app.GetName().Version!;
        string dir = Path.Combine(Path.GetTempPath(), "dvbind-" + Guid.NewGuid().ToString("N"));
        ExternalLoadContext context = new(new ExtensionCandidate(dir,
            FakeManifests.For("dev.example.bind", "Bind", "1.0.0")));

        Assembly lower = context.LoadFromAssemblyName(
            new AssemblyName($"Avalonia.Base, Version={running.Major}.0.0.0"));
        Exception? higher = Record(() => context.LoadFromAssemblyName(
            new AssemblyName($"Avalonia.Base, Version={running.Major}.{running.Minor + 1}.0.0")));
        Exception? lowerMajor = Record(() => context.LoadFromAssemblyName(
            new AssemblyName($"Avalonia.Base, Version={running.Major - 1}.0.0.0")));

        using (Assert.Multiple())
        {
            await Assert.That(ReferenceEquals(lower, app)).IsTrue();
            await Assert.That(higher).IsTypeOf<FileNotFoundException>()
                .Because("the default context refuses a request above the version it has");
            await Assert.That(lowerMajor).IsNull()
                .Because("the runtime binds a lower major too; refusing it is the loader's policy, not the binder's");
        }
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
