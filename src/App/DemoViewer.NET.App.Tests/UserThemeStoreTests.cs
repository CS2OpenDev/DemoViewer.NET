#region

using Avalonia.Media;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Customizing writes a named copy with a fresh id into the themes folder; names are unique ignoring case;
///     rename keeps the id; delete removes the file; a save leaves a token the user never filled still missing.
/// </summary>
[NotInParallel]
public class UserThemeStoreTests
{
    [Test]
    public async Task CustomizingABuiltIn_WritesANamedCopyWithAFreshId()
    {
        using TempThemesFolder folder = new();
        ThemeRegistry registry = new();
        registry.Reload();
        UserThemeStore store = new(registry);

        Theme copy = store.Customize("egirl", "  My pink  ");

        await Assert.That(copy.Source).IsEqualTo(ThemeSource.User);
        await Assert.That(copy.DisplayName).IsEqualTo("My pink");
        await Assert.That(copy.Id).IsEqualTo("user-my-pink");
        await Assert.That(File.Exists(Path.Combine(folder.Themes, "user-my-pink.json"))).IsTrue();
        await Assert.That(copy.MissingTokens).IsEmpty().Because("a copy starts as the full export");

        ThemePalette source = BuiltInThemes.Find("egirl")!;
        ThemePalette mine = registry.PaletteFor(copy.Id)!;
        await Assert.That(ThemeTokens.All.Where(t => !mine.SameValue(t, source))).IsEmpty();
        await Assert.That(registry.Find("egirl")!.IsBuiltIn).IsTrue().Because("the built-in itself is untouched");
    }

    [Test]
    public async Task NameCollisions_AreRefused_AndSuggestionsAndIdsStayUnique()
    {
        using TempThemesFolder folder = new();
        ThemeRegistry registry = new();
        registry.Reload();
        UserThemeStore store = new(registry);

        await Assert.That(store.ValidateName("dark")).IsEqualTo("There is already a theme called \"Dark\".");
        await Assert.That(store.ValidateName("   ")).IsEqualTo("Give the theme a name.");
        await Assert.That(store.SuggestName("Dark")).IsEqualTo("Dark copy");

        Theme first = store.Customize("dark", "Dark copy");
        await Assert.That(store.SuggestName("Dark")).IsEqualTo("Dark copy 2");
        await Assert.That(() => store.Customize("light", "DARK COPY")).Throws<ArgumentException>();

        // A rename frees the name, but not the id: the old file name is still taken.
        store.Rename(first.Id, "Something else");
        Theme second = store.Customize("light", "Dark copy");
        await Assert.That(second.Id).IsEqualTo("user-dark-copy-2");
        await Assert.That(registry.Find(first.Id)!.DisplayName).IsEqualTo("Something else");
        await Assert.That(store.ValidateName("Something else", first.Id)).IsNull().Because("a theme may keep its own name");

        // A name with nothing usable for an id still gets one.
        await Assert.That(store.NewId("!!!")).IsEqualTo("user-theme");
    }

    [Test]
    public async Task RenameKeepsTheId_AndDeleteRemovesTheFile()
    {
        using TempThemesFolder folder = new();
        ThemeRegistry registry = new();
        registry.Reload();
        UserThemeStore store = new(registry);
        Theme theme = store.Customize("high-contrast", "Loud");
        string path = theme.FilePath!;

        Theme renamed = store.Rename(theme.Id, "Louder");
        await Assert.That(renamed.Id).IsEqualTo(theme.Id);
        await Assert.That(renamed.FilePath).IsEqualTo(path);
        await Assert.That(registry.Themes.Count(t => t.DisplayName == "Louder")).IsEqualTo(1);

        store.Delete(theme.Id);
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(registry.Contains(theme.Id)).IsFalse();
    }

    // Two files with one id load as one theme (the later file wins). Deleting that theme must take both, or the
    // reload brings the earlier file back as the theme the user just deleted.
    [Test]
    public async Task Delete_RemovesEveryFileWithTheId()
    {
        using TempThemesFolder folder = new();
        string first = folder.Write("a-dup.json", """{ "id": "dup", "name": "Older", "tokens": { "ShellBg": "#010203" } }""");
        string second = folder.Write("b-dup.json", """{ "id": "dup", "name": "Newer", "tokens": { "ShellBg": "#040506" } }""");
        string other = folder.Write("other.json", """{ "id": "other", "name": "Other", "tokens": { "ShellBg": "#070809" } }""");
        ThemeRegistry registry = new();
        registry.Reload();
        UserThemeStore store = new(registry);
        await Assert.That(registry.Find("dup")!.DisplayName).IsEqualTo("Newer");
        await Assert.That(UserThemeStore.FilesFor("dup").Select(f => Path.GetFileName(f))).IsEquivalentTo(["a-dup.json", "b-dup.json"]);

        store.Delete("dup");

        await Assert.That(File.Exists(first)).IsFalse();
        await Assert.That(File.Exists(second)).IsFalse();
        await Assert.That(registry.Contains("dup")).IsFalse().Because("the older file does not come back");
        await Assert.That(File.Exists(other)).IsTrue();
    }

    [Test]
    public async Task Saving_KeepsUnfilledTokensMissing_AndWritesEdits()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", """{ "id": "partial", "name": "Partial", "tokens": { "ShellBg": "#010203" } }""");
        ThemeRegistry registry = new();
        registry.Reload();
        UserThemeStore store = new(registry);

        ThemePalette palette = registry.PaletteFor("partial")!;
        Dictionary<string, Color> colors = new(palette.Colors) { ["PanelBg"] = Color.Parse("#0A0B0C") };
        ThemePalette edited = new(palette.Id, palette.DisplayName, palette.BaseVariant, colors, palette.Gradients,
            palette.Inherited.Where(t => t != "PanelBg"));
        Theme saved = store.Save("partial", edited);

        await Assert.That(saved.MissingTokens.Count).IsEqualTo(ThemeTokens.All.Count - 2);
        await Assert.That(registry.PaletteFor("partial")!.Colors["PanelBg"]).IsEqualTo(Color.Parse("#0A0B0C"));
    }
}
