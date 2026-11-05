#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DemoViewer.NET.Services;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     The single source of truth for available themes (central theme system, design notes in git history).
///     Holds the built-in <c>dark</c> / <c>light</c> / <c>system</c> (native <see cref="ThemeVariant" />s whose
///     tokens live in <c>DarkPalette.axaml</c>) plus any registered CUSTOM themes (High-Contrast, E-Girl, user
///     drop-ins). A custom theme is registered as a <c>ThemeVariant(id, base)</c> and a token-override
///     <see cref="ResourceDictionary" />; those override dictionaries are collected in one registry-owned
///     dictionary and merged into the app once, so the variant → base → Default fallback chain resolves each
///     token: a custom variant's own token wins, everything else inherits its base Light/Dark palette.
///     <para>
///         Load order: the built-ins first, from <see cref="BuiltInThemes" />, then user drop-ins from
///         <see cref="AppPaths.ThemesDirectory" /> on <see cref="Reload" />. A user file can never replace a
///         built-in: one that reuses a built-in id is skipped and listed in <see cref="LoadProblems" />. A token
///         a user file leaves out resolves through the variant chain to its base's built-in value, and is listed
///         on <see cref="Theme.MissingTokens" /> so the app can say so.
///     </para>
///     <para>
///         The theme editor's live preview is one more custom variant, <see cref="PreviewId" />, holding a
///         complete palette whose brushes are recoloured in place as the user edits. It is never in
///         <see cref="Themes" /> and is removed by <see cref="EndPreview" />.
///     </para>
/// </summary>
public sealed class ThemeRegistry
{
    /// <summary>The id of the editor's preview variant. Not selectable, and never saved.</summary>
    public const string PreviewId = "theme-editor-preview";

    private readonly Dictionary<string, Theme> _byId = new(StringComparer.OrdinalIgnoreCase);

    // One dictionary holding ThemeDictionaries[customVariant] = overrides, merged into Application.Resources.
    private readonly ResourceDictionary _customDicts = new();

    // What each user theme's file set, for PaletteFor (a copy, the editor).
    private readonly Dictionary<string, ThemeDefinition> _userDefinitions = new(StringComparer.OrdinalIgnoreCase);

    // Ordered store (built-ins first, in registration order, then user drop-ins) + a case-insensitive id
    // lookup over the same Theme instances. Both are kept in sync by Add/Remove.
    private readonly List<Theme> _ordered = [];
    private readonly List<ThemeLoadProblem> _problems = [];
    private bool _installed;

    private ResourceDictionary? _previewDict;
    private int _previewSerial;

    public ThemeRegistry()
    {
        Add(new Theme { Id = "dark", DisplayName = "Dark", Variant = ThemeVariant.Dark });
        Add(new Theme { Id = "light", DisplayName = "Light", Variant = ThemeVariant.Light });
        Add(new Theme { Id = "system", DisplayName = "System", Variant = ThemeVariant.Default, CanCustomize = false });

        // The custom built-ins register only what they set; the rest falls through to Dark exactly as before.
        foreach (ThemeDefinition def in BuiltInThemes.CustomDefinitions)
        {
            Register(def, ThemeSource.BuiltIn);
        }
    }

    /// <summary>All registered themes, in registration order (built-ins first, then user drop-ins).</summary>
    public IReadOnlyList<Theme> Themes => [.. _ordered];

    /// <summary>Theme files the last <see cref="Reload" /> skipped, with the reason.</summary>
    public IReadOnlyList<ThemeLoadProblem> LoadProblems => [.. _problems];

    /// <summary>Fires after <see cref="Reload" /> re-scans the drop-in directory, so the app can refresh + repaint.</summary>
    public event EventHandler? Reloaded;

    private void Add(Theme t)
    {
        if (_byId.TryGetValue(t.Id, out Theme? existing))
        {
            _ordered.Remove(existing); // replace in place (re-registering the same id, e.g. a drop-in edit)
        }

        _byId[t.Id] = t;
        _ordered.Add(t);
    }

    /// <summary>
    ///     Registers a custom theme: a <paramref name="baseVariant" /> (Light or Dark) it inherits plus
    ///     <paramref name="tokens" /> it overrides. Idempotent per id (re-registering replaces the overrides,
    ///     e.g. a drop-in edit + reload). Returns the registered <see cref="Theme" />.
    /// </summary>
    public Theme RegisterCustom(
        string id, string displayName, ThemeVariant baseVariant,
        IReadOnlyDictionary<string, Color> tokens, ThemeSource source = ThemeSource.BuiltIn) =>
        RegisterCore(new ThemeDefinition(id, displayName, baseVariant, tokens), source, null, []);

