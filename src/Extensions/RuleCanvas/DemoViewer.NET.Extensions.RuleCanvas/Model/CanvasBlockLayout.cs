#region

using Avalonia;
using Item = DemoViewer.NET.Extensions.RuleCanvas.Model.CanvasPlacement.Item;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     The placement of a big component, in event blocks. The shared gates, context values and the events
///     that only drive them stand once in a column at the left. Then one block per event or define that
///     fires stats: a big block's head at the foot of a lane, the block's own enrichments and context flags
///     in that lane at the row of the first stat that reads them, and the stats in columns beside the lane,
///     split when they outgrow the target height; consecutive blocks of one or two stats share a column
///     with each head in the lane beside its stat. A block follows the blocks it reads from, and among the
///     rest the one with fewer wires leaving it goes first. Derived stats (computes, flags with a when,
///     tallies) follow in their own columns, highlights and the show and params surfaces last.
///     <para>
///         A big block's head wires climb from the lane's foot, so a wire that enters a column at some height
///         crosses the head's wires to the stats above that height and none below: the stats wired beyond
///         the block (a shared gate, a lane scaffold, another block, a surface) sit at the top of their
///         column, the most connected first, and the stats the head alone writes fill the bottom. The target
///         height is the smallest that lands the extent at or under the target aspect.
///     </para>
/// </summary>
public static class CanvasBlockLayout
{
    private const double RowGap = 40;
    private const double LaneGap = 60;
    private const double BlockGutter = 160;
    private const double LaneExtra = 100;
    private const double ScaffoldGap = 8;
    private const int SmallBlock = 2;
    private const int ImprovePasses = 6;
    private const double SlideStep = 150;
    private const double TargetAspect = 1.8;
    private const double MinTarget = 1400;
    private const double MaxTarget = 12000;
    private const double TargetStep = 200;

    /// <summary>Lays out one component; positions are relative to the component's own top-left.</summary>
    public static (Dictionary<RulesetDocumentNodeKey, Point> Local, Size Box) LayOut(IReadOnlyList<Item> items, IReadOnlyList<RulesetDocumentEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(edges);
        Plan plan = new(items, edges);
        Dictionary<RulesetDocumentNodeKey, Point> local = plan.Place(MinTarget).Local;
        for (double target = MinTarget + TargetStep; target <= MaxTarget; target += TargetStep)
        {
            Size box = CanvasPlacement.BoxOf(local, plan.Items);
            if (box.Height > 0 && box.Width / box.Height <= TargetAspect)
            {
                break;
            }

            local = plan.Place(target).Local;
        }

        plan.Improve(local);
        Plan.Round(local);
        return (local, CanvasPlacement.BoxOf(local, plan.Items));
    }

    private sealed class Block(Item head)
    {
        public Item Head { get; } = head;

        public List<Item> Stats { get; } = [];

        public List<Item> Scaffolds { get; } = [];
    }

    // One column of stats with the lane of scaffolds to its left; the first column's lane holds the head too.
    private sealed class Column
    {
        public List<Item> Stats { get; } = [];

        public List<(Item Item, int Anchor)> Lane { get; } = [];

        public double StatsHeight => Stats.Sum(s => s.Height + RowGap) - (Stats.Count > 0 ? RowGap : 0);
    }

    private sealed class Plan
    {
        private readonly Dictionary<RulesetDocumentNodeKey, Item> _byKey;
        private readonly Dictionary<RulesetDocumentNodeKey, List<RulesetDocumentNodeKey>> _targets = new();
        private readonly Dictionary<RulesetDocumentNodeKey, List<RulesetDocumentNodeKey>> _sources = new();
        private readonly List<Block> _blocks = [];
        private readonly List<Item> _derived = [];
        private readonly List<Item> _highlights = [];
        private readonly List<Item> _surfaces = [];
        private readonly List<Item> _shared = [];

