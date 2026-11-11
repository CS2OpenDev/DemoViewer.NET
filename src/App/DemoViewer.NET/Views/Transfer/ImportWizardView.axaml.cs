#region

using Avalonia;
using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Transfer;

#endregion

namespace DemoViewer.NET.Views.Transfer;

// The only code-behind is the storage-provider handoff for the folder pickers, which needs the visual tree.
/// <summary>Import wizard view.</summary>
public partial class ImportWizardView : UserControl
{
    /// <summary>Initializes a new <see cref="ImportWizardView" /> instance.</summary>
    public ImportWizardView()
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