    /// <summary>Registers a <see cref="ThemeDefinition" /> (parsed JSON, a built-in or a user drop-in).</summary>
    private void Register(ThemeDefinition def, ThemeSource source) => RegisterCore(def, source, null, []);

    private Theme RegisterCore(ThemeDefinition def, ThemeSource source, string? filePath,
        IReadOnlyList<string> badValues)
    {
        ThemeVariant variant = new(def.Id, def.BaseVariant);
        _customDicts.ThemeDictionaries[variant] = BuildDictionary(def.Tokens, def.Gradients);

        Theme theme = new()
        {
            Id = def.Id,
            DisplayName = def.DisplayName,
            Variant = variant,
            Source = source,
            FilePath = filePath,
            MissingTokens = source == ThemeSource.User ? [.. ThemeTokens.All.Where(t => !def.Sets(t))] : [],
            UnknownKeys = source == ThemeSource.User ? [.. def.UnknownTokens.Keys] : [],
            BadValues = badValues
        };
        if (source == ThemeSource.User)
        {
            _userDefinitions[def.Id] = def;
        }

        Add(theme);
        return theme;
    }

    private static ResourceDictionary BuildDictionary(
        IReadOnlyDictionary<string, Color> tokens, IReadOnlyDictionary<string, ThemeGradient> gradients)
    {
        ResourceDictionary overrides = new();
        foreach ((string key, Color color) in tokens)
        {
            overrides[key] = new SolidColorBrush(color);
        }

        foreach ((string key, ThemeGradient gradient) in gradients)
        {
            overrides[key] = gradient.ToBrush();
        }

        return overrides;
    }

    /// <summary>
    ///     Re-scans <see cref="AppPaths.ThemesDirectory" /> for <c>*.json</c> drop-ins (T3): drops the previously
    ///     loaded user themes, loads the current ones (malformed files skipped, a user id colliding with a
    ///     built-in skipped to protect it; both listed in <see cref="LoadProblems" />), then raises
    ///     <see cref="Reloaded" />. No-op on WASM (no filesystem). Not called at construction: the app calls it
    ///     once at startup (before <see cref="Install" />) and again whenever the themes folder changes.
    /// </summary>
    public void Reload()
    {
        ClearUserThemes();
        LoadUserThemes();
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    // Remove every user drop-in theme (from the ordered list, the id lookup, and the merged override dicts) so a
    // reload reflects deletions/renames rather than accumulating stale variants.
    private void ClearUserThemes()
    {
        foreach (Theme user in _ordered.Where(t => t.Source == ThemeSource.User).ToList())
        {
            _ordered.Remove(user);
            _byId.Remove(user.Id);
            _customDicts.ThemeDictionaries.Remove(user.Variant);
        }

        _userDefinitions.Clear();
        _problems.Clear();
    }

    private void LoadUserThemes()
    {
        string? dir = AppPaths.ThemesDirectory;
        if (dir is null || !Directory.Exists(dir))
        {
            return; // WASM, or the directory could not be created: no drop-ins
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*.json");
        }
        catch (IOException)
        {
            return; // an inaccessible directory yields no drop-ins rather than throwing at startup
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        // Deterministic order (by filename) so the picker list is stable across reloads.
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _problems.Add(new ThemeLoadProblem(name, "It could not be read: " + e.Message));
                continue;
            }

            ThemeParseResult parsed = ThemeJson.Parse(json);
            if (parsed.Definition is not { } def)
            {
                _problems.Add(new ThemeLoadProblem(name, parsed.Error ?? "It is not a theme file."));
                continue; // malformed: skip, keep the rest
            }

            // A user file must not shadow a built-in id (that would turn a native theme into a custom variant
            // and could surprise the user). A later user file with the same id as an earlier one replaces it
            // (last write wins), and the earlier file is reported.
            if (def.Id.StartsWith(PreviewId, StringComparison.OrdinalIgnoreCase))
            {
                _problems.Add(new ThemeLoadProblem(name,
                    $"Its id \"{def.Id}\" is reserved for the editor's preview. Give it a different id."));
                continue;
            }

            if (_byId.TryGetValue(def.Id, out Theme? existing))
            {
                if (existing.Source == ThemeSource.BuiltIn)
                {
                    _problems.Add(new ThemeLoadProblem(name,
                        $"Its id \"{def.Id}\" belongs to the built-in {existing.DisplayName} theme, so it was not loaded. Give it a different id."));
                    continue;
                }

                _problems.Add(new ThemeLoadProblem(Path.GetFileName(existing.FilePath ?? existing.Id),
                    $"{name} has the same id \"{def.Id}\" and was loaded instead."));
            }

            RegisterCore(def, ThemeSource.User, file, parsed.BadValues);
        }
    }

