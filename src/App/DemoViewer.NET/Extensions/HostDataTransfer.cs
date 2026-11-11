namespace DemoViewer.NET.Extensions;

/// <summary>
///     What Library Transfer carries for one extension: the declared stores that travel and the files of its own
///     folders it named. Read-only, like <see cref="HostDataRemoval.Stores" />: it lists, it does not copy. The
///     stores are the ones <see cref="PackContributions.StoreRefusal" /> accepted, so a template that would reach
///     past the store never gets here.
/// </summary>
internal sealed class HostDataTransfer
{
    private readonly PackContributions _pack;

    /// <param name="pack">The extension's contributions.</param>
    public HostDataTransfer(PackContributions pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _pack = pack;
    }

    /// <summary>The extension's contributions, for the importer's guarded calls into its merge.</summary>
    public PackContributions Pack => _pack;

    /// <summary>
    ///     The switch an import checks before it touches the extension's data: the portability contribution's
    ///     own when it names one of the extension's features, else the master switch.
    /// </summary>
    public string FeatureId =>
        _pack.DataPortabilityContribution is { } portability
            ? _pack.Guard.OwnFeature("data portability",
                _pack.Guard.Run("data portability", () => portability.FeatureId, null))
            : _pack.Pack.FeatureId;

    /// <summary>The accepted stores that travel, in declaration order.</summary>
    public IReadOnlyList<StoreDescriptor> Stores =>
        [.. _pack.Stores.Where(s => s.Portability != StorePortability.None)];

    /// <summary>
    ///     The files of the extension's own folders that travel, with every refused one dropped and reported.
    ///     Read once: the extension's list is not asked again, so a refusal is reported once.
    /// </summary>
    public IReadOnlyList<PortableOwnPath> OwnPaths => field ??= AcceptedOwnPaths();

    private PortableOwnPath[] AcceptedOwnPaths()
    {
        if (_pack.DataPortabilityContribution is not { } portability)
        {
            return [];
        }

        List<PortableOwnPath> accepted = [];
        foreach (PortableOwnPath? own in _pack.Guard.Run("data portability", () => portability.OwnPaths?.ToArray(),
                     null) ?? [])
        {
            if (PackContributions.OwnPathRefusal(own) is { } refusal)
            {
                _pack.Guard.Report("data portability",
                    new ArgumentException($"Own path '{own?.Path}' is refused: {refusal}."));
                continue;
            }

            accepted.Add(own!);
        }

        return [.. accepted];
    }
}
