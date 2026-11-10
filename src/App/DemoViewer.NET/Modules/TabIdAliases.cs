#region

using System.Text.Json;
using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Modules;

/// <summary>
///     Maps a persisted tab id to the tab that carries it now. A tab renamed by its extension lists its old ids in
///     <see cref="WorkspaceTabDescriptor.PreviousTabIds" />; a session or idle snapshot written before the rename
///     names one of them. Read-side only: nothing rewrites the stored file, the next save simply records the
///     current <see cref="WorkspaceTabDescriptor.TabId" />.
/// </summary>
public static class TabIdAliases
{
    /// <summary>
    ///     The current id for <paramref name="tabId" />: itself when a descriptor carries it as its
    ///     <see cref="WorkspaceTabDescriptor.TabId" />, else the id of the descriptor that lists it as a previous
    ///     id, else itself unchanged. A live id always wins over an alias, so a new tab may reuse a retired id.
    /// </summary>
    /// <param name="tabId">The persisted id.</param>
    /// <param name="descriptors">Every descriptor the host knows, strip tabs and hosted sections alike.</param>
    public static string? Resolve(string? tabId, IEnumerable<WorkspaceTabDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        if (tabId is not { Length: > 0 })
        {
            return tabId;
        }

        WorkspaceTabDescriptor? renamed = null;
        foreach (WorkspaceTabDescriptor d in descriptors)
        {
            if (string.Equals(d.TabId, tabId, StringComparison.Ordinal))
            {
                return tabId;
            }

            if (renamed is null && d.PreviousTabIds.Contains(tabId, StringComparer.Ordinal))
            {
                renamed = d;
            }
        }

        return renamed?.TabId ?? tabId;
    }

    /// <summary>
    ///     The state <paramref name="states" /> holds for <paramref name="tab" />: under its current id, else under
    ///     the newest of its previous ids that has an entry.
    /// </summary>
    /// <param name="tab">The descriptor being restored.</param>
    /// <param name="states">The session's per-tab state, keyed by the id each tab had when it was saved.</param>
    /// <param name="state">The state found.</param>
    public static bool TryGetState(WorkspaceTabDescriptor tab, IReadOnlyDictionary<string, JsonElement> states,
        out JsonElement state)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(states);
        if (states.TryGetValue(tab.TabId, out state))
        {
            return true;
        }

        for (int i = tab.PreviousTabIds.Count - 1; i >= 0; i--)
        {
            if (states.TryGetValue(tab.PreviousTabIds[i], out state))
            {
                return true;
            }
        }

        state = default;
        return false;
    }
}
