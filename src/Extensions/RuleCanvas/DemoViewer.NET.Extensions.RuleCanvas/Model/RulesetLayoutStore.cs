#region

using System.Text;
using System.Text.Json;
using Avalonia;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     A frame the user drew around some nodes: its title, the nodes inside it by layout key, and where it
///     sits. Layout only; the file never sees it. A group is identified by its member set, which is why
///     one node is in at most one group.
/// </summary>
/// <param name="Title">What the frame's header says.</param>
/// <param name="Members">The nodes inside, in no meaningful order; never empty in a stored layout.</param>
/// <param name="Rect">The frame in graph coordinates.</param>
public sealed record CanvasGroup(string Title, IReadOnlyList<RulesetDocumentNodeKey> Members, Rect Rect)
{
    /// <summary>Whether <paramref name="other" /> frames the same nodes, whatever its title or place.</summary>
    public bool SameMembers(CanvasGroup other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Members.Count == other.Members.Count && Members.All(other.Members.Contains);
    }

    public bool Contains(RulesetDocumentNodeKey key) => Members.Contains(key);
}

/// <summary>
///     A user's arrangement of one ruleset's canvas: where they put each node they moved, keyed by
///     the identity <see cref="RulesetDocumentNodeKey" /> gives, which nodes they folded to their header,
///     and the groups they framed. Nothing else.
///     <para>
///         <b>A node this layout does not mention is not missing and is not an error.</b> It is a
///         node nobody has dragged, and the computed MSAGL position is the right answer for it,
///         which is what <see cref="PositionOf" /> says in one line. The same holds for a whole
///         ruleset: one the editor has never opened has no sidecar at all, so auto-layout is its
///         entire answer and nothing has to be migrated to get there.
///     </para>
///     <para>
///         <b>Entries naming nodes that no longer exist are kept, not pruned.</b> Load is a bad
///         moment to prune on: the node set comes from a composition that comes back short whenever
///         the open document does not parse, so pruning against it would delete the arrangement of
///         everything an author was halfway through renaming. An entry nothing looks up costs one
///         line in a file; a discarded arrangement costs the work the sidecar exists to keep.
///     </para>
/// </summary>
public sealed class RulesetNodeLayout
{
    private readonly Dictionary<RulesetDocumentNodeKey, Point> _positions;
    private readonly HashSet<RulesetDocumentNodeKey> _collapsed;
    private readonly List<CanvasGroup> _groups;
    private readonly HashSet<string> _expanded;

    private RulesetNodeLayout(Dictionary<RulesetDocumentNodeKey, Point> positions, HashSet<RulesetDocumentNodeKey> collapsed, List<CanvasGroup> groups,
        HashSet<string> expanded)
    {
        _positions = positions;
        _collapsed = collapsed;
        _groups = groups;
        _expanded = expanded;
    }

    /// <summary>The answer for a ruleset with no sidecar, which is every ruleset until one is dragged.</summary>
    public static RulesetNodeLayout Empty { get; } = new([], [], [], new HashSet<string>(StringComparer.Ordinal));

    /// <summary>How many nodes carry a stored position, including ones the current graph does not draw.</summary>
    public int Count => _positions.Count;

    /// <summary>Whether there is nothing stored at all: no position, no collapsed node, no group, no expanded dependency.</summary>
    public bool IsEmpty => _positions.Count == 0 && _collapsed.Count == 0 && _groups.Count == 0 && _expanded.Count == 0;

    /// <summary>Every stored position, in no meaningful order.</summary>
    public IReadOnlyDictionary<RulesetDocumentNodeKey, Point> Positions => _positions;

    /// <summary>The nodes folded to their header.</summary>
    public IReadOnlySet<RulesetDocumentNodeKey> Collapsed => _collapsed;

    /// <summary>The frames drawn, in the order they were made.</summary>
    public IReadOnlyList<CanvasGroup> Groups => _groups;

    /// <summary>The <c>use:</c> dependencies drawn in full rather than folded to one node, by ruleset id.</summary>
    public IReadOnlySet<string> Expanded => _expanded;

