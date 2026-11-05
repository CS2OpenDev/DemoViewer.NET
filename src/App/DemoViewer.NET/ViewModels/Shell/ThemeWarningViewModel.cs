#region

using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.ViewModels.Shell;

/// <summary>
///     The shell's notice for a selected theme that is missing colours. Those colours already fall back to the
///     base palette, so nothing is broken; the notice says so and offers the editor, filtered to just those
///     tokens. Dismissing it records which tokens were missing, so it stays quiet on later launches until that
///     set changes (a token added to the app, or a file edited).
/// </summary>
public sealed partial class ThemeWarningViewModel : ObservableObject
{
    private readonly Func<string?> _dismissed;
    private readonly Action<string> _dismiss;
    private readonly Action<string, bool> _openEditor;
    private readonly ThemeRegistry _registry;
    private readonly Func<string?> _selectedId;
    private string? _signature;
    private string? _themeId;

    /// <param name="registry">The theme catalogue.</param>
    /// <param name="selectedId">The selected theme id from settings.</param>
    /// <param name="dismissed">The signature the user last dismissed, from settings.</param>
    /// <param name="dismiss">Saves a dismissed signature.</param>
    /// <param name="openEditor">Opens Theme Customization on a theme id, "Missing only" on when the flag is set.</param>
    public ThemeWarningViewModel(ThemeRegistry registry, Func<string?> selectedId, Func<string?> dismissed,
        Action<string> dismiss, Action<string, bool> openEditor)
    {
        _registry = registry;
        _selectedId = selectedId;
        _dismissed = dismissed;
        _dismiss = dismiss;
        _openEditor = openEditor;
        Reevaluate();
    }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private string _message = "";

    /// <summary>Re-reads the selected theme. Call after a theme reload or a settings change.</summary>
    public void Reevaluate()
    {
        Theme? theme = _registry.Find(_selectedId());
        if (theme is not { IsBuiltIn: false, MissingTokens.Count: > 0 })
        {
            _themeId = null;
            _signature = null;
            IsVisible = false;
            return;
        }

        _themeId = theme.Id;
        _signature = Signature(theme);
        int n = theme.MissingTokens.Count;
        string baseName = theme.Variant.InheritVariant?.Key as string ?? "base";
        Message = n == 1
            ? $"Your theme \"{theme.DisplayName}\" is missing 1 colour, so it uses the {baseName} default for now. Open Theme Customization to fill it in."
            : $"Your theme \"{theme.DisplayName}\" is missing {n} colours, so they use the {baseName} defaults for now. Open Theme Customization to fill them in.";
        IsVisible = !string.Equals(_dismissed(), _signature, StringComparison.Ordinal);
    }

    /// <summary>The theme id and a hash of exactly which tokens are missing.</summary>
    private static string Signature(Theme theme)
    {
        byte[] hash =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", theme.MissingTokens.Order(StringComparer.Ordinal))));
        return theme.Id + "|" + Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Opens the editor on the theme with "Missing only" applied.</summary>
    [RelayCommand]
    private void Open()
    {
        if (_themeId is not null)
        {
            _openEditor(_themeId, true);
        }
    }

    /// <summary>Hides the notice until the set of missing tokens changes.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (_signature is not null)
        {
            _dismiss(_signature);
        }

        IsVisible = false;
    }
}
