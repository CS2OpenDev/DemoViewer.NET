#region

using System.Globalization;
using System.Reflection;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.Highlights;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     What a machine's analysis data is computed under, as a manifest records it: one identity per stamped ruleset
///     that is on, and each extension's version. Import compares a bundle's against this machine's to warn that
///     data will arrive stale.
/// </summary>
/// <param name="RulesetIdentities">One <c>id:identity</c> per stamped ruleset that is on.</param>
/// <param name="Extensions">Each loaded extension with its informational version.</param>
public sealed record TransferEnvironment(
    IReadOnlyList<string> RulesetIdentities,
    IReadOnlyList<TransferExtensionVersion> Extensions)
{
    // Identities fold durations to ticks; one fixed rate keeps two machines' lists comparable.
    private const int IdentityTickRate = 64;

    /// <summary>This machine's: the merged rules' stamped rulesets that are on, and the loaded extensions.</summary>
    /// <param name="rules">The merged rules.</param>
    /// <param name="extensions">The loaded extensions.</param>
    public static TransferEnvironment Local(MergedRulesBuild rules, IEnumerable<IExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(extensions);
        List<string> identities = [];
        foreach (string id in rules.StampedRulesets.Select(r => r.RulesetId).Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            if (rules.EnabledDoc(id) is null)
            {
                continue;
            }

            try
            {
                identities.Add(id + ":" + rules.RulesetIdentity(id, IdentityTickRate));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A ruleset that does not compose writes no data, so it has nothing to be stale against.
            }
        }

        return new TransferEnvironment(identities,
        [
            .. extensions.Select(e => new TransferExtensionVersion(e.Id,
                    e.GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion))
                .OrderBy(e => e.Id, StringComparer.Ordinal)
        ]);
    }

    /// <summary>
    ///     Null when everything <paramref name="manifest" />'s data was computed under is also in force here, else
    ///     what differs, in user terms. Rules or extensions this machine has on top are not a difference: they only
    ///     add work, they do not make the imported data stale. An extension the bundle carries only stores of is
    ///     not compared either: what the user made is not computed, so no version makes it stale.
    /// </summary>
    /// <param name="manifest">The bundle's manifest.</param>
    public string? DifferenceFrom(TransferManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        HashSet<string> here = new(RulesetIdentities, StringComparer.Ordinal);
        int rules = manifest.RulesetIdentities.Count(id => !here.Contains(id));
        Dictionary<string, string?> versions = Extensions.GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Version, StringComparer.Ordinal);
        HashSet<string> computed = new(StringComparer.Ordinal);
        foreach (TransferFile file in manifest.Demos.SelectMany(d => d.Files))
        {
            if (file.Path.Split('/') is [_, _, TransferLayout.ExtensionDirectory, var id, var facet, ..]
                && facet != TransferLayout.StoresDirectory)
            {
                computed.Add(id);
            }
        }

        List<string> extensions =
        [
            .. manifest.Extensions.Where(e => computed.Contains(e.Id)
                    && (!versions.TryGetValue(e.Id, out string? version)
                        || !string.Equals(version, e.Version, StringComparison.Ordinal)))
                .Select(e => e.Id)
        ];
        if (rules == 0 && extensions.Count == 0)
        {
            return null;
        }

        List<string> parts = [];
        if (rules > 0)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} ruleset{1} differ{2} from this machine's",
                rules, rules == 1 ? "" : "s", rules == 1 ? "s" : ""));
        }

        if (extensions.Count > 0)
        {
            parts.Add("extension versions differ (" + string.Join(", ", extensions) + ")");
        }

        return "Some of this data was computed under different rules: " + string.Join("; ", parts)
            + ". It is imported anyway, and recomputed when those demos are next processed.";
    }
}
