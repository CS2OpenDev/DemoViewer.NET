#region

using System.Reflection;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;

#endregion

namespace DemoViewer.NET.Extensions.Loading;

/// <summary>Which third-party extensions may load.</summary>
/// <param name="PublisherKeys">The keys whose signature makes a copy verified.</param>
/// <param name="AllowUnverified">The user's "Allow unverified and potentially dangerous extensions" setting.</param>
public sealed record ExternalTrust(IReadOnlyList<string> PublisherKeys, bool AllowUnverified);

/// <summary>What <see cref="ExternalExtensions.Resolve" /> loaded, and every copy it refused with the reason.</summary>
public sealed record ExternalResolution(IReadOnlyList<PackStatus> Loaded, IReadOnlyList<LoadOutcome> Rejected)
{
    /// <summary>Nothing installed, or nothing looked at.</summary>
    public static ExternalResolution None { get; } = new([], []);
}

/// <summary>
///     Finds and loads the extensions this build does not ship: every id under the extensions folder that is
///     not a shipped one. A third-party copy loads only when it is compatible, its signature is not broken,
///     it is verified or the user allowed unverified extensions, it loads and probes cleanly, and nothing it
///     declares collides with what is already loaded. A copy under the reserved prefix is an installed
///     first-party extension: it loads only when this app's publisher signed it, and the user's unverified
///     setting is never consulted for it. The newest such version of each id wins.
/// </summary>
public static class ExternalExtensions
{
    /// <summary>
    ///     Ids under this prefix belong to this app's publisher. A copy claiming one loads only on the strength
    ///     of a publisher signature; a third-party copy never takes one.
    /// </summary>
    public const string ReservedIdPrefix = "net.demoviewer.";

    /// <summary>The setting's label, verbatim.</summary>
    public const string AllowUnverifiedLabel = "Allow unverified and potentially dangerous extensions";

    /// <summary>How a copy's signature reads.</summary>
    public enum Trust
    {
        /// <summary>Signed by a publisher key and unchanged since.</summary>
        Verified,

        /// <summary>Unsigned, or signed by a key this app does not know.</summary>
        Unverified,

        /// <summary>A signature that does not hold: malformed, wrong, or files changed after signing.</summary>
        Invalid
    }