        public IReadOnlyDictionary<RulesetDocumentNodeKey, Item> Items => _byKey;

        public Plan(IReadOnlyList<Item> items, IReadOnlyList<RulesetDocumentEdge> edges)
        {
            _byKey = items.ToDictionary(i => i.Key);
            foreach (RulesetDocumentEdge edge in edges)
            {
                if (edge.Source.Equals(edge.Target) || !_byKey.ContainsKey(edge.Source) || !_byKey.ContainsKey(edge.Target))
                {
                    continue;
                }

                Add(_targets, edge.Source, edge.Target);
                Add(_sources, edge.Target, edge.Source);
            }

            Dictionary<RulesetDocumentNodeKey, Block> byHead = new();
            Dictionary<RulesetDocumentNodeKey, int> blockOf = new();

            Block BlockFor(Item head)
            {
                if (!byHead.TryGetValue(head.Key, out Block? block))
                {
                    block = new Block(head);
                    byHead[head.Key] = block;
                    _blocks.Add(block);
                    blockOf[head.Key] = _blocks.Count - 1;
                }

                return block;
            }

            // Blocks in the order the document first names a member; a define heads its own block.
            foreach (Item item in items)
            {
                switch (item.EffectiveRole)
                {
                    case CanvasNodeRole.Define:
                        BlockFor(item);
                        break;
                    case CanvasNodeRole.Stat when HeadOf(item.Key) is { } head:
                        Block block = BlockFor(head);
                        block.Stats.Add(item);
                        blockOf[item.Key] = _blocks.IndexOf(block);
                        break;
                    case CanvasNodeRole.Stat:
                        _derived.Add(item);
                        break;
                    case CanvasNodeRole.Highlight:
                        _highlights.Add(item);
                        break;
                    case CanvasNodeRole.Surface:
                        _surfaces.Add(item);
                        break;
                }
            }

            // A feeder whose every consumer is in one block, directly or through another such feeder, is the
            // block's own; one read across blocks, or by a derived node, a highlight or a surface, is shared.
            List<Item> feeders = [.. items.Where(i => i.IsFeeder)];
            Dictionary<RulesetDocumentNodeKey, int> feederBlock = new();
            for (int round = 0; round < 4 && feeders.Count > 0; round++)
            {
                List<Item> deferred = [];
                foreach (Item feeder in feeders)
                {
                    int? decided = Locality(feeder.Key, blockOf, feederBlock, out bool unknown);
                    if (unknown)
                    {
                        deferred.Add(feeder);
                        continue;
                    }

                    if (decided is { } index && index >= 0)
                    {
                        feederBlock[feeder.Key] = index;
                        _blocks[index].Scaffolds.Add(feeder);
                    }
                    else
                    {
                        feederBlock[feeder.Key] = -1;
                        _shared.Add(feeder);
                    }
                }

                feeders = deferred;
            }

            _shared.AddRange(feeders);
            _shared.AddRange(items.Where(i => i.EffectiveRole == CanvasNodeRole.Event && !byHead.ContainsKey(i.Key)));
            OrderBlocks(blockOf);
        }