    /// <summary>Builds a layout from the positions a canvas holds. A later duplicate of a key wins.</summary>
    /// <param name="positions">The node-to-position pairs to store.</param>
    /// <returns>The layout, or <see cref="Empty" /> when there is nothing to store.</returns>
    public static RulesetNodeLayout From(IEnumerable<KeyValuePair<RulesetDocumentNodeKey, Point>> positions) =>
        From(positions, [], []);

    /// <summary>Builds a layout from everything a canvas holds. A member of two groups stays in the later one.</summary>
    public static RulesetNodeLayout From(IEnumerable<KeyValuePair<RulesetDocumentNodeKey, Point>> positions,
        IEnumerable<RulesetDocumentNodeKey> collapsed, IEnumerable<CanvasGroup> groups) =>
        From(positions, collapsed, groups, []);

    /// <summary>Builds a layout from everything a canvas holds, the expanded dependencies included.</summary>
    public static RulesetNodeLayout From(IEnumerable<KeyValuePair<RulesetDocumentNodeKey, Point>> positions,
        IEnumerable<RulesetDocumentNodeKey> collapsed, IEnumerable<CanvasGroup> groups, IEnumerable<string> expanded)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(expanded);

        Dictionary<RulesetDocumentNodeKey, Point> map = [];
        foreach ((RulesetDocumentNodeKey key, Point at) in positions)
        {
            map[key] = at;
        }

        RulesetNodeLayout layout = new(map, [.. collapsed], [], new HashSet<string>(expanded, StringComparer.Ordinal));
        foreach (CanvasGroup group in groups)
        {
            layout = layout.WithGroup(group);
        }

