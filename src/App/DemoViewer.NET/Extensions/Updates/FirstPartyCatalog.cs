namespace DemoViewer.NET.Extensions.Updates;

/// <summary>
///     The first-party extensions a build can install although it does not ship them. Literals, not type
///     references: the app assembly references no extension. Each id's feed is
///     <see cref="ExtensionFeedSource.Resolve" /> over the same template the updater uses.
/// </summary>
public static class FirstPartyCatalog
{
    /// <summary>Every installable first-party id. A shipped id in this list is listed by nothing, safe mode included: its status row covers it.</summary>
    public static IReadOnlyList<string> Ids { get; } = ["net.demoviewer.pack.stratbook", "net.demoviewer.pack.rulecanvas"];

    /// <summary>
    ///     The name Settings shows for <paramref name="id" /> until a feed or a copy on disk gives one, so an id
    ///     with nothing published yet is not listed by its reverse-DNS id. The id itself for any other.
    /// </summary>
    public static string NameOf(string id) => id switch
    {
        "net.demoviewer.pack.stratbook" => "Strat Book",
        "net.demoviewer.pack.rulecanvas" => "Rule Canvas",
        _ => id
    };
}
