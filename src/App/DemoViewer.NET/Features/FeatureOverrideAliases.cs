#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.Features;

/// <summary>
///     Carries a feature override saved under an id the feature had before a rename
///     (<see cref="FeatureDescriptor.PreviousIds" />) over to its current id. Runs once at startup, after the
///     catalog is composed and before the gate first reads <c>Features:Overrides</c>.
///     <para>
///         The convention matches the other settings imports: the value moves and the old key is removed, so
///         <c>settings.json</c> holds one key per feature and a second run finds nothing to do. When the current id
///         already has an override it wins and the stale previous keys are just removed. Among several previous
///         ids, the newest one with a saved value is adopted.
///     </para>
/// </summary>
public static class FeatureOverrideAliases
{
    /// <summary>
    ///     Rewrites <paramref name="overrides" /> in place for every renamed feature in
    ///     <paramref name="descriptors" />. Returns whether anything changed.
    /// </summary>
    /// <param name="overrides">The saved overrides, keyed by feature id.</param>
    /// <param name="descriptors">The composed catalog.</param>
    public static bool Migrate(IDictionary<string, bool> overrides, IEnumerable<FeatureDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ArgumentNullException.ThrowIfNull(descriptors);
        bool changed = false;
        foreach (FeatureDescriptor d in descriptors)
        {
            // Newest first: the last rename is the value the user saw most recently.
            for (int i = d.PreviousIds.Count - 1; i >= 0; i--)
            {
                if (!overrides.TryGetValue(d.PreviousIds[i], out bool value))
                {
                    continue;
                }

                overrides.TryAdd(d.Id, value);
                overrides.Remove(d.PreviousIds[i]);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    ///     Applies <see cref="Migrate" /> to <paramref name="settings" />, writing only when a previous id holds a
    ///     value. Returns whether it wrote.
    /// </summary>
    /// <param name="settings">The app's settings file.</param>
    /// <param name="descriptors">The composed catalog.</param>
    public static bool Apply(SettingsService settings, IReadOnlyList<FeatureDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptors);

        // Probe a copy first: a launch with nothing to move must not rewrite settings.json.
        Dictionary<string, bool> probe = new(settings.Current.Features.Overrides, StringComparer.Ordinal);
        if (!Migrate(probe, descriptors))
        {
            return false;
        }

        settings.Write(s => _ = Migrate(s.Features.Overrides, descriptors));
        return true;
    }
}
