#region

using Avalonia.Controls;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>The condition editor of one wire: bindings only, the keys and the placement are the host's.</summary>
public partial class WireEditorView : UserControl
{
    /// <summary>Builds the view.</summary>
    public WireEditorView()
    {
        InitializeComponent();
    }
}
