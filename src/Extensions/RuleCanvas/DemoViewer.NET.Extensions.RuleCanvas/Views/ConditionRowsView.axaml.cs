#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The expression line's editors. Bindings, plus the two things a binding cannot do: complete the
///     token at the end of the text rather than replace the whole text, and write on Enter or on focus
///     leaving an editor whose drop-down is closed.
/// </summary>
public partial class ConditionRowsView : UserControl
{
    /// <summary>Builds the view.</summary>
    public ConditionRowsView()
    {
        InitializeComponent();
    }

    /// <summary>Offers a path when it starts with the token being typed; an empty text offers every path.</summary>
    public static AutoCompleteFilterPredicate<string?> TokenFilter { get; } = (text, item) =>
    {
        if (item is null)
        {
            return false;
        }

        string token = LastToken(text ?? "");
        return token.Length == 0 ? string.IsNullOrEmpty(text) : item.StartsWith(token, StringComparison.OrdinalIgnoreCase);
    };

    /// <summary>Puts the chosen path in place of the token being typed and keeps the text before it.</summary>
    public static AutoCompleteSelector<string?> TokenSelector { get; } = (text, item) =>
    {
        string current = text ?? "";
        return current[..(current.Length - LastToken(current).Length)] + item;
    };

    // The trailing run of path characters: letters, digits, '_', '.' and '$', as the condition lexer reads a word.
    private static string LastToken(string text)
    {
        int start = text.Length;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '.' or '$'))
        {
            start--;
        }

        return text[start..];
    }

    // Enter writes once the drop-down is closed; with it open, Enter picks the highlighted path instead.
    private void OnCompletionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || sender is not AutoCompleteBox { IsDropDownOpen: false } box)
        {
            return;
        }

        switch (box.DataContext)
        {
            case CanvasConditionRowViewModel row:
                row.Commit();
                e.Handled = true;
                break;
            case CanvasExpressionViewModel line:
                line.Commit();
                e.Handled = true;
                break;
        }
    }

    private void OnRowEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CanvasConditionRowViewModel row } && sender is not AutoCompleteBox { IsDropDownOpen: true })
        {
            row.Commit();
        }
    }

    private void OnLineEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CanvasExpressionViewModel line } && sender is not AutoCompleteBox { IsDropDownOpen: true })
        {
            line.Commit();
        }
    }
}
