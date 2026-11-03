#region

using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>
///     One token in the theme editor: its value, whether the theme sets it (a token it does not set is
///     "missing" and shows its base's value), and the base value Reset goes back to. A solid token edits its
///     colour; a gradient edits one stop at a time, picked by <see cref="ActiveStop" />.
/// </summary>
public sealed partial class ThemeTokenRowViewModel : ObservableObject
{
    private readonly Action<ThemeTokenRowViewModel, ThemeTokenValue> _changed;
    private bool _syncingHex;

    internal ThemeTokenRowViewModel(string key, ThemeTokenValue value, bool isSet, ThemeTokenValue baseValue,
        Action<ThemeTokenRowViewModel, ThemeTokenValue> changed)
    {
        Key = key;
        ThemeTokenInfo? info = ThemeTokenCatalog.Find(key);
        Family = info?.Family;
        Description = info?.Description;
        _value = value;
        _isSet = isSet;
        BaseValue = baseValue;
        _changed = changed;
        _hex = ThemeJson.FormatColor(EditColor);
    }

    public string Key { get; }

    /// <summary>The catalogue family, e.g. "Text ramp". Null when the catalogue does not list the token.</summary>
    public string? Family { get; }

    /// <summary>What the token drives, from the catalogue. Null when it does not list it.</summary>
    public string? Description { get; }

    public bool HasDescription => Description is not null;

    /// <summary>The value the base variant's built-in palette gives this token.</summary>
    public ThemeTokenValue BaseValue { get; private set; }

    /// <summary>The current value.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditColor), nameof(SwatchBrush), nameof(CanReset), nameof(IsGradient))]
    private ThemeTokenValue _value;

    /// <summary>False while the theme does not set this token, so it shows the base value and counts as missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing), nameof(CanReset))]
    private bool _isSet;

    /// <summary>True while the theme does not set this token. Built-ins never have missing tokens.</summary>
    public bool IsMissing => !IsSet;

    /// <summary>True for the row the picker is editing.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The gradient stop the picker and hex box edit. Always 0 for a solid token.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditColor))]
    private int _activeStop;

    /// <summary>The hex box's text; a colour is applied as soon as it parses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HexIsValid))]
    private string _hex;

    public bool HexIsValid => ThemeJson.TryParseColor(Hex, out _);

    public bool IsGradient => Value.Gradient is not null;

    /// <summary>The gradient's stops, labelled for the picker. Empty for a solid token.</summary>
    public IReadOnlyList<ThemeGradientStopItem> Stops =>
        _stops ??= Value.Gradient is { } g
            ? [.. g.Stops.Select((s, i) => new ThemeGradientStopItem(i, $"Stop {i + 1}, at {s.Offset:P0}"))]
            : [];

    private IReadOnlyList<ThemeGradientStopItem>? _stops;

    /// <summary>True when the value differs from the base's.</summary>
    public bool CanReset => !Value.Equals(BaseValue);

    /// <summary>The colour the picker shows: the solid value, or the active gradient stop.</summary>
    public Color EditColor
    {
        get => Value.Gradient is { } g ? g.Stops[Math.Clamp(ActiveStop, 0, g.Stops.Count - 1)].Color : Value.Color;
        set
        {
            if (value == EditColor)
            {
                return;
            }

            Apply(Value.Gradient is { } g
                ? ThemeTokenValue.Of(g.WithStopColor(Math.Clamp(ActiveStop, 0, g.Stops.Count - 1), value))
                : ThemeTokenValue.Of(value));
        }
    }

    public IBrush SwatchBrush => Value.Gradient is { } g ? g.ToBrush() : new SolidColorBrush(Value.Color);

    /// <summary>Sets a new value as a user edit: the token becomes set, and the editor records it for undo.</summary>
    public void Apply(ThemeTokenValue value)
    {
        ThemeTokenValue old = Value;
        bool oldSet = IsSet;
        if (old.Equals(value) && oldSet)
        {
            return;
        }

        Value = value;
        IsSet = true;
        _changed(this, old with { WasSet = oldSet });
    }

    /// <summary>Puts a value back without recording an edit (undo, load).</summary>
    internal void Restore(ThemeTokenValue value, bool isSet)
    {
        Value = value;
        IsSet = isSet;
    }

    /// <summary>A new base after the theme's base variant changes: a missing token follows it.</summary>
    internal void Rebase(ThemeTokenValue baseValue)
    {
        BaseValue = baseValue;
        if (!IsSet)
        {
            Value = baseValue;
        }

        OnPropertyChanged(nameof(CanReset));
        OnPropertyChanged(nameof(BaseHex));
    }

    /// <summary>The base value as text: a hex colour, or "gradient" with its stops.</summary>
    public string BaseHex => BaseValue.Gradient is { } g
        ? "gradient " + string.Join(", ", g.Stops.Select(s => ThemeJson.FormatColor(s.Color)))
        : ThemeJson.FormatColor(BaseValue.Color);

    /// <summary>Back to the base value. The token stays set, so the file writes the base value out.</summary>
    [RelayCommand]
    private void Reset() => Apply(BaseValue);

    partial void OnValueChanged(ThemeTokenValue value) => SyncHex();

    partial void OnActiveStopChanged(int value) => SyncHex();

    private void SyncHex()
    {
        _syncingHex = true;
        try
        {
            Hex = ThemeJson.FormatColor(EditColor);
        }
        finally
        {
            _syncingHex = false;
        }
    }

    partial void OnHexChanged(string value)
    {
        if (!_syncingHex && ThemeJson.TryParseColor(value, out Color color))
        {
            EditColor = color;
        }
    }
}

/// <summary>One gradient stop the picker can switch to.</summary>
public sealed record ThemeGradientStopItem(int Index, string Label);

/// <summary>A token's value in the editor: a colour, or a gradient. <see cref="WasSet" /> rides along on undo entries.</summary>
public readonly record struct ThemeTokenValue(Color Color, ThemeGradient? Gradient, bool WasSet = true)
{
    public static ThemeTokenValue Of(Color color) => new(color, null);

    public static ThemeTokenValue Of(ThemeGradient gradient) => new(default, gradient);

    /// <summary>The palette's value for <paramref name="token" />.</summary>
    public static ThemeTokenValue From(ThemePalette palette, string token) =>
        palette.Gradients.TryGetValue(token, out ThemeGradient? g)
            ? Of(g)
            : Of(palette.Colors.GetValueOrDefault(token));

    public bool Equals(ThemeTokenValue other) =>
        Gradient is null ? other.Gradient is null && Color == other.Color : Gradient.Equals(other.Gradient);

    public override int GetHashCode() => Gradient?.GetHashCode() ?? Color.GetHashCode();
}