        return layout.IsEmpty ? Empty : layout;
    }

    /// <summary>The stored position of <paramref name="key" />, or <c>null</c> when nobody moved it.</summary>
    /// <param name="key">The node to look up.</param>
    /// <returns>The position, or <c>null</c>.</returns>
    public Point? PositionFor(RulesetDocumentNodeKey key) =>
        _positions.TryGetValue(key, out Point at) ? at : null;

    /// <summary>
    ///     The fallback rule itself: a node the user arranged keeps their position, and every other
    ///     node keeps the one layout computed for it.
    /// </summary>
    /// <param name="key">The node being placed.</param>
    /// <param name="computed">Where auto-layout put it.</param>
    /// <returns>The position to draw at.</returns>
    public Point PositionOf(RulesetDocumentNodeKey key, Point computed) => PositionFor(key) ?? computed;

    /// <summary>Whether the user folded <paramref name="key" /> to its header.</summary>
    public bool IsCollapsed(RulesetDocumentNodeKey key) => _collapsed.Contains(key);

    /// <summary>Whether the user unfolded the dependency <paramref name="ruleset" /> into its entries.</summary>
    public bool IsExpanded(string ruleset) => _expanded.Contains(ruleset);

    /// <summary>A copy with the dependency <paramref name="ruleset" /> expanded or folded.</summary>
    public RulesetNodeLayout WithExpanded(string ruleset, bool expanded)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleset);
        if (_expanded.Contains(ruleset) == expanded)
        {
            return this;
        }

        HashSet<string> set = new(_expanded, StringComparer.Ordinal);
        if (expanded)
        {
            set.Add(ruleset);
        }
        else
        {
            set.Remove(ruleset);
        }

        RulesetNodeLayout layout = new(_positions, _collapsed, _groups, set);
        return layout.IsEmpty ? Empty : layout;
    }

    /// <summary>The group <paramref name="key" /> is inside, or null.</summary>
    public CanvasGroup? GroupOf(RulesetDocumentNodeKey key) => _groups.FirstOrDefault(g => g.Contains(key));

    /// <summary>A copy with <paramref name="key" /> at <paramref name="position" />.</summary>
    /// <param name="key">The node that moved.</param>
    /// <param name="position">Where it now sits.</param>
    /// <returns>The new layout; this one is unchanged.</returns>
    public RulesetNodeLayout With(RulesetDocumentNodeKey key, Point position)
    {
        Dictionary<RulesetDocumentNodeKey, Point> map = new(_positions)
        {
            [key] = position
        };

        return new RulesetNodeLayout(map, _collapsed, _groups, _expanded);
    }

    /// <summary>A copy with <paramref name="key" /> back on auto-layout. Its collapse flag and group stay.</summary>
    /// <param name="key">The node to forget.</param>
    /// <returns>The new layout; this one is unchanged.</returns>
    public RulesetNodeLayout Without(RulesetDocumentNodeKey key)
    {
        if (!_positions.ContainsKey(key))
        {
            return this;
        }

        Dictionary<RulesetDocumentNodeKey, Point> map = new(_positions);
        map.Remove(key);
        RulesetNodeLayout layout = new(map, _collapsed, _groups, _expanded);
        return layout.IsEmpty ? Empty : layout;
    }

    /// <summary>A copy with <paramref name="key" /> folded or unfolded.</summary>
    public RulesetNodeLayout WithCollapsed(RulesetDocumentNodeKey key, bool collapsed)
    {
        if (_collapsed.Contains(key) == collapsed)
        {
            return this;
        }

        HashSet<RulesetDocumentNodeKey> set = new(_collapsed);
        if (collapsed)
        {
            set.Add(key);
        }
        else
        {
            set.Remove(key);
        }

        RulesetNodeLayout layout = new(_positions, set, _groups, _expanded);
        return layout.IsEmpty ? Empty : layout;
    }

    /// <summary>
    ///     A copy with <paramref name="group" /> added. Its members leave any group they were in; a group
    ///     left with no member goes. A group with no members adds nothing.
    /// </summary>
    public RulesetNodeLayout WithGroup(CanvasGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        List<RulesetDocumentNodeKey> members = [.. group.Members.Distinct()];
        if (members.Count == 0)
        {
            return this;
        }

        List<CanvasGroup> groups = [];
        foreach (CanvasGroup existing in _groups)
        {
            List<RulesetDocumentNodeKey> kept = [.. existing.Members.Where(m => !members.Contains(m))];
            if (kept.Count > 0)
            {
                groups.Add(kept.Count == existing.Members.Count ? existing : existing with { Members = kept });
            }
        }

        groups.Add(group with { Members = members });
        return new RulesetNodeLayout(_positions, _collapsed, groups, _expanded);
    }

    /// <summary>A copy without the group framing <paramref name="group" />'s members; the nodes stay where they are.</summary>
    public RulesetNodeLayout WithoutGroup(CanvasGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        List<CanvasGroup> groups = [.. _groups.Where(g => !g.SameMembers(group))];
        if (groups.Count == _groups.Count)
        {
            return this;
        }

        RulesetNodeLayout layout = new(_positions, _collapsed, groups, _expanded);
        return layout.IsEmpty ? Empty : layout;
    }

    /// <summary>
    ///     A copy where everything stored under <paramref name="from" /> is stored under <paramref name="to" />:
    ///     the position, the collapse flag and the group membership. The rename of an entry is a document
    ///     edit; its arrangement is not, and this is what keeps it.
    /// </summary>
    public RulesetNodeLayout Renamed(RulesetDocumentNodeKey from, RulesetDocumentNodeKey to)
    {
        if (from == to)
        {
            return this;
        }

        Dictionary<RulesetDocumentNodeKey, Point> map = new(_positions);
        if (map.Remove(from, out Point at))
        {
            map[to] = at;
        }

        HashSet<RulesetDocumentNodeKey> set = new(_collapsed);
        if (set.Remove(from))
        {
            set.Add(to);
        }

        List<CanvasGroup> groups = [.. _groups.Select(g => g.Contains(from)
            ? g with { Members = [.. g.Members.Select(m => m == from ? to : m).Distinct()] }
            : g)];
        return new RulesetNodeLayout(map, set, groups, _expanded);
    }

    /// <summary>
    ///     This layout with <paramref name="over" />'s positions winning per node and, when <paramref name="over" />
    ///     stores anything at all, its collapse flags, groups and expanded dependencies replacing this one's: what
    ///     is on screen is the whole truth about folds, frames and unfolded dependencies, while a position missing
    ///     on screen may still be a stale entry worth keeping.
    /// </summary>
    public RulesetNodeLayout Overlaid(RulesetNodeLayout over)
    {
        ArgumentNullException.ThrowIfNull(over);
        if (over.IsEmpty)
        {
            return this;
        }

        Dictionary<RulesetDocumentNodeKey, Point> map = new(_positions);
        foreach ((RulesetDocumentNodeKey key, Point at) in over._positions)
        {
            map[key] = at;
        }

        return new RulesetNodeLayout(map, over._collapsed, over._groups, over._expanded);
    }
}

