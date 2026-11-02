#region

using System.Text;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     Creates, saves, renames and deletes user themes in <see cref="AppPaths.ThemesDirectory" />, then reloads the
///     registry so every list sees the change. Built-ins are never written: customizing one writes a new user
///     theme that starts as its full export. Names are unique across every theme, ignoring case, and each new
///     theme gets an id no theme or file in the folder already has.
/// </summary>
public sealed class UserThemeStore(ThemeRegistry registry)
{
    /// <summary>False where there is no themes folder (the browser head).</summary>
    public static bool IsAvailable => AppPaths.ThemesDirectory is not null;

    public ThemeRegistry Registry { get; } = registry;

    /// <summary>
    ///     Why <paramref name="name" /> cannot be used, or null when it can. <paramref name="exceptId" /> is the
    ///     theme being renamed, which may keep its own name.
    /// </summary>
    public string? ValidateName(string? name, string? exceptId = null)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return "Give the theme a name.";
        }

        if (trimmed.Length > 80)
        {
            return "That name is too long. Keep it under 80 characters.";
        }

        Theme? clash = Registry.Themes.FirstOrDefault(t =>
            string.Equals(t.DisplayName.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t.Id, exceptId, StringComparison.OrdinalIgnoreCase));
        return clash is null ? null : $"There is already a theme called \"{clash.DisplayName}\".";
    }

    /// <summary>A free name for a copy of <paramref name="sourceName" />: "X copy", then "X copy 2", and so on.</summary>
    public string SuggestName(string sourceName)
    {
        string stem = sourceName.Trim() + " copy";
        string candidate = stem;
        for (int n = 2; ValidateName(candidate) is not null; n++)
        {
            candidate = $"{stem} {n}";
        }

        return candidate;
    }

    /// <summary>
    ///     A new id from <paramref name="name" />: lower-case letters, digits and dashes, unique against every
    ///     registered theme and every file in the folder (a file the registry skipped still owns its name).
    /// </summary>
    public string NewId(string name)
    {
        StringBuilder slug = new();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        string stem = slug.ToString().Trim('-');
        if (stem.Length == 0)
        {
            stem = "theme";
        }

        if (stem.Length > 40)
        {
            stem = stem[..40].TrimEnd('-');
        }

        stem = "user-" + stem;
        string candidate = stem;
        for (int n = 2; Taken(candidate); n++)
        {
            candidate = $"{stem}-{n}";
        }

        return candidate;
    }

    private bool Taken(string id) =>
        Registry.Contains(id)
        || id.StartsWith(ThemeRegistry.PreviewId, StringComparison.OrdinalIgnoreCase)
        || (AppPaths.ThemesDirectory is { } dir && File.Exists(Path.Combine(dir, id + ".json")));

    /// <summary>
    ///     Writes a new user theme named <paramref name="name" /> that starts as <paramref name="palette" />
    ///     written out in full, and returns it. Throws <see cref="ArgumentException" /> for a name
    ///     <see cref="ValidateName" /> refuses.
    /// </summary>
    public Theme Create(ThemePalette palette, string name)
    {
        if (ValidateName(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        string dir = Directory();
        string trimmed = name.Trim();
        string id = NewId(trimmed);
        string path = Path.Combine(dir, id + ".json");
        AtomicFile.WriteAllText(path, ThemeExport.ToJson(palette, id, trimmed));
        Registry.Reload();
        return Registry.Find(id) ?? throw new IOException($"The new theme {path} did not load.");
    }

    /// <summary>A copy of any theme with a palette (built-in or user) under a new name.</summary>
    public Theme Customize(string sourceId, string name) =>
        Create(Registry.PaletteFor(sourceId) ?? throw new ArgumentException($"\"{sourceId}\" has no palette to copy.", nameof(sourceId)), name);

    /// <summary>
    ///     Overwrites the user theme <paramref name="id" /> with <paramref name="palette" />, keeping its id and file.
    ///     Tokens the palette inherits are not written, so a token the user never filled stays missing.
    /// </summary>
    public Theme Save(string id, ThemePalette palette, string? name = null)
    {
        Theme theme = UserTheme(id);
        string displayName = (name ?? palette.DisplayName).Trim();
        if (ValidateName(displayName, id) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        AtomicFile.WriteAllText(theme.FilePath!, ThemeExport.ToJson(palette, theme.Id, displayName, includeInherited: false));
        Registry.Reload();
        return Registry.Find(id)!;
    }

    /// <summary>Renames the user theme <paramref name="id" />. Its id stays, so settings that select it keep working.</summary>
    public Theme Rename(string id, string newName)
    {
        ThemePalette palette = Registry.PaletteFor(id) ?? throw new ArgumentException($"\"{id}\" is not a theme.", nameof(id));
        return Save(id, palette, newName);
    }

    /// <summary>
    ///     Deletes the user theme <paramref name="id" />: its file, and any other file in the folder with the same
    ///     id. Only one of them is loaded, but leaving the rest would bring the theme back on the next reload.
    /// </summary>
    public void Delete(string id)
    {
        Theme theme = UserTheme(id);
        foreach (string file in FilesFor(id).Append(theme.FilePath!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            File.Delete(file);
        }

        Registry.Reload();
    }

    /// <summary>
    ///     Every file in the themes folder whose id is <paramref name="id" />, in load order. Usually one; two
    ///     files can share an id, and the registry loads the later one and lists the earlier as a load problem.
    /// </summary>
    public static IReadOnlyList<string> FilesFor(string id)
    {
        if (AppPaths.ThemesDirectory is not { } dir || !System.IO.Directory.Exists(dir))
        {
            return [];
        }

        List<string> files = [];
        foreach (string file in System.IO.Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (ThemeJson.Parse(json, file).Definition is { } def && string.Equals(def.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }

        return files;
    }

    private Theme UserTheme(string id)
    {
        Theme theme = Registry.Find(id) ?? throw new ArgumentException($"\"{id}\" is not a theme.", nameof(id));
        if (theme.IsBuiltIn || theme.FilePath is null)
        {
            throw new InvalidOperationException($"{theme.DisplayName} is built in and cannot be changed. Customize a copy instead.");
        }

        return theme;
    }

    private static string Directory() =>
        AppPaths.EnsureThemesDirectory() ?? throw new InvalidOperationException("There is no themes folder on this platform.");
}
