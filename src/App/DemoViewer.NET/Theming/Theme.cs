#region

using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>Where a theme came from: a shipped built-in or a user drop-in JSON file.</summary>
public enum ThemeSource
{
    /// <summary>Shipped with the app.</summary>
    BuiltIn,

    /// <summary>Loaded from the user's <c>&lt;config&gt;/themes/</c> directory.</summary>
    User
}

/// <summary>
///     One selectable theme in the central theme system. The built-ins
///     <c>dark</c> / <c>light</c> / <c>system</c> map to Avalonia's native <see cref="ThemeVariant" />s (their
///     tokens live in <c>DarkPalette.axaml</c>'s ThemeDictionaries). A custom theme (High-Contrast, E-Girl, or a
///     user drop-in) declares a <b>base</b> variant it inherits (Light or Dark) plus a set of token overrides;
///     the <see cref="ThemeRegistry" /> registers it as a custom <c>ThemeVariant(Id, base)</c> so its overrides
///     win and everything it omits falls through to the base: including all FluentTheme base-control colours.
/// </summary>
public sealed record Theme
{
    /// <summary>Stable id, persisted in <c>settings.json</c> (e.g. <c>"dark"</c>, <c>"egirl"</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name shown in the Settings theme picker.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The variant to set as <c>RequestedThemeVariant</c>: native (Dark/Light/Default) or a custom one.</summary>
    public required ThemeVariant Variant { get; init; }

    /// <summary>Built-in vs a user drop-in.</summary>
    public ThemeSource Source { get; init; } = ThemeSource.BuiltIn;

    /// <summary>True for the shipped themes, which the user can copy but never change.</summary>
    public bool IsBuiltIn => Source == ThemeSource.BuiltIn;

    /// <summary>
    ///     True when the theme has a palette to copy: everything but <c>system</c>, which only follows the OS
    ///     between Dark and Light.
    /// </summary>
    public bool CanCustomize { get; init; } = true;

    /// <summary>The file a user theme was loaded from. Null for a built-in.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    ///     Tokens a user theme does not set, in palette order. Each one shows its base palette's value. Always
    ///     empty for a built-in: those are complete by construction.
    /// </summary>
    public IReadOnlyList<string> MissingTokens { get; init; } = [];

    /// <summary>Keys in the file this app version does not use. Kept in the file, never applied.</summary>
    public IReadOnlyList<string> UnknownKeys { get; init; } = [];

    /// <summary>Known tokens whose value could not be read, so they fall back to the base too.</summary>
    public IReadOnlyList<string> BadValues { get; init; } = [];
}

/// <summary>A theme file the registry could not offer, and why, for the Settings and editor lists.</summary>
/// <param name="FileName">The file's name inside the themes folder.</param>
/// <param name="Reason">Why it was skipped, in words for the UI.</param>
public sealed record ThemeLoadProblem(string FileName, string Reason);
