namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     Where each imported team id lands on this machine, from <see cref="TeamIdentityService.ImportTeams" />. The
///     stores imported after it file their entries under the mapped id. An id the map does not hold maps to itself:
///     a team both machines know by the same id, or one the import left out.
/// </summary>
public sealed class TeamIdMap
{
    private readonly Dictionary<Guid, Guid> _map;

    /// <param name="map">Imported id to local id; ids that map to themselves may be left out.</param>
    public TeamIdMap(IReadOnlyDictionary<Guid, Guid> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        _map = new Dictionary<Guid, Guid>(map);
    }

    /// <summary>Every id maps to itself.</summary>
    public static TeamIdMap Identity { get; } = new(new Dictionary<Guid, Guid>());

    /// <summary>The pairs whose ids differ.</summary>
    public IReadOnlyDictionary<Guid, Guid> Moved =>
        _map.Where(p => p.Key != p.Value).ToDictionary(p => p.Key, p => p.Value);

    /// <summary>The local id of an imported team.</summary>
    /// <param name="imported">The id in the bundle.</param>
    public Guid Map(Guid imported) => _map.GetValueOrDefault(imported, imported);

    /// <summary>As <see cref="Map(Guid)" />, passing null through.</summary>
    /// <param name="imported">The id in the bundle, or null.</param>
    public Guid? Map(Guid? imported) => imported is { } id ? Map(id) : null;
}
