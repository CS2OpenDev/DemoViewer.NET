#region

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A scratch config root for one test: points <see cref="AppPaths.ConfigDirEnvVar" /> at a temp folder with
///     an empty <c>themes</c> folder, and puts the variable back and deletes the folder on dispose.
/// </summary>
internal sealed class TempThemesFolder : IDisposable
{
    private readonly string? _prior = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);

    public TempThemesFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "dv-theme-test-" + Guid.NewGuid().ToString("N"));
        Themes = Path.Combine(Root, "themes");
        Directory.CreateDirectory(Themes);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, Root);
    }

    public string Root { get; }

    public string Themes { get; }

    public string Write(string fileName, string json)
    {
        string path = Path.Combine(Themes, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, _prior);
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            /* best-effort */
        }
    }
}

/// <summary>
///     The theme model: the four built-ins are complete and cannot be changed, Dark and Light read from the
///     palette XML match what the app compiles, an export reads back to the same palette, and a user file that
///     leaves tokens out, adds keys this version does not know, or is broken, still loads or is reported.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ThemeModelTests
{
    [Test]
    public async Task EveryBuiltIn_HasAValueForEveryToken()
    {
        await Assert.That(BuiltInThemes.All.Select(p => p.Id))
            .IsEquivalentTo(["dark", "light", "high-contrast", "egirl"]);
        foreach (ThemePalette palette in BuiltInThemes.All)
        {
            string[] missing =
                [.. ThemeTokens.All.Where(t => !palette.Colors.ContainsKey(t) && !palette.Gradients.ContainsKey(t))];
            await Assert.That(missing).IsEmpty().Because(palette.Id);
        }
    }

    [Test]
    public async Task BuiltInPalettes_CannotBeChanged()
    {
        ThemePalette dark = BuiltInThemes.Dark;
        await Assert.That(() => ((IDictionary<string, Color>)dark.Colors)["ShellBg"] = Colors.Red)
            .Throws<NotSupportedException>();
        await Assert.That(BuiltInThemes.Find("dark")!.Colors["ShellBg"]).IsEqualTo(Color.Parse("#080816"));
    }

    [Test]
    public async Task UserFile_WithABuiltInId_IsRefusedAndReported_AndTheStoreWillNotWriteABuiltIn()
    {
        using TempThemesFolder folder = new();
        folder.Write("fake.json", """{ "id": "egirl", "name": "Not E-Girl", "tokens": { "ShellBg": "#FF0000" } }""");
        ThemeRegistry registry = new();
        registry.Reload();

        await Assert.That(registry.Find("egirl")!.IsBuiltIn).IsTrue();
        await Assert.That(registry.Find("egirl")!.DisplayName).IsEqualTo("E-Girl (Pink / Black)");
        await Assert.That(registry.LoadProblems.Single().FileName).IsEqualTo("fake.json");
        await Assert.That(registry.LoadProblems.Single().Reason).Contains("built-in");

        UserThemeStore store = new(registry);
        await Assert.That(() => store.Save("egirl", BuiltInThemes.Dark)).Throws<InvalidOperationException>();
        await Assert.That(() => store.Delete("dark")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DarkAndLight_ReadFromThePaletteXml_MatchTheCompiledPalette()
    {
        List<string> diffs = [];
        await HeadlessSession.RunOnUi(() =>
        {
            foreach ((ThemePalette palette, ThemeVariant variant) in new[]
                     {
                         (BuiltInThemes.Dark, ThemeVariant.Dark), (BuiltInThemes.Light, ThemeVariant.Light)
                     })
            {
                foreach (string token in ThemeTokens.All)
                {
                    Application.Current!.TryGetResource(token, variant, out object? compiled);
                    object model = palette.Colors.TryGetValue(token, out Color c)
                        ? new SolidColorBrush(c)
                        : palette.Gradients[token].ToBrush();
                    if (!ThemeSampleFileTests.SameBrush(compiled, model))
                    {
                        diffs.Add(
                            $"{palette.Id} {token}: compiled {ThemeSampleFileTests.Describe(compiled)}, model {ThemeSampleFileTests.Describe(model)}");
                    }
                }
            }

            return Task.CompletedTask;
        });

        await Assert.That(diffs).IsEmpty().Because(string.Join("\n", diffs));
    }

    [Test]
    public async Task CustomBuiltIns_ResolveInTheAppExactlyAsTheirPalette()
    {
        List<string> diffs = [];
        ThemeRegistry registry = new();
        await HeadlessSession.RunOnUi(() =>
        {
            registry.Install(Application.Current!);
            try
            {
                foreach (ThemePalette palette in BuiltInThemes.All)
                {
                    ThemeVariant variant = registry.VariantFor(palette.Id);
                    foreach (string token in ThemeTokens.All)
                    {
                        Application.Current!.TryGetResource(token, variant, out object? live);
                        object model = palette.Colors.TryGetValue(token, out Color c)
                            ? new SolidColorBrush(c)
                            : palette.Gradients[token].ToBrush();
                        if (!ThemeSampleFileTests.SameBrush(live, model))
                        {
                            diffs.Add($"{palette.Id} {token}");
                        }
                    }
                }
            }
            finally
            {
                registry.Uninstall(Application.Current!);
            }

            return Task.CompletedTask;
        });

        await Assert.That(diffs).IsEmpty().Because(string.Join(", ", diffs));
    }

    [Test]
    public async Task Export_ReadsBackToTheSamePalette_ForEveryBuiltIn()
    {
        foreach (ThemePalette palette in BuiltInThemes.All)
        {
            ThemeParseResult parsed = ThemeJson.Parse(ThemeExport.ToJson(palette), palette.Id);
            await Assert.That(parsed.Definition).IsNotNull().Because(parsed.Error ?? palette.Id);
            await Assert.That(parsed.BadValues).IsEmpty();
            ThemePalette back = ThemePalette.Resolve(parsed.Definition!);

            await Assert.That(back.Id).IsEqualTo(palette.Id);
            await Assert.That(back.DisplayName).IsEqualTo(palette.DisplayName);
            await Assert.That(back.BaseVariant).IsEqualTo(palette.BaseVariant);
            await Assert.That(back.Inherited).IsEmpty().Because("an export sets every token");
            string[] differ = [.. ThemeTokens.All.Where(t => !back.SameValue(t, palette))];
            await Assert.That(differ).IsEmpty().Because($"{palette.Id}: {string.Join(", ", differ)}");
        }
    }

    [Test]
    public async Task Export_MarksTheTokensABuiltInInherits()
    {
        ThemePalette egirl = BuiltInThemes.Find("egirl")!;
        string json = ThemeExport.ToJson(egirl);
        string pb2DTeamCt = json.Split('\n')
            .Single(l => l.TrimStart().StartsWith("\"Pb2dTeamCt\"", StringComparison.Ordinal));
        string shell = json.Split('\n').Single(l => l.TrimStart().StartsWith("\"ShellBg\"", StringComparison.Ordinal));

        await Assert.That(egirl.Inherited.Contains("Pb2dTeamCt")).IsTrue();
        await Assert.That(pb2DTeamCt).EndsWith("// base");
        await Assert.That(shell).DoesNotContain("// base");
        foreach (ThemeSection section in ThemeExport.Sections)
        {
            await Assert.That(json).Contains("    // " + section.Title + "\n");
        }
    }

    [Test]
    public async Task MissingTokens_FallBackToTheBase_AndAreReported()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json",
            """{ "id": "partial", "name": "Partial", "base": "light", "tokens": { "ShellBg": "#010203", "PanelBg": "nope" } }""");
        ThemeRegistry registry = new();
        registry.Reload();
        Theme theme = registry.Find("partial")!;

        await Assert.That(theme.MissingTokens.Count).IsEqualTo(ThemeTokens.All.Count - 1);
        await Assert.That(theme.MissingTokens).Contains("PanelBg").Because("a bad value falls back like a missing one");
        await Assert.That(theme.MissingTokens).Contains("LibraryCardScrim")
            .Because("a missing gradient follows the same rule");
        await Assert.That(theme.BadValues).IsEquivalentTo(["PanelBg"]);
        await Assert.That(theme.MissingTokens[0]).IsEqualTo(ThemeTokens.All[1]).Because("palette order");

        ThemePalette palette = registry.PaletteFor("partial")!;
        await Assert.That(palette.Colors["ShellBg"]).IsEqualTo(Color.Parse("#010203"));
        await Assert.That(palette.Colors["PanelBg"]).IsEqualTo(BuiltInThemes.Light.Colors["PanelBg"]);
        await Assert.That(palette.Gradients["LibraryCardScrim"])
            .IsEqualTo(BuiltInThemes.Light.Gradients["LibraryCardScrim"]);

        object? live = null;
        await HeadlessSession.RunOnUi(() =>
        {
            registry.Install(Application.Current!);
            Application.Current!.TryGetResource("PanelBg", theme.Variant, out live);
            registry.Uninstall(Application.Current);
            return Task.CompletedTask;
        });
        await Assert.That(((ISolidColorBrush)live!).Color).IsEqualTo(BuiltInThemes.Light.Colors["PanelBg"]);
    }

    [Test]
    public async Task UnknownKeys_AreToleratedKeptAndWrittenBack()
    {
        ThemeParseResult parsed = ThemeJson.Parse(
            """
            // a comment
            { "ID": "future", "Tokens": { "ShellBg": "#010203", "ShinyNewToken": "#445566", "Odd": { "x": [1, 2] }, "Num": 4, }, "extra": true }
            """, "test");
        ThemeDefinition def = parsed.Definition!;

        await Assert.That(def.Id).IsEqualTo("future");
        await Assert.That(def.Tokens.Keys).IsEquivalentTo(["ShellBg"]);
        await Assert.That(def.UnknownTokens.Keys).IsEquivalentTo(["ShinyNewToken", "Odd", "Num"]);

        string json = ThemeExport.ToJson(ThemePalette.Resolve(def), includeInherited: false);
        ThemeDefinition again = ThemeJson.TryParse(json, "again")!;
        await Assert.That(again.UnknownTokens["ShinyNewToken"]).IsEqualTo("\"#445566\"");
        await Assert.That(again.UnknownTokens["Odd"]).IsEqualTo("""{ "x": [1, 2] }""");
        await Assert.That(again.Tokens.Keys).IsEquivalentTo(["ShellBg"])
            .Because("inherited tokens left out stay missing");

        using TempThemesFolder folder = new();
        folder.Write("future.json", json);
        ThemeRegistry registry = new();
        registry.Reload();
        await Assert.That(registry.Find("future")!.UnknownKeys).IsEquivalentTo(["Num", "Odd", "ShinyNewToken"]);
    }

    [Test]
    public async Task MalformedFiles_AreSkipped_WithAReason()
    {
        using TempThemesFolder folder = new();
        folder.Write("broken.json", "{ \"id\": \"x\", ");
        folder.Write("noid.json", """{ "name": "No id" }""");
        folder.Write("ok.json", """{ "id": "ok" }""");
        ThemeRegistry registry = new();
        registry.Reload();

        await Assert.That(registry.Contains("ok")).IsTrue();
        await Assert.That(registry.LoadProblems.Select(p => p.FileName)).IsEquivalentTo(["broken.json", "noid.json"]);
        await Assert.That(registry.LoadProblems.First(p => p.FileName == "broken.json").Reason)
            .Contains("not valid JSON");
        await Assert.That(registry.LoadProblems.First(p => p.FileName == "noid.json").Reason).Contains("no \"id\"");
    }

    [Test]
    public async Task AGradientToken_FromAUserFile_Applies()
    {
        using TempThemesFolder folder = new();
        folder.Write("g.json", """
                               { "id": "g", "tokens": { "LibraryCardScrim": { "start": "0%,0%", "end": "100%,0%",
                                 "stops": [ { "offset": 0, "color": "#FF0000" }, { "offset": 1, "color": "#800000FF" } ] } } }
                               """);
        ThemeRegistry registry = new();
        registry.Reload();

        ThemeGradient g = registry.PaletteFor("g")!.Gradients["LibraryCardScrim"];
        await Assert.That(registry.Find("g")!.MissingTokens).DoesNotContain("LibraryCardScrim");
        await Assert.That(g.Stops.Select(s => s.Color))
            .IsEquivalentTo([Color.Parse("#FF0000"), Color.Parse("#800000FF")]);
        await Assert.That(g.End).IsEqualTo(RelativePoint.Parse("100%,0%"));
    }

    [Test]
    public async Task TheSyntaxHighlighter_KeepsATokensAlpha()
    {
        ThemeRegistry registry = new();
        Theme theme = registry.RegisterCustom("see-through", "See-through", ThemeVariant.Dark,
            new Dictionary<string, Color> { ["SyntaxComment"] = Color.Parse("#806A9955") }, ThemeSource.User);
        Color? comment = null;
        await HeadlessSession.RunOnUi(() =>
        {
            registry.Install(Application.Current!);
            try
            {
                Views.RuleWorkbench.WorkbenchYamlHighlighting.ClearCache();
                AvaloniaEdit.Highlighting.IHighlightingDefinition def =
                    Views.RuleWorkbench.WorkbenchYamlHighlighting.DefinitionFor(theme.Variant);
                comment = (def.GetNamedColor("Comment").Foreground.GetBrush(null) as ISolidColorBrush)?.Color;
            }
            finally
            {
                Views.RuleWorkbench.WorkbenchYamlHighlighting.ClearCache();
                registry.Uninstall(Application.Current!);
            }

            return Task.CompletedTask;
        });

        await Assert.That(comment).IsEqualTo(Color.Parse("#806A9955"));
    }

    [Test]
    public async Task TheCatalogue_DescribesTheTokensTheEditorShows()
    {
        ThemeTokenInfo? shell = ThemeTokenCatalog.Find("ShellBg");
        await Assert.That(shell).IsNotNull();
        await Assert.That(shell!.Family).IsEqualTo("Shell");
        await Assert.That(shell.Description).Contains("App window");
        await Assert.That(ThemeTokenCatalog.Find("StatBarFill")!.Description).StartsWith("Filled part.");

        // Most tokens are described. The rest show no description rather than a wrong one.
        int described = ThemeTokens.All.Count(t => ThemeTokenCatalog.Find(t) is not null);
        await Assert.That(described).IsGreaterThan(ThemeTokens.All.Count * 9 / 10);
    }
}