        // A block follows every block it reads from; among the rest the one with fewer wires leaving it goes
        // first, so the blocks that feed the derived stats and the surfaces stand beside them. Document order
        // breaks ties.
        private void OrderBlocks(Dictionary<RulesetDocumentNodeKey, int> blockOf)
        {
            int count = _blocks.Count;
            int[] leaving = new int[count];
            HashSet<(int From, int To)> reads = [];
            for (int b = 0; b < count; b++)
            {
                foreach (Item member in _blocks[b].Stats.Concat(_blocks[b].Scaffolds).Append(_blocks[b].Head))
                {
                    foreach (RulesetDocumentNodeKey target in TargetsOf(member.Key))
                    {
                        if (_byKey[target].IsFeeder && !blockOf.ContainsKey(target))
                        {
                            // A wire to a shared node is the bus's business, not the block's.
                            continue;
                        }

                        int other = blockOf.TryGetValue(target, out int index) ? index : -1;
                        if (other != b)
                        {
                            leaving[b]++;
                        }

                        if (other >= 0 && other != b)
                        {
                            reads.Add((b, other));
                        }
                    }
                }
            }

            List<Block> ordered = [];
            HashSet<int> placed = [];
            while (ordered.Count < count)
            {
                int? next = null;
                for (int b = 0; b < count; b++)
                {
                    if (placed.Contains(b) || reads.Any(r => r.To == b && !placed.Contains(r.From)))
                    {
                        continue;
                    }

                    if (next is not { } best || leaving[b] < leaving[best])
                    {
                        next = b;
                    }
                }

                // A read cycle among the rest: take them in document order.
                int chosen = next ?? Enumerable.Range(0, count).First(b => !placed.Contains(b));
                placed.Add(chosen);
                ordered.Add(_blocks[chosen]);
            }

            _blocks.Clear();
            _blocks.AddRange(ordered);
        }

