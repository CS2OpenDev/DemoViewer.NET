#region

using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Modules.Library;

#endregion

namespace DemoViewer.NET.ViewModels.Transfer;

/// <summary>
///     One checkable demo row in a transfer wizard: a library demo on the export picker, or a bundle demo on the
///     import preview and the demo-files step.
/// </summary>
public sealed partial class TransferDemoRowViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>A row with no library entry behind it (an import row).</summary>
    public TransferDemoRowViewModel(string key, string displayName, string? map, long sizeBytes,
        string? status = null, bool isChecked = false)
    {
        Key = key;
        DisplayName = displayName;
        MapDisplay = DemoEntry.PrettifyMap(map);
        SizeBytes = sizeBytes;
        Status = status;
        _isChecked = isChecked;
    }

    /// <summary>A row for a library demo, keyed by its path (an export request takes a path or a sha).</summary>
    public TransferDemoRowViewModel(DemoEntry entry)
        : this(entry.FilePath, entry.FileName, entry.MapName, entry.FileSizeBytes) => Entry = entry;

    /// <summary>What the request names the demo by: a demo path on export, the sha on import.</summary>
    public string Key { get; }

    /// <summary>The file name shown on the row.</summary>
    public string DisplayName { get; }

    /// <summary>The prettified map, "Unknown" when the map is not known.</summary>
    public string MapDisplay { get; }

    /// <summary>The .dem size in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>The .dem size for display.</summary>
    public string SizeText => DemoEntry.FormatSize(SizeBytes);

    /// <summary>A short status for the row ("New", "Already here", …), or null for none.</summary>
    public string? Status { get; }

    /// <summary>True when <see cref="Status" /> has text to show.</summary>
    public bool HasStatus => !string.IsNullOrEmpty(Status);

    /// <summary>The library entry behind an export row, which the search and map filters match against.</summary>
    public DemoEntry? Entry { get; }
}