/// <summary>
///     Reads and writes the <c>&lt;name&gt;.rules.layout.json</c> sidecar that holds a ruleset's node
///     arrangement, per design.md §9 decision 9.
///     <para>
///         <b>The coordinates stay out of the YAML.</b> A ruleset is a documentation-grade file whose
///         value is substantially its prose, <c>rules/aim_rating.rules.yaml</c> opening with about
///         sixty lines of it, and a <c>layout:</c> key would make every drag a document edit that the
///         comment-preserving writer of decision 4 has to splice back in. The sidecar is purely
///         additive instead: it is written beside the ruleset, and its absence means auto-layout.
///     </para>
///     <para>
///         <b>Nothing here throws into the UI.</b> A missing sidecar, an unparseable one, a
///         half-written one, an entry naming a node that no longer exists, and a location that cannot
///         be written all resolve to "this ruleset has no stored arrangement", which is the state
///         every ruleset starts in. <see cref="Save" /> reports failure rather than raising it, which
///         is the case a shipped ruleset under a read-only install directory hits.
///     </para>
///     <para>
///         The sidecar's key spelling belongs to this file rather than to
///         <c>RulesetDocumentNodeKey.ToString</c>: the written form and the read form are one pair
///         here, so changing how a node key DISPLAYS cannot silently repoint every stored position.
///     </para>
///     <para>
///         The shape is additive over the positions-only sidecar: a node entry may carry <c>collapsed</c>
///         beside <c>x</c> and <c>y</c>, or instead of them, and a <c>groups</c> array and an <c>expanded</c>
///         array of dependency ids may follow the nodes. A reader of the old shape asks only for <c>x</c> and
///         <c>y</c>, so it places every node this writer placed and skips the rest; a layout with no fold, no
///         frame and no expanded dependency is written exactly as before.
///     </para>
/// </summary>
public static class RulesetLayoutStore
{
    /// <summary>What replaces a ruleset's <c>.yaml</c> to name its sidecar.</summary>
    public const string SidecarSuffix = ".layout.json";

    // The format's version, written but not yet branched on. It exists so a later shape change can
    // be read rather than discarded, which is most of why a sidecar is cheap to evolve.
    private const int CurrentVersion = 1;

    private const string VersionProperty = "version";
    private const string NodesProperty = "nodes";
    private const string GroupsProperty = "groups";
    private const string ExpandedProperty = "expanded";
    private const string XProperty = "x";
    private const string YProperty = "y";
    private const string WidthProperty = "width";
    private const string HeightProperty = "height";
    private const string CollapsedProperty = "collapsed";
    private const string TitleProperty = "title";
    private const string MembersProperty = "members";

    // Canvas coordinates are graph units for a node box, so two decimals is past the point of
    // visible difference. Rounding keeps a dragged node from rewriting its line as
    // 120.00000000000001, and makes two saves of one arrangement byte-identical in a diff.
    private const int Decimals = 2;

    private static readonly string[] _yamlExtensions = [".yaml", ".yml"];

    /// <summary>
    ///     Where <paramref name="rulesetPath" />'s sidecar sits: beside it, named for it. Pure string
    ///     work, so it touches no disk and never throws on a path that does not exist.
    /// </summary>
    /// <param name="rulesetPath">The ruleset's full path, normally a <c>*.rules.yaml</c>.</param>
    /// <returns>The sidecar's full path.</returns>
    public static string SidecarPathFor(string rulesetPath)
    {
        ArgumentNullException.ThrowIfNull(rulesetPath);

        foreach (string extension in _yamlExtensions)
        {
            if (rulesetPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return rulesetPath[..^extension.Length] + SidecarSuffix;
            }
        }

        return rulesetPath + SidecarSuffix;
    }