        private static void Add(Dictionary<RulesetDocumentNodeKey, List<RulesetDocumentNodeKey>> map, RulesetDocumentNodeKey key, RulesetDocumentNodeKey value)
        {
            if (!map.TryGetValue(key, out List<RulesetDocumentNodeKey>? list))
            {
                list = [];
                map[key] = list;
            }

            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        private List<RulesetDocumentNodeKey> TargetsOf(RulesetDocumentNodeKey key) => _targets.GetValueOrDefault(key) ?? [];

        private List<RulesetDocumentNodeKey> SourcesOf(RulesetDocumentNodeKey key) => _sources.GetValueOrDefault(key) ?? [];

        // The first event or define that fires the stat, or null for a derived one.
        private Item? HeadOf(RulesetDocumentNodeKey stat) =>
            SourcesOf(stat).Select(s => _byKey[s]).FirstOrDefault(s => s.EffectiveRole is CanvasNodeRole.Event or CanvasNodeRole.Define);

        // The one block every consumer is in, -1 for shared, null with unknown set while a consuming feeder is undecided.
        private int? Locality(RulesetDocumentNodeKey feeder, Dictionary<RulesetDocumentNodeKey, int> blockOf,
            Dictionary<RulesetDocumentNodeKey, int> feederBlock, out bool unknown)
        {
            unknown = false;
            int? block = null;
            List<RulesetDocumentNodeKey> consumers = TargetsOf(feeder);
            if (consumers.Count == 0)
            {
                return -1;
            }

            foreach (RulesetDocumentNodeKey consumer in consumers)
            {
                int index;
                if (blockOf.TryGetValue(consumer, out int statBlock))
                {
                    index = statBlock;
                }
                else if (_byKey[consumer].IsFeeder)
                {
                    if (!feederBlock.TryGetValue(consumer, out index))
                    {
                        unknown = true;
                        continue;
                    }
                }
                else
                {
                    return -1;
                }

                if (index < 0 || (block is { } seen && seen != index))
                {
                    return -1;
                }

                block = index;
            }

            return unknown ? null : block ?? -1;
        }

        public (Dictionary<RulesetDocumentNodeKey, Point> Local, Size Box) Place(double target)
        {
            Dictionary<RulesetDocumentNodeKey, Point> local = new();

            // Consecutive small blocks share one column, each head beside its first stat; a big block stands alone.
            List<List<Block>> groups = [];
            foreach (Block block in _blocks)
            {
                if (block.Stats.Count <= SmallBlock && groups.Count > 0 && groups[^1][^1].Stats.Count <= SmallBlock)
                {
                    groups[^1].Add(block);
                }
                else
                {
                    groups.Add([block]);
                }
            }

            // The shared column is placed last, once the rows it feeds are known, but it stands first.
            double x = _shared.Count == 0 ? 0 : _shared.Max(s => s.Width) + BlockGutter;
            foreach (List<Block> group in groups)
            {
                Dictionary<RulesetDocumentNodeKey, Point> placed = new();
                Size box = PlaceGroup(group, target, placed);
                foreach ((RulesetDocumentNodeKey key, Point at) in placed)
                {
                    local[key] = new Point(x + at.X, at.Y);
                }

                x += box.Width + BlockGutter;
            }

            if (_derived.Count > 0)
            {
                x += PlaceColumns(OrderByBarycenter(_derived, local, SourcesOf), target, x, local) + BlockGutter;
            }

            if (_highlights.Count > 0)
            {
                x += PlaceColumns(OrderByBarycenter(_highlights, local, SourcesOf), target, x, local) + LaneGap;
            }

            if (_surfaces.Count > 0)
            {
                List<Item> surfaces = [.. _surfaces.Where(s => s.Key.IsParams), .. _surfaces.Where(s => !s.Key.IsParams)];
                PlaceColumns(surfaces, double.MaxValue, x, local);
            }

            if (_shared.Count > 0)
            {
                double width = _shared.Max(s => s.Width);
                double y = 0;
                foreach (Item item in OrderByBarycenter(_shared, local, TargetsOf))
                {
                    local[item.Key] = new Point(width - item.Width, y);
                    y += item.Height + ScaffoldGap;
                }
            }

            return (local, CanvasPlacement.BoxOf(local, _byKey));
        }

        /// <summary>Rounds every position to whole pixels, as the canvas stores them.</summary>
        public static void Round(Dictionary<RulesetDocumentNodeKey, Point> local)
        {
            foreach ((RulesetDocumentNodeKey key, Point at) in local.ToList())
            {
                local[key] = new Point(Math.Round(at.X), Math.Round(at.Y));
            }
        }

        // Columns of at most target height, in the order given.
        private static List<List<Item>> Split(IReadOnlyList<Item> items, double target)
        {
            List<List<Item>> columns = [];
            List<Item> current = [];
            double y = 0;
            foreach (Item item in items)
            {
                if (current.Count > 0 && y + item.Height > target)
                {
                    columns.Add(current);
                    current = [];
                    y = 0;
                }

                current.Add(item);
                y += item.Height + RowGap;
            }

            if (current.Count > 0)
            {
                columns.Add(current);
            }

            return columns;
        }

        // Fills columns of at most target height from x, top-aligned; returns the width taken.
        private static double PlaceColumns(IReadOnlyList<Item> items, double target, double x, Dictionary<RulesetDocumentNodeKey, Point> local)
        {
            double columnX = x;
            foreach (List<Item> column in Split(items, target))
            {
                double top = 0;
                foreach (Item item in column)
                {
                    local[item.Key] = new Point(columnX, top);
                    top += item.Height + RowGap;
                }

                columnX += column.Max(i => i.Width) + LaneGap;
            }

            return columnX - LaneGap - x;
        }

        // One group at its own origin: a lone big block's head at the foot of the first lane, a small block's
        // head in the lane at the row of its first stat, scaffolds down each lane from the row of their first
        // reader, stats stacked beside. Returns the group's box.
        private Size PlaceGroup(List<Block> group, double target, Dictionary<RulesetDocumentNodeKey, Point> local)
        {
            bool footHead = group.Count == 1 && group[0].Stats.Count > SmallBlock;
            List<Column> columns = Columns(group, target, footHead);
            double cursor = 0, bottom = 0;
            for (int c = 0; c < columns.Count; c++)
            {
                Column column = columns[c];
                Block? foot = footHead && c == 0 ? group[0] : null;
                double laneItems = column.Lane.Count == 0 ? 0 : column.Lane.Max(l => l.Item.Width);
                double laneWidth = foot is null
                    ? laneItems
                    : column.Lane.Count == 0 ? foot.Head.Width : Math.Max(foot.Head.Width, laneItems) + LaneExtra;
                double statsX = cursor + laneWidth + (laneWidth > 0 ? LaneGap : 0);

                double y = 0;
                List<double> rows = [];
                foreach (Item stat in column.Stats)
                {
                    rows.Add(y);
                    local[stat.Key] = new Point(statsX, y);
                    y += stat.Height + RowGap;
                }

                // A lane item sits level with the foot of its first reader, under the rows where wires enter.
                double laneCursor = 0;
                foreach ((Item item, int anchor) in column.Lane.OrderBy(l => l.Anchor))
                {
                    double wish = anchor >= 0 && anchor < rows.Count ? rows[anchor] + column.Stats[anchor].Height - item.Height : laneCursor;
                    double top = Math.Max(laneCursor, wish);
                    local[item.Key] = new Point(cursor + laneWidth - item.Width, top);
                    laneCursor = top + item.Height + ScaffoldGap;
                }

                double columnBottom = Math.Max(column.StatsHeight, laneCursor);
                if (foot is not null)
                {
                    double headY = Math.Max(column.StatsHeight - foot.Head.Height, laneCursor);
                    local[foot.Head.Key] = new Point(cursor, headY);
                    columnBottom = Math.Max(columnBottom, headY + foot.Head.Height);
                }

                bottom = Math.Max(bottom, columnBottom);
                double statsWidth = column.Stats.Count == 0 ? 0 : column.Stats.Max(s => s.Width);
                cursor = statsX + statsWidth + LaneGap;
            }

            return new Size(cursor - LaneGap, bottom);
        }

        // Splits the group's stats into columns of at most target height and seats every head and scaffold
        // in the lane of its first stat or reader. A lone big block sorts each column with the stats wired
        // beyond the head first, most connected at the top; small blocks keep each block's stats together,
        // the most connected block first.
        private List<Column> Columns(List<Block> group, double target, bool footHead)
        {
            HashSet<RulesetDocumentNodeKey> heads = [.. group.Select(b => b.Head.Key)];
            List<Item> scaffolds = [.. group.SelectMany(b => b.Scaffolds)];
            HashSet<RulesetDocumentNodeKey> scaffoldKeys = [.. scaffolds.Select(s => s.Key)];
            HashSet<RulesetDocumentNodeKey> inside = [.. group.SelectMany(b => b.Stats).Select(s => s.Key), .. scaffoldKeys, .. heads];

            // Wires that leave the group: inputs from anything but a head, outputs to anything outside.
            int Foreign(Item stat) => SourcesOf(stat.Key).Count(s => !heads.Contains(s)) + TargetsOf(stat.Key).Count(t => !inside.Contains(t));

            List<Item> stats = footHead
                ? [.. group[0].Stats.OrderByDescending(Foreign)]
                : [.. group.OrderByDescending(b => b.Stats.Sum(Foreign)).SelectMany(b => b.Stats)];

            List<Column> columns = [];
            Column current = new();
            double height = 0;
            foreach (Item stat in stats)
            {
                if (current.Stats.Count > 0 && height + stat.Height > target)
                {
                    columns.Add(current);
                    current = new Column();
                    height = 0;
                }

                current.Stats.Add(stat);
                height += stat.Height + RowGap;
            }

            if (current.Stats.Count > 0 || columns.Count == 0)
            {
                columns.Add(current);
            }

            // The most connected slice stands nearest the surfaces, so its wires leave the block without crossing it.
            if (footHead)
            {
                columns.Reverse();
            }

            (int Column, int Row)? Find(RulesetDocumentNodeKey key)
            {
                for (int c = 0; c < columns.Count; c++)
                {
                    int row = columns[c].Stats.FindIndex(s => s.Key.Equals(key));
                    if (row >= 0)
                    {
                        return (c, row);
                    }
                }

                return null;
            }

            static (int Column, int Row)? First((int Column, int Row)? a, (int Column, int Row)? b) =>
                a is null ? b : b is null ? a : Before(a.Value, b.Value) ? a : b;

            if (!footHead)
            {
                foreach (Block block in group)
                {
                    (int Column, int Row)? best = null;
                    foreach (Item stat in block.Stats)
                    {
                        best = First(best, Find(stat.Key));
                    }

                    columns[best?.Column ?? 0].Lane.Add((block.Head, best?.Row ?? -1));
                }
            }

            // A scaffold goes to the lane of its first reading stat; one read only by another scaffold
            // follows that scaffold, at its row.
            Dictionary<RulesetDocumentNodeKey, (int Column, int Row)> seat = new();
            List<Item> pending = [.. scaffolds];
            for (int round = 0; round < 4 && pending.Count > 0; round++)
            {
                List<Item> deferred = [];
                foreach (Item scaffold in pending)
                {
                    (int Column, int Row)? best = null;
                    foreach (RulesetDocumentNodeKey consumer in TargetsOf(scaffold.Key))
                    {
                        (int Column, int Row)? found = Find(consumer);
                        if (found is null && scaffoldKeys.Contains(consumer) && seat.TryGetValue(consumer, out (int Column, int Row) at))
                        {
                            found = at;
                        }

                        best = First(best, found);
                    }

                    if (best is { } hit)
                    {
                        seat[scaffold.Key] = hit;
                    }
                    else
                    {
                        deferred.Add(scaffold);
                    }
                }

                pending = deferred;
            }

            foreach (Item scaffold in scaffolds)
            {
                (int column, int row) = seat.TryGetValue(scaffold.Key, out (int Column, int Row) at) ? at : (0, -1);
                columns[column].Lane.Add((scaffold, row));
            }

            return columns;
        }

        /// <summary>
        ///     Adjacent stats of one column trade places while that lowers the crossings of the wires touching
        ///     them, a few passes over every column, stopping early once a pass changes nothing; then the
        ///     shared column and each column right of the blocks slides to the height that crosses least.
        /// </summary>
        public void Improve(Dictionary<RulesetDocumentNodeKey, Point> local)
        {
            List<(RulesetDocumentNodeKey Source, RulesetDocumentNodeKey Target)> wires = [.. _targets.SelectMany(p => p.Value.Select(t => (p.Key, t)))];
            Dictionary<RulesetDocumentNodeKey, List<int>> touching = new();
            for (int i = 0; i < wires.Count; i++)
            {
                foreach (RulesetDocumentNodeKey end in new[] { wires[i].Source, wires[i].Target })
                {
                    if (!touching.TryGetValue(end, out List<int>? list))
                    {
                        list = [];
                        touching[end] = list;
                    }

                    list.Add(i);
                }
            }

            (Point From, Point To) RunOf(int w) =>
                CanvasPlacement.Run(local[wires[w].Source], _byKey[wires[w].Source], local[wires[w].Target], _byKey[wires[w].Target]);

            bool Shares(int i, int j) =>
                wires[i].Source.Equals(wires[j].Source) || wires[i].Source.Equals(wires[j].Target)
                || wires[i].Target.Equals(wires[j].Source) || wires[i].Target.Equals(wires[j].Target);

            int CrossingsOf(IEnumerable<RulesetDocumentNodeKey> nodes)
            {
                HashSet<int> mine = [.. nodes.SelectMany(n => touching.GetValueOrDefault(n) ?? [])];
                int count = 0;
                foreach (int i in mine)
                {
                    (Point From, Point To) run = RunOf(i);
                    for (int j = 0; j < wires.Count; j++)
                    {
                        if (j == i || (mine.Contains(j) && j < i) || Shares(i, j))
                        {
                            continue;
                        }

                        (Point From, Point To) other = RunOf(j);
                        if (CanvasPlacement.Crosses(run.From, run.To, other.From, other.To))
                        {
                            count++;
                        }
                    }
                }

                return count;
            }

            List<List<RulesetDocumentNodeKey>> columns = [.. local
                .Where(p => _byKey[p.Key].EffectiveRole is CanvasNodeRole.Stat or CanvasNodeRole.Define or CanvasNodeRole.Highlight)
                .GroupBy(p => p.Value.X)
                .Select(g => g.OrderBy(p => p.Value.Y).Select(p => p.Key).ToList())
                .Where(c => c.Count > 1)];

            for (int pass = 0; pass < ImprovePasses; pass++)
            {
                bool improved = false;
                foreach (List<RulesetDocumentNodeKey> column in columns)
                {
                    for (int i = 0; i + 1 < column.Count; i++)
                    {
                        RulesetDocumentNodeKey a = column[i], b = column[i + 1];
                        Point above = local[a], below = local[b];
                        int before = CrossingsOf([a, b]);
                        local[b] = above;
                        local[a] = new Point(above.X, above.Y + _byKey[b].Height + RowGap);
                        if (CrossingsOf([a, b]) < before)
                        {
                            column[i] = b;
                            column[i + 1] = a;
                            improved = true;
                        }
                        else
                        {
                            local[a] = above;
                            local[b] = below;
                        }
                    }
                }

                if (!improved)
                {
                    break;
                }
            }

            // The shared column slides as a whole, step by step down the extent, and stays at the offset
            // where the wires touching it cross least.
            double height = local.Max(p => p.Value.Y + _byKey[p.Key].Height);
            if (_shared.Count > 0)
            {
                List<RulesetDocumentNodeKey> column = [.. _shared.Select(s => s.Key)];
                double columnHeight = column.Max(k => local[k].Y + _byKey[k].Height) - column.Min(k => local[k].Y);
                double offset = 0, best = 0;
                int fewest = CrossingsOf(column);
                while (offset + SlideStep + columnHeight <= height)
                {
                    offset += SlideStep;
                    Shift(column, SlideStep);
                    int count = CrossingsOf(column);
                    if (count < fewest)
                    {
                        fewest = count;
                        best = offset;
                    }
                }

                Shift(column, best - offset);
            }

            void Shift(List<RulesetDocumentNodeKey> column, double by)
            {
                foreach (RulesetDocumentNodeKey key in column)
                {
                    local[key] = local[key] + new Vector(0, by);
                }
            }
        }

        private static bool Before((int Column, int Row) a, (int Column, int Row) b) => a.Column < b.Column || (a.Column == b.Column && a.Row < b.Row);

        // Items in the order of the mean row of their neighbours already placed; one with none keeps its place after those.
        private static List<Item> OrderByBarycenter(List<Item> items, Dictionary<RulesetDocumentNodeKey, Point> local,
            Func<RulesetDocumentNodeKey, List<RulesetDocumentNodeKey>> neighbours)
        {
            List<(Item Item, double Key, int Index)> keyed = [];
            for (int i = 0; i < items.Count; i++)
            {
                keyed.Add((items[i], Barycenter(items[i].Key, local, neighbours) ?? double.MaxValue, i));
            }

            return [.. keyed.OrderBy(k => k.Key).ThenBy(k => k.Index).Select(k => k.Item)];
        }

        private static double? Barycenter(RulesetDocumentNodeKey key, Dictionary<RulesetDocumentNodeKey, Point> local,
            Func<RulesetDocumentNodeKey, List<RulesetDocumentNodeKey>> neighbours)
        {
            double sum = 0;
            int count = 0;
            foreach (RulesetDocumentNodeKey neighbour in neighbours(key))
            {
                if (local.TryGetValue(neighbour, out Point at))
                {
                    sum += at.Y;
                    count++;
                }
            }

            return count == 0 ? null : sum / count;
        }
    }
}