    /// <summary>
    ///     Merges the custom-theme override dictionaries into the app so their variants resolve. Added AFTER the
    ///     base palette: merged dictionaries are searched last-first, so a custom variant's own token wins,
    ///     and anything it omits falls through to the base palette via the variant's InheritVariant. Call once.
    /// </summary>
    public void Install(Application app)
    {
        if (_installed)
        {
            return;
        }

        app.Resources.MergedDictionaries.Add(_customDicts);
        _installed = true;
    }

    /// <summary>Removes the merged override dictionaries (test cleanup / teardown).</summary>
    public void Uninstall(Application app)
    {
        if (_installed)
        {
            app.Resources.MergedDictionaries.Remove(_customDicts);
            _installed = false;
        }
    }

    /// <summary>The <see cref="ThemeVariant" /> for a theme id, or <c>Default</c> (System) for an unknown id.</summary>
    public ThemeVariant VariantFor(string? id) =>
        id is not null && _byId.TryGetValue(id, out Theme? t) ? t.Variant : ThemeVariant.Default;

    /// <summary>True when <paramref name="id" /> is a known theme.</summary>
    public bool Contains(string? id) => id is not null && _byId.ContainsKey(id);

    /// <summary>The theme registered under <paramref name="id" />, or null.</summary>
    public Theme? Find(string? id) => id is not null && _byId.TryGetValue(id, out Theme? t) ? t : null;

    /// <summary>
    ///     The complete palette for <paramref name="id" />: a built-in's own, or a user theme filled out from its
    ///     base. Null for <c>system</c> and unknown ids.
    /// </summary>
    public ThemePalette? PaletteFor(string? id)
    {
        if (id is null || !_byId.TryGetValue(id, out Theme? theme))
        {
            return null;
        }

        return theme.Source == ThemeSource.BuiltIn
            ? BuiltInThemes.Find(theme.Id)
            : _userDefinitions.TryGetValue(theme.Id, out ThemeDefinition? def)
                ? ThemePalette.Resolve(def)
                : null;
    }

    /// <summary>The definition a user theme's file holds, as read. Null for a built-in.</summary>
    public ThemeDefinition? UserDefinitionFor(string? id) =>
        id is not null && _userDefinitions.TryGetValue(id, out ThemeDefinition? def) ? def : null;

    // ── Live preview ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The preview variant while a preview is running, else null.</summary>
    public ThemeVariant? PreviewVariant { get; private set; }

    /// <summary>
    ///     Starts (or restarts) the editor preview with every token of <paramref name="palette" />, and returns the
    ///     variant to show it under. A restart is needed only when the base changes; a token edit goes through
    ///     <see cref="UpdatePreview(string, Color)" />.
    /// </summary>
    public ThemeVariant BeginPreview(ThemePalette palette)
    {
        if (PreviewVariant is { } old)
        {
            _customDicts.ThemeDictionaries.Remove(old);
        }

        // A fresh key per start: replacing a theme dictionary under the same key does not make a scope already
        // on that variant re-resolve, and the base may have changed; a new variant does both.
        ThemeVariant variant = new($"{PreviewId}-{++_previewSerial}", palette.BaseVariant);
        _previewDict = BuildDictionary(palette.Colors, palette.Gradients);
        PreviewVariant = variant;
        _customDicts.ThemeDictionaries[variant] = _previewDict;
        return variant;
    }

    /// <summary>Recolours one preview token in place, so everything showing it repaints without a re-resolve.</summary>
    public void UpdatePreview(string token, Color color)
    {
        if (_previewDict is null)
        {
            return;
        }

        if (_previewDict.TryGetValue(token, out object? existing) && existing is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            _previewDict[token] = new SolidColorBrush(color);
        }
    }

    /// <summary>Replaces one preview gradient.</summary>
    public void UpdatePreview(string token, ThemeGradient gradient)
    {
        if (_previewDict is not null)
        {
            _previewDict[token] = gradient.ToBrush();
        }
    }

    /// <summary>Drops the preview variant. Whoever pointed the app at it must point it back first.</summary>
    public void EndPreview()
    {
        if (PreviewVariant is { } variant)
        {
            _customDicts.ThemeDictionaries.Remove(variant);
        }

        _previewDict = null;
        PreviewVariant = null;
    }
}