    /// <summary>
    ///     The arrangement stored for <paramref name="rulesetPath" />, or
    ///     <see cref="RulesetNodeLayout.Empty" /> when there is no sidecar, it cannot be read, or it
    ///     cannot be parsed. All three mean one thing to a canvas: lay this one out.
    /// </summary>
    /// <param name="rulesetPath">The ruleset's full path.</param>
    /// <returns>The layout.</returns>
    public static RulesetNodeLayout Load(string rulesetPath) => LoadSidecar(SidecarPathFor(rulesetPath));

    /// <summary>
    ///     Reads the arrangement at <paramref name="sidecar" /> itself, for a sidecar that does not sit beside
    ///     its ruleset: a shipped ruleset's directory is not the extension's to write, so its sidecar lives in
    ///     the extension's own folder.
    /// </summary>
    public static RulesetNodeLayout LoadSidecar(string sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        try
        {
            return File.Exists(sidecar) ? Parse(File.ReadAllText(sidecar)) : RulesetNodeLayout.Empty;
        }
        catch (IOException)
        {
            return RulesetNodeLayout.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return RulesetNodeLayout.Empty;
        }
    }

    /// <summary>
    ///     Writes <paramref name="layout" /> beside <paramref name="rulesetPath" />, replacing any
    ///     sidecar already there. A layout with nothing to store REMOVES the sidecar rather than
    ///     writing an empty one, so opening a ruleset and dragging nothing leaves no file behind.
    /// </summary>
    /// <param name="rulesetPath">The ruleset's full path.</param>
    /// <param name="layout">The arrangement to store.</param>
    /// <returns>
    ///     <c>true</c> when the sidecar now matches <paramref name="layout" />. <c>false</c> is the
    ///     answer for a location that cannot be written, which a shipped ruleset under a read-only
    ///     install directory is: the arrangement is lost and the canvas computes one next time.
    /// </returns>
    public static bool Save(string rulesetPath, RulesetNodeLayout layout) =>
        SaveSidecar(SidecarPathFor(rulesetPath), layout);

    /// <summary>As <see cref="Save" />, at <paramref name="sidecar" /> itself; see <see cref="LoadSidecar" />.</summary>
    public static bool SaveSidecar(string sidecar, RulesetNodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        ArgumentNullException.ThrowIfNull(layout);
        try
        {
            // A layout with no writable entry is the same state as no layout, and is stored the same
            // way: as the absence of a file. Otherwise a ruleset whose every coordinate came back
            // non-finite would keep a sidecar that stores nothing.
            if (!WritableNodes(layout).Any() && !WritableGroups(layout).Any() && layout.Expanded.Count == 0)
            {
                if (File.Exists(sidecar))
                {
                    File.Delete(sidecar);
                }

                return true;
            }

            string? directory = Path.GetDirectoryName(sidecar);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(sidecar, Serialize(layout));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Reads the sidecar's text. Entry-level damage is survivable and is survived: a malformed
    ///     entry costs its own node's position and no other's.
    ///     <para>
    ///         A file whose JSON does not parse at all is NOT recoverable and yields
    ///         <see cref="RulesetNodeLayout.Empty" />, which is the truncation case. That boundary is
    ///         deliberate: guessing at the tail of a half-written file would put nodes somewhere
    ///         nobody placed them, and the cost of not guessing is one auto-layout.
    ///     </para>
    /// </summary>
    /// <param name="json">The sidecar's contents.</param>
    /// <returns>The layout.</returns>
    public static RulesetNodeLayout Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return RulesetNodeLayout.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(NodesProperty, out JsonElement nodes)
                || nodes.ValueKind != JsonValueKind.Object)
            {
                return RulesetNodeLayout.Empty;
            }

            Dictionary<RulesetDocumentNodeKey, Point> map = [];
            HashSet<RulesetDocumentNodeKey> collapsed = [];
            foreach (JsonProperty entry in nodes.EnumerateObject())
            {
                // Everything an entry needs is checked before it is taken, so a bad one falls out
                // here instead of failing the read. A property this version does not know about is
                // simply not asked for, which is how an older build reads a newer sidecar.
                if (!TryReadKey(entry.Name, out RulesetDocumentNodeKey key) || entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryReadNumber(entry.Value, XProperty, out double x) && TryReadNumber(entry.Value, YProperty, out double y))
                {
                    map[key] = new Point(x, y);
                }

                if (entry.Value.TryGetProperty(CollapsedProperty, out JsonElement fold) && fold.ValueKind == JsonValueKind.True)
                {
                    collapsed.Add(key);
                }
            }

            List<CanvasGroup> groups = [];
            if (document.RootElement.TryGetProperty(GroupsProperty, out JsonElement frames) && frames.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement frame in frames.EnumerateArray())
                {
                    if (TryReadGroup(frame, out CanvasGroup? group))
                    {
                        groups.Add(group);
                    }
                }
            }

