#region

using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Transfer;
using DemoViewer.NET.ViewModels.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Transfer;

/// <summary>
///     Opens the export and import wizards from Settings. The exporter and importer are resolved when a wizard
///     opens, so a host without them (nothing registered yet) opens nothing rather than failing at startup.
/// </summary>
public sealed class DataTransferLauncher(
    IWindowService windows,
    Func<ITransferExporter?> exporter,
    Func<ITransferImporter?> importer,
    Func<IReadOnlyList<DemoEntry>> libraryDemos,
    Func<IReadOnlyList<string>> libraryFolders,
    Func<int>? demosInFlight = null,
    Action<string>? openFolder = null,
    Func<IReadOnlyList<string>>? libraryDataExtensions = null,
    Func<IReadOnlyList<ContributedLibraryFilter>>? contributedFilters = null)
{
    /// <summary>False on the browser head, which has no file system to export from or import into.</summary>
    public static bool IsSupported => !OperatingSystem.IsBrowser();

    /// <summary>Opens the export wizard, or brings the open one forward.</summary>
    public void OpenExport()
    {
        if (exporter() is not { } service)
        {
            return;
        }

        windows.ShowExportWizard(() =>
            new ExportWizardViewModel(service, libraryDemos, demosInFlight: demosInFlight, openFolder: openFolder,
                libraryDataExtensions: libraryDataExtensions?.Invoke(),
                contributedFilters: contributedFilters?.Invoke()));
    }

    /// <summary>Opens the import wizard, or brings the open one forward.</summary>
    public void OpenImport()
    {
        if (importer() is not { } service)
        {
            return;
        }

        windows.ShowImportWizard(() =>
            new ImportWizardViewModel(service, libraryFolders, demosInFlight: demosInFlight, openFolder: openFolder));
    }
}