    /// <summary>Resolves the installed extensions under <paramref name="configRoot" /> that this build does not ship.</summary>
    /// <param name="configRoot">The config root; null (the browser) loads nothing.</param>
    /// <param name="shipped">The shipped extensions that loaded: nothing may collide with them.</param>
    /// <param name="host">What this app provides.</param>
    /// <param name="trust">The publisher keys and the user's setting.</param>
    /// <param name="shippedIds">
    ///     Every id this build ships, loaded or not; a folder of one of these is the shipped loader's to judge
    ///     and is never looked at here. Null means the ids of <paramref name="shipped" />.
    /// </param>
    public static ExternalResolution Resolve(string? configRoot, IReadOnlyList<IExtension> shipped,
        ExtensionHostInfo host,
        ExternalTrust trust, IReadOnlyList<string>? shippedIds = null)
    {
        ArgumentNullException.ThrowIfNull(shipped);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(trust);
        if (ExtensionLoader.ExtensionsDirectory(configRoot) is not { } directory || !Directory.Exists(directory))
        {
            return ExternalResolution.None;
        }

        List<PackStatus> loaded = [];
        List<LoadOutcome> rejected = [];
        try
        {
            HashSet<string> excluded = new(shippedIds ?? [.. shipped.Select(p => p.Id)], StringComparer.Ordinal);
            ExtensionLoader.Discovery discovery = ExtensionLoader.Discover(directory);
            rejected.AddRange(discovery.Rejected
                .Where(o => !excluded.Contains(IdFolderOf(directory, o.Directory)))
                .Select(o => o with { External = true }));

            List<IExtension> accepted = [.. shipped];
            foreach (IGrouping<string, ExtensionCandidate> id in discovery.Candidates
                         .Where(c => !excluded.Contains(c.Manifest.Id))
                         .GroupBy(c => c.Manifest.Id, StringComparer.Ordinal)
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                bool firstParty = id.Key.StartsWith(ReservedIdPrefix, StringComparison.Ordinal);
                foreach (ExtensionCandidate candidate in id.OrderByDescending(c => c.Manifest.Version))
                {
                    (PackStatus? status, LoadOutcome? failure) = firstParty
                        ? TryLoadFirstParty(candidate, accepted, host, trust.PublisherKeys)
                        : TryLoad(candidate, accepted, host, trust);
                    if (status is null)
                    {
                        rejected.Add(failure!);
                        continue;
                    }

                    accepted.Add(status.Pack);
                    loaded.Add(status);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            rejected.Add(new LoadOutcome(directory, null, LoadFailure.LoaderFailed, "the extension loader failed",
                $"{ex.GetType().Name}: {ex.Message}", External: true));
        }

        return new ExternalResolution(loaded, rejected);
    }

    /// <summary>
    ///     Reads a copy's signature against <paramref name="publisherKeys" />: the verdict, the user-terms
    ///     detail for an invalid signature, the signature check's own failure (null when verified) and the
    ///     log-only specific.
    /// </summary>
    public static (Trust Trust, string? Detail, SignatureFailure? Failure, string? LogDetail) Classify(
        string directory, IReadOnlyList<string> publisherKeys)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(publisherKeys);
        try
        {
            SignatureCheck check = ExtensionSignature.Verify(directory, publisherKeys);
            return check switch
            {
                { Verified: true } => (Trust.Verified, null, null, null),
                { Failure: SignatureFailure.Missing or SignatureFailure.UnknownKey } =>
                    (Trust.Unverified, null, check.Failure, check.LogDetail),
                _ => (Trust.Invalid, check.Detail, check.Failure, check.LogDetail)
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (Trust.Invalid, ex.Message, SignatureFailure.Unreadable, ex.Message);
        }
    }

    private static (PackStatus? Status, LoadOutcome? Failure) TryLoad(ExtensionCandidate candidate,
        IReadOnlyList<IExtension> accepted, ExtensionHostInfo host, ExternalTrust trust)
    {
        ExtensionManifest m = candidate.Manifest;
        PackCompatibility compatibility = PackCompatibility.Check(m, host);
        if (!compatibility.IsCompatible)
        {
            return (null, Outcome(candidate, LoadFailure.Incompatible, compatibility.Describe(m, m.Id)!));
        }

        (Trust verdict, string? detail, _, string? logDetail) = Classify(candidate.Directory, trust.PublisherKeys);
        if (verdict == Trust.Invalid)
        {
            return (null, Outcome(candidate, LoadFailure.Untrusted, detail ?? "signature invalid", logDetail));
        }

        if (verdict == Trust.Unverified && !trust.AllowUnverified)
        {
            return (null, Outcome(candidate, LoadFailure.Unverified,
                $"it is not verified; turn on \"{AllowUnverifiedLabel}\" in Settings to load it"));
        }

        return LoadAndCheck(candidate, accepted, host,
            new PackSource.External(candidate.Directory, verdict == Trust.Verified));
    }

    // A reserved id is this app's publisher's. The copy must verify against a publisher key; the user's
    // unverified setting and the developer opt-in never reach this branch. The signature is judged before
    // compatibility, so an untrusted copy is reported as untrusted, never as merely needing a newer app.
    // Each way the signature can fail is its own outcome, so Settings can say what is wrong with the copy.
    private static (PackStatus? Status, LoadOutcome? Failure) TryLoadFirstParty(ExtensionCandidate candidate,
        IReadOnlyList<IExtension> accepted, ExtensionHostInfo host, IReadOnlyList<string> publisherKeys)
    {
        ExtensionManifest m = candidate.Manifest;
        (Trust verdict, string? detail, SignatureFailure? signature, string? logDetail) =
            Classify(candidate.Directory, publisherKeys);
        if (verdict != Trust.Verified)
        {
            string reserved = $"the id '{m.Id}' is reserved for extensions signed by this app's publisher";
            return signature switch
            {
                SignatureFailure.Missing => (null,
                    Outcome(candidate, LoadFailure.ReservedId, $"{reserved}, and the copy is not signed")),
                SignatureFailure.UnknownKey => (null, Outcome(candidate, LoadFailure.ReservedForeignKey,
                    $"{reserved}, and the copy is signed by a key this app does not know", logDetail)),
                _ => (null, Outcome(candidate, LoadFailure.Untrusted, detail ?? "signature invalid", logDetail))
            };
        }

        PackCompatibility compatibility = PackCompatibility.Check(m, host);
        if (!compatibility.IsCompatible)
        {
            return (null, Outcome(candidate, LoadFailure.Incompatible, compatibility.Describe(m, m.Id)!));
        }

        return LoadAndCheck(candidate, accepted, host, new PackSource.Installed(candidate.Directory));
    }

    // The steps both branches share once the copy is trusted: load into the dependency-resolving context,
    // then the SDK, app-reference, version-skew, probe and collision checks.
    private static (PackStatus? Status, LoadOutcome? Failure) LoadAndCheck(ExtensionCandidate candidate,
        IReadOnlyList<IExtension> accepted, ExtensionHostInfo host, PackSource source)
    {
        ExtensionLoader.LoadResult result = ExtensionLoader.Load(candidate, new ExternalLoadContext(candidate));
        if (result.Failure is { } loadFailure)
        {
            return (null, loadFailure with { External = true });
        }

        IExtension pack = result.Pack!;
        Assembly assembly = pack.GetType().Assembly;
        LoadOutcome? failure = CheckSdk(candidate, assembly)
            ?? CheckAppReference(candidate, assembly)
            ?? ExtensionLoader.CheckReferences(candidate, assembly, ExtensionLoader.RunningVersion)
            ?? ExtensionLoader.Probe(candidate, pack)
            ?? Conflicts(candidate, accepted, pack);
        if (failure is not null)
        {
            return (null, failure with { External = true });
        }

        return (PackStatus.Evaluate(pack, host) with { Source = source }, null);
    }

    // The app's assembly is shared with every extension's load context, so an extension built against it would
    // bind to the live composition root, catalog and registries. Only the SDK packages are a contract.
    internal static LoadOutcome? CheckAppReference(ExtensionCandidate candidate, Assembly assembly)
    {
        string[] app =
        [
            .. new[] { typeof(App).Assembly, Assembly.GetEntryAssembly() }
                .Where(a => a is not null)
                .Select(a => a!.GetName().Name!)
        ];
        string? referenced = assembly.GetReferencedAssemblies()
            .Select(r => r.Name)
            .FirstOrDefault(n => n is not null && app.Contains(n, StringComparer.OrdinalIgnoreCase));
        return referenced is null
            ? null
            : Outcome(candidate, LoadFailure.ReferencesApp,
                $"it references the app's own {referenced}.dll; an extension builds against the SDK packages only");
    }

    // The SDK assembly version is Major.0.0.0, so a different major is a contract the host does not implement.
    private static LoadOutcome? CheckSdk(ExtensionCandidate candidate, Assembly assembly)
    {
        AssemblyName sdk = typeof(IExtension).Assembly.GetName();
        AssemblyName? built = assembly.GetReferencedAssemblies()
            .FirstOrDefault(r => string.Equals(r.Name, sdk.Name, StringComparison.Ordinal));
        if (built?.Version is not { } builtVersion || sdk.Version is not { } running
            || builtVersion.Major == running.Major)
        {
            return null;
        }

        return Outcome(candidate, LoadFailure.ReferenceMismatch,
            $"it was built against extension SDK {builtVersion.Major}.x; this app provides {running.Major}.x");
    }

    // Composes the catalogs the app builds at startup with the candidate added, so a collision refuses the
    // candidate here instead of failing the app's own composition.
    private static LoadOutcome? Conflicts(ExtensionCandidate candidate, IReadOnlyList<IExtension> accepted,
        IExtension pack)
    {
        if (accepted.Any(a => string.Equals(a.FeatureId, pack.FeatureId, StringComparison.Ordinal)))
        {
            return Outcome(candidate, LoadFailure.Conflicts, $"its feature id '{pack.FeatureId}' is already taken");
        }

        IExtension[] together = [.. accepted, pack];
        try
        {
            _ = FeatureCatalog.Build(together);
            _ = JobKindRegistry.Build(together);
            _ = CommandRegistry.Build(together);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return Outcome(candidate, LoadFailure.Conflicts, "it collides with an extension already loaded",
                ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The candidate's own Features, Commands or JobKinds getter threw.
            return Outcome(candidate, LoadFailure.ProbeFailed, "it failed while describing itself", ex.ToString());
        }
    }

    private static LoadOutcome Outcome(ExtensionCandidate candidate, LoadFailure failure, string detail,
        string? logDetail = null) =>
        new(candidate.Directory, candidate.Manifest, failure, detail, logDetail, External: true);

    // <extensions>/<id>/<version>: the id folder of a discovery outcome.
    private static string IdFolderOf(string extensionsDirectory, string path)
    {
        string relative = Path.GetRelativePath(extensionsDirectory, path);
        int separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return separator < 0 ? relative : relative[..separator];
    }
}