            List<string> expanded = [];
            if (document.RootElement.TryGetProperty(ExpandedProperty, out JsonElement ids) && ids.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in ids.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } ruleset)
                    {
                        expanded.Add(ruleset);
                    }
                }
            }

            return RulesetNodeLayout.From(map, collapsed, groups, expanded);
        }
        catch (JsonException)
        {
            return RulesetNodeLayout.Empty;
        }
    }

    /// <summary>
    ///     Renders <paramref name="layout" /> as the sidecar's text, keys in ordinal order so one
    ///     arrangement always produces one file and a version-controlled sidecar does not churn.
    /// </summary>
    /// <param name="layout">The arrangement to render.</param>
    /// <returns>The JSON.</returns>
    public static string Serialize(RulesetNodeLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(VersionProperty, CurrentVersion);
            writer.WriteStartObject(NodesProperty);

            foreach ((string name, Point? at, bool collapsed) in WritableNodes(layout))
            {
                writer.WriteStartObject(name);
                if (at is { } position)
                {
                    writer.WriteNumber(XProperty, Round(position.X));
                    writer.WriteNumber(YProperty, Round(position.Y));
                }

                if (collapsed)
                {
                    writer.WriteBoolean(CollapsedProperty, true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            // Absent rather than empty, so a layout without frames is the old shape byte for byte.
            List<(string Title, List<string> Members, Rect Rect)> groups = [.. WritableGroups(layout)];
            if (groups.Count > 0)
            {
                writer.WriteStartArray(GroupsProperty);
                foreach ((string title, List<string> members, Rect rect) in groups)
                {
                    writer.WriteStartObject();
                    writer.WriteString(TitleProperty, title);
                    writer.WriteStartArray(MembersProperty);
                    foreach (string member in members)
                    {
                        writer.WriteStringValue(member);
                    }

                    writer.WriteEndArray();
                    writer.WriteNumber(XProperty, Round(rect.X));
                    writer.WriteNumber(YProperty, Round(rect.Y));
                    writer.WriteNumber(WidthProperty, Round(rect.Width));
                    writer.WriteNumber(HeightProperty, Round(rect.Height));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            // Absent rather than empty, as the groups are.
            if (layout.Expanded.Count > 0)
            {
                writer.WriteStartArray(ExpandedProperty);
                foreach (string ruleset in layout.Expanded.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(ruleset);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static double Round(double value) => Math.Round(value, Decimals, MidpointRounding.AwayFromZero);

    /// <summary>
    ///     The node entries that can be written at all, sorted: a finite position, a collapse flag, or both.
    ///     A non-finite coordinate is dropped here rather than at the writer, which rejects one by throwing,
    ///     and a node placed at NaN is nowhere anyway.
    /// </summary>
    private static IEnumerable<(string Name, Point? At, bool Collapsed)> WritableNodes(RulesetNodeLayout layout)
    {
        Dictionary<string, (Point? At, bool Collapsed)> entries = new(StringComparer.Ordinal);
        foreach ((RulesetDocumentNodeKey key, Point at) in layout.Positions)
        {
            string name = FormatKey(key);
            if (name.Length > 0 && double.IsFinite(at.X) && double.IsFinite(at.Y))
            {
                entries[name] = (at, layout.IsCollapsed(key));
            }
        }

        foreach (RulesetDocumentNodeKey key in layout.Collapsed)
        {
            string name = FormatKey(key);
            if (name.Length > 0 && !entries.ContainsKey(name))
            {
                entries[name] = (null, true);
            }
        }

        return entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value.At, e.Value.Collapsed));
    }

    /// <summary>The groups that can be written: a finite frame around at least one writable key, in stored order.</summary>
    private static IEnumerable<(string Title, List<string> Members, Rect Rect)> WritableGroups(RulesetNodeLayout layout)
    {
        foreach (CanvasGroup group in layout.Groups)
        {
            Rect rect = group.Rect;
            if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) || !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height))
            {
                continue;
            }

            List<string> members = [.. group.Members.Select(FormatKey).Where(m => m.Length > 0).Distinct().Order(StringComparer.Ordinal)];
            if (members.Count > 0)
            {
                yield return (group.Title, members, rect);
            }
        }
    }

    /// <summary>
    ///     The qualified <c>ruleset.id</c> spelling, or empty for a key that cannot round-trip. A
    ///     ruleset id carrying a dot is the only such case, and it cannot arise: that id is what an
    ///     author writes before the dot in a cross-ruleset read, so a dot inside it would be
    ///     ambiguous to the engine long before it reached here.
    /// </summary>
    private static string FormatKey(RulesetDocumentNodeKey key) =>
        string.IsNullOrEmpty(key.Ruleset) || string.IsNullOrEmpty(key.Id)
        || key.Ruleset.Contains('.', StringComparison.Ordinal)
            ? string.Empty
            : key.Ruleset + "." + key.Id;

    /// <summary>Splits a stored key at its first dot, which is the inverse of <see cref="FormatKey" />.</summary>
    private static bool TryReadKey(string name, out RulesetDocumentNodeKey key)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && dot < name.Length - 1)
        {
            key = new RulesetDocumentNodeKey(name[..dot], name[(dot + 1)..]);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>
    ///     Reads one number, refusing anything that is not finite. <c>TryGetDouble</c> is what rejects a
    ///     value too large to represent, which is the shape an overflowed or hand-edited coordinate arrives in.
    /// </summary>
    private static bool TryReadNumber(JsonElement entry, string property, out double value)
    {
        value = 0;
        return entry.ValueKind == JsonValueKind.Object
               && entry.TryGetProperty(property, out JsonElement number)
               && number.ValueKind == JsonValueKind.Number
               && number.TryGetDouble(out value)
               && double.IsFinite(value);
    }

    // A frame needs its rectangle and at least one readable member; the title may be missing and reads as empty.
    private static bool TryReadGroup(JsonElement frame, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CanvasGroup? group)
    {
        group = null;
        if (frame.ValueKind != JsonValueKind.Object
            || !TryReadNumber(frame, XProperty, out double x) || !TryReadNumber(frame, YProperty, out double y)
            || !TryReadNumber(frame, WidthProperty, out double width) || !TryReadNumber(frame, HeightProperty, out double height)
            || width < 0 || height < 0
            || !frame.TryGetProperty(MembersProperty, out JsonElement members) || members.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<RulesetDocumentNodeKey> keys = [];
        foreach (JsonElement member in members.EnumerateArray())
        {
            if (member.ValueKind == JsonValueKind.String && TryReadKey(member.GetString() ?? "", out RulesetDocumentNodeKey key))
            {
                keys.Add(key);
            }
        }

        if (keys.Count == 0)
        {
            return false;
        }

        string title = frame.TryGetProperty(TitleProperty, out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
        group = new CanvasGroup(title, keys, new Rect(x, y, width, height));
        return true;
    }
}
