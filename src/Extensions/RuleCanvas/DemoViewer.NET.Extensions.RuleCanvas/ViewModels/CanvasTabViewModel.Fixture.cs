#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Freeze to fixture: the node menu writes the selected instance's track as a comment block above the
///     entry, through the session like every other write, so Undo takes it back and the file is re-read.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    /// <summary>The date a fixture is stamped with. Today, unless a test pins it.</summary>
    public Func<DateOnly> Today { get; set; } = static () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>
    ///     Writes <paramref name="node" />'s value in the run as a fixture comment above its entry, or says why
    ///     nothing was written: the file is not writable here, there is no run, or the node has no track.
    /// </summary>
    public void FreezeFixture(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!IsEditable)
        {
            Notify(IsUserRuleset ? "Save or revert the YAML tab before freezing a fixture." : "A shipped ruleset is read only here; Edit a copy first.");
            return;
        }

        if (LiveRun is not { } run || SelectedInstance is not { } instance)
        {
            Notify("No run yet: evaluate the demo before freezing a fixture.");
            return;
        }

        if (TrackFor(run, node.Key, instance.Index) is not { } track)
        {
            Notify($"{node.Title} has no data in the run; nothing to freeze.");
            return;
        }

        string demo = Path.GetFileNameWithoutExtension(run.DemoPath);
        IReadOnlyList<string> lines = LiveValuesText.FixtureLines(track, instance, demo.Length > 0 ? demo : run.DemoPath, node.ValueType == "bool", Today());
        Apply(CanvasEditSemantics.FreezeFixture(node, lines));
    }
}
