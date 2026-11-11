#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Transfer;

#endregion

namespace DemoViewer.NET.Views.Transfer;

// The only code-behind is the storage-provider handoff for the folder picker, which needs the visual tree.
/// <summary>Export wizard view.</summary>
public partial class ExportWizardView : UserControl
{
    /// <summary>Initializes a new <see cref="ExportWizardView" /> instance.</summary>
    public ExportWizardView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is TransferWizardViewModel vm)
        {
            vm.SetStorageProvider(TopLevel.GetTopLevel(this)?.StorageProvider);
        }
    }
}
