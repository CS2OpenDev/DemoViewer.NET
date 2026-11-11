#region

using Avalonia.Input;
using Avalonia.Markup.Xaml;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     A menu item's shown gesture written with Ctrl, given as the platform's primary modifier: Cmd on macOS,
///     Ctrl elsewhere. <see cref="CanvasKeyMap" /> takes either, so the label is the only thing that differs.
/// </summary>
public sealed class PrimaryGesture(string gesture) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => For(gesture, OperatingSystem.IsMacOS());

    public static KeyGesture For(string gesture, bool mac)
    {
        KeyGesture parsed = KeyGesture.Parse(gesture);
        return mac && parsed.KeyModifiers.HasFlag(KeyModifiers.Control)
            ? new KeyGesture(parsed.Key, (parsed.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta)
            : parsed;
    }
}
