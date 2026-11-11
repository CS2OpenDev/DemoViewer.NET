#region

using Avalonia.Data.Converters;
using DemoViewer.NET.Extensions.Sdk.Review;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.Review;

/// <summary>
///     The words the Review tab shows for a clip's source. The queue stores plain ids
///     (<see cref="ReviewSources" />); a reviewer reads a label. An id this build does not know, from a
///     newer extension, shows as itself.
/// </summary>
public static class ReviewSourceLabels
{
    /// <summary>For a binding: the label of a source id, or the filter's "every source" value unchanged.</summary>
    public static readonly FuncValueConverter<string?, string> Converter = new(id => Label(id));

    /// <summary>The label for <paramref name="id" />.</summary>
    public static string Label(string? id) => id switch
    {
        null or "" => "",
        ReviewSources.ScoutingReport => "Scouting report",
        ReviewSources.Situation => "Situation",
        ReviewSources.Tag => "Tag",
        ReviewSources.Highlight => "Highlight",
        ReviewSources.Manual => "Manual",
        ReviewSources.Lineup => "Lineup",
        // A queue an older build wrote before the host migration ran.
        "dossier" => "Scouting report",
        ReviewQueueTabViewModel.AllSources => "All sources",
        _ => id
    };
}
