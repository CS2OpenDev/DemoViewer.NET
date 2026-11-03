#region

using System.Collections.Frozen;
using Avalonia.Media;
using Avalonia.Styling;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>
///     A complete, immutable theme: a value for every token in <see cref="ThemeTokens.All" />. A file sets only
///     some tokens; <see cref="Resolve" /> fills the rest from the base variant's built-in palette and records
///     which ones it filled, so the editor and the missing-token warning can say so.
/// </summary>
public sealed class ThemePalette
{
    public ThemePalette(
        string id, string displayName, ThemeVariant baseVariant,
        IReadOnlyDictionary<string, Color> colors, IReadOnlyDictionary<string, ThemeGradient> gradients,
        IEnumerable<string> inherited, IReadOnlyDictionary<string, string>? unknownTokens = null)
    {
        Id = id;
        DisplayName = displayName;
        BaseVariant = baseVariant;
        Colors = colors.ToFrozenDictionary(StringComparer.Ordinal);
        Gradients = gradients.ToFrozenDictionary(StringComparer.Ordinal);
        Inherited = inherited.ToFrozenSet(StringComparer.Ordinal);
        UnknownTokens = (unknownTokens ?? new Dictionary<string, string>()).ToFrozenDictionary(StringComparer.Ordinal);
    }

    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>Light or Dark: the palette omitted tokens come from, and the look of the stock controls.</summary>
    public ThemeVariant BaseVariant { get; }

    /// <summary>Every solid token's colour.</summary>
    public IReadOnlyDictionary<string, Color> Colors { get; }

    /// <summary>Every gradient token's value.</summary>
    public IReadOnlyDictionary<string, ThemeGradient> Gradients { get; }

    /// <summary>Tokens taken from the base palette because the theme does not set them.</summary>
    public IReadOnlySet<string> Inherited { get; }

    /// <summary>Keys the theme carries that this app version does not use, with their raw JSON.</summary>
    public IReadOnlyDictionary<string, string> UnknownTokens { get; }

    /// <summary>
    ///     Fills <paramref name="def" /> out to every token from its base variant's built-in palette. A token the
    ///     base itself lacks (it never should) is left out, so a theme still loads.
    /// </summary>
    public static ThemePalette Resolve(ThemeDefinition def)
    {
        ThemePalette basePalette = BuiltInThemes.BaseFor(def.BaseVariant);
        Dictionary<string, Color> colors = new(StringComparer.Ordinal);
        Dictionary<string, ThemeGradient> gradients = new(StringComparer.Ordinal);
        List<string> inherited = [];
        foreach (string token in ThemeTokens.All)
        {
            if (def.Tokens.TryGetValue(token, out Color c))
            {
                colors[token] = c;
            }
            else if (def.Gradients.TryGetValue(token, out ThemeGradient? g))
            {
                gradients[token] = g;
            }
            else
            {
                inherited.Add(token);
                if (basePalette.Colors.TryGetValue(token, out Color bc))
                {
                    colors[token] = bc;
                }
                else if (basePalette.Gradients.TryGetValue(token, out ThemeGradient? bg))
                {
                    gradients[token] = bg;
                }
            }
        }

        return new ThemePalette(def.Id, def.DisplayName, def.BaseVariant, colors, gradients, inherited,
            def.UnknownTokens);
    }

    /// <summary>
    ///     The theme as a file definition that sets every token, so nothing depends on the base any more. Used for
    ///     a customized copy and for saving an edit.
    /// </summary>
    public ThemeDefinition ToCompleteDefinition(string? id = null, string? displayName = null) =>
        new(id ?? Id, displayName ?? DisplayName, BaseVariant, Colors)
        {
            Gradients = Gradients, UnknownTokens = UnknownTokens
        };

    /// <summary>True when <paramref name="token" /> has the same value here as in <paramref name="other" />.</summary>
    public bool SameValue(string token, ThemePalette other) =>
        Colors.TryGetValue(token, out Color a)
            ? other.Colors.TryGetValue(token, out Color b) && a == b
            : Gradients.TryGetValue(token, out ThemeGradient? ga)
              && other.Gradients.TryGetValue(token, out ThemeGradient? gb) && ga.Equals(gb);
}
