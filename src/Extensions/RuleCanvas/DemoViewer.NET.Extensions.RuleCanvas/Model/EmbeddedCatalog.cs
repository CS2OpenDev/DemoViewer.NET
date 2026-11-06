#region

using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>The engine's embedded catalog, read once per process; every composition and every facet form shares it.</summary>
public static class EmbeddedCatalog
{
    private static readonly Lazy<CatalogRoot> _root = new(CatalogResource.Load);
    private static readonly Lazy<CatalogScopeAdapter> _adapter = new(() => CatalogScopeAdapter.From(_root.Value));

    public static CatalogRoot Root => _root.Value;

    public static CatalogScopeAdapter Adapter => _adapter.Value;
}
