#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Services;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.Theming;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     samples/Themes/ holds each built-in custom theme written out as a complete user drop-in, id suffixed
///     <c>-json</c>. These load the samples the way a user's file loads (the themes folder, then
///     <see cref="ThemeRegistry.Reload" />) and hold every token they resolve to the built-in's, so the two
///     cannot drift while the built-ins still ship from Themes/*.json.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ThemeSampleFileTests
{
    private const string Suffix = "-json";

    private static string SamplesDir()
    {
        string? root = DemoTestHelper.FindRepoRoot();
        if (root is null)
        {
            throw new SkipTestException("repo root not found from the test bin");
        }

        return Path.Combine(root, "samples", "Themes");
    }

    // Copies the samples into a scratch config dir and runs the real drop-in scan over it.
    private static async Task WithSamplesLoaded(Action<ThemeRegistry> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-theme-samples-" + Guid.NewGuid().ToString("N"));
        string themesDir = Path.Combine(dir, "themes");
        Directory.CreateDirectory(themesDir);
        foreach (string file in Directory.GetFiles(SamplesDir(), "*.json"))
        {
            File.Copy(file, Path.Combine(themesDir, Path.GetFileName(file)));
        }

        string? prior = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        try
        {
            ThemeRegistry registry = new();
            registry.Reload();
            await HeadlessSession.RunOnUi(() =>
            {
                registry.Install(Application.Current!);
                try
                {
                    body(registry);
                }
                finally
                {
                    registry.Uninstall(Application.Current!);
                }

                return Task.CompletedTask;
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, prior);
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* best-effort */
            }
        }
    }

    [Test]
    public async Task EveryBuiltInCustomTheme_HasASample_AndEverySampleLoadsAsAUserTheme()
    {
        string[] builtIns = [];
        string[] samples = [];
        await WithSamplesLoaded(registry =>
        {
            builtIns = [.. registry.Themes.Where(t => t.Source == ThemeSource.BuiltIn && t.Variant.InheritVariant is not null).Select(t => t.Id)];
            samples = [.. registry.Themes.Where(t => t.Source == ThemeSource.User).Select(t => t.Id)];
        });

        await Assert.That(samples.Length).IsEqualTo(Directory.GetFiles(SamplesDir(), "*.json").Length)
            .Because("every sample file must parse and register; a skipped one is malformed or reuses a built-in id");
        await Assert.That(samples.Select(id => id.EndsWith(Suffix, StringComparison.Ordinal) ? id[..^Suffix.Length] : id))
            .IsEquivalentTo(builtIns);
    }

    [Test]
    public async Task EachSample_ResolvesEveryToken_ExactlyAsItsBuiltIn()
    {
        List<string> diffs = [];
        await WithSamplesLoaded(registry =>
        {
            foreach (Theme sample in registry.Themes.Where(t => t.Source == ThemeSource.User))
            {
                ThemeVariant builtIn = registry.VariantFor(sample.Id[..^Suffix.Length]);
                if (!Equals(sample.Variant.InheritVariant, builtIn.InheritVariant))
                {
                    diffs.Add($"{sample.Id}: base {sample.Variant.InheritVariant}, built-in {builtIn.InheritVariant}");
                }

                foreach (string token in ThemeTokens.All)
                {
                    Application.Current!.TryGetResource(token, builtIn, out object? want);
                    Application.Current.TryGetResource(token, sample.Variant, out object? got);
                    bool same = (want, got) switch
                    {
                        (ISolidColorBrush w, ISolidColorBrush g) => w.Color == g.Color,
                        _ => ReferenceEquals(want, got) // a gradient: both must inherit the same base brush
                    };
                    if (!same)
                    {
                        diffs.Add($"{sample.Id} {token}: built-in {Describe(want)}, sample {Describe(got)}");
                    }
                }
            }
        });

        await Assert.That(diffs).IsEmpty().Because(string.Join("\n", diffs));
    }

    // Resolving equal is not enough for a reference file: a token left out inherits the same value, so this
    // checks the file itself names every token a theme file can set, and nothing else.
    [Test]
    public async Task EachSample_SetsEverySolidToken_AndNoUnknownOnes()
    {
        string[] solid = [];
        await HeadlessSession.RunOnUi(() =>
        {
            solid =
            [
                .. ThemeTokens.All.Where(t =>
                    Application.Current!.TryGetResource(t, ThemeVariant.Dark, out object? o) && o is ISolidColorBrush)
            ];
            return Task.CompletedTask;
        });

        foreach (string file in Directory.GetFiles(SamplesDir(), "*.json"))
        {
            ThemeDefinition? def = ThemeJson.TryParse(await File.ReadAllTextAsync(file), file);
            await Assert.That(def).IsNotNull().Because(file);

            string[] missing = [.. solid.Where(t => !def!.Tokens.ContainsKey(t))];
            string[] unknown = [.. def!.Tokens.Keys.Where(k => !ThemeTokens.All.Contains(k))];
            await Assert.That(missing).IsEmpty().Because($"{Path.GetFileName(file)} omits {string.Join(", ", missing)}");
            await Assert.That(unknown).IsEmpty().Because($"{Path.GetFileName(file)} sets {string.Join(", ", unknown)}");
        }
    }

    private static string Describe(object? brush) => brush switch
    {
        ISolidColorBrush b => b.Color.ToString(),
        null => "(none)",
        _ => brush.GetType().Name
    };
}
