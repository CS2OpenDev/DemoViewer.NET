#region

using DemoViewer.NET.Features;
using DemoViewer.NET.ViewModels.Tutorial;

#endregion

namespace DemoViewer.NET.Services.Tutorial;

/// <summary>
///     Decides which tour steps and notes a user sees, from the same gate the shell filters its tabs with, so
///     a step never points at a surface that is hidden for this profile. Read at each
///     <see cref="TutorialController.Start" />: a category change in Settings takes effect on the next replay.
///     <para>
///         A null gate admits every feature and every category, the same fail-open the shell's own null-gate
///         path takes (the designer and most shell tests run that way). The desktop-only rules apply either
///         way, through <see cref="ShellModuleFeatureGate.DesktopOnlyIds" /> plus each item's own
///         <see cref="ITutorialAudience.DesktopOnly" />.
///     </para>
/// </summary>
public sealed class TutorialAudienceFilter
{
    private readonly IFeatureGate? _gate;
    private readonly bool _isBrowser;

    /// <param name="gate">The shell's feature gate; null admits everything a desktop head has.</param>
    /// <param name="isBrowser">Whether the host is the WASM head. Injected so tests can take that branch.</param>
    public TutorialAudienceFilter(IFeatureGate? gate, bool isBrowser)
    {
        _gate = gate;
        _isBrowser = isBrowser;
    }

    /// <summary>Admits everything: the controller's default when the host passes no filter.</summary>
    public static TutorialAudienceFilter Everyone { get; } = new(null, false);

    /// <summary>Whether <paramref name="item" /> is for the current user.</summary>
    private bool Admits(ITutorialAudience item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_isBrowser
            && (item.DesktopOnly
                || (item.FeatureId is { } desktopId && ShellModuleFeatureGate.DesktopOnlyIds.Contains(desktopId))))
        {
            return false;
        }

        if (_gate is null)
        {
            return true;
        }

        if (item.FeatureId is { } id && !_gate.IsEnabled(id))
        {
            return false;
        }

        return item.Categories is null || item.Categories.Contains(_gate.Category);
    }

    /// <summary>
    ///     The steps this user sees, in script order, each with its admitted notes folded into the body. A step
    ///     with no admitted note is returned as the same instance.
    /// </summary>
    public IReadOnlyList<TutorialStep> Apply(IReadOnlyList<TutorialStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        List<TutorialStep> shown = [];
        foreach (TutorialStep step in steps)
        {
            if (!Admits(step))
            {
                continue;
            }

            string[] notes = [.. step.Notes.Where(Admits).Select(n => n.Text)];
            shown.Add(notes.Length == 0 ? step : step with { Body = step.Body + " " + string.Join(" ", notes) });
        }

        return shown;
    }
}
