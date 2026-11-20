#region

using DemoViewer.NET.Debugging;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Persistence for Analysis-graph breakpoints, and the one-way carry of the pre-v2 file.
///     <para>
///         The carry exists because a pre-v2 record names a node, and the identity change made a name
///         ambiguous in general. It is exact in this particular case: before the per-player join the
///         graph drew only the game-scope scaffolding, so every record such a file can hold refers to
///         a node that now keys as <c>g:{name}</c>. These tests pin that, and pin that the file is
///         removed afterwards so the carry runs once.
///     </para>
/// </summary>
[NotInParallel]
public sealed class GraphBreakpointStoreTests
{
    [Test]
    public async Task LegacyRecords_AreCarriedForward_AsGameScopeKeys()
    {
        using ConfigDir dir = new();
        WriteLegacy(dir, "demo-a",
            """
            {"TargetKind":"Node","NodeName":"Alive","EdgeSource":null,"EdgeDest":null,
             "EdgeLabel":null,"EdgeConditionLabel":null,"Condition":"value > 2","Enabled":true}
            """);

        GraphBreakpointStore.MigrateLegacyFile();

        IReadOnlyList<PersistedGraphBreakpoint> carried = new GraphBreakpointStore().Load("demo-a");
        await Assert.That(carried.Count).IsEqualTo(1);
        await Assert.That(carried[0].NodeKey).IsEqualTo(GraphNodeKey.ForGameScope("Alive").ToString());
        await Assert.That(carried[0].Condition).IsEqualTo("value > 2")
            .Because("the hand-authored condition is the expensive part of a breakpoint");
        await Assert.That(carried[0].Enabled).IsTrue();
    }

    [Test]
    public async Task LegacyEdgeRecords_CarryBothEndpoints()
    {
        using ConfigDir dir = new();
        WriteLegacy(dir, "demo-b",
            """
            {"TargetKind":"Edge","NodeName":null,"EdgeSource":"Root","EdgeDest":"kills",
             "EdgeLabel":"player_death","EdgeConditionLabel":"foe","Condition":null,"Enabled":false}
            """);

        GraphBreakpointStore.MigrateLegacyFile();

        PersistedGraphBreakpoint bp = new GraphBreakpointStore().Load("demo-b").Single();
        await Assert.That(bp.EdgeSourceKey).IsEqualTo(GraphNodeKey.ForGameScope("Root").ToString());
        await Assert.That(bp.EdgeDestKey).IsEqualTo(GraphNodeKey.ForGameScope("kills").ToString());
        await Assert.That(bp.EdgeLabel).IsEqualTo("player_death");
        await Assert.That(bp.EdgeConditionLabel).IsEqualTo("foe")
            .Because("the condition label is part of the edge identity, not decoration");
        await Assert.That(bp.Enabled).IsFalse()
            .Because("a disabled breakpoint stays disabled across the carry");
    }

    [Test]
    public async Task TheCarriedKeys_ParseBackAsGameScope()
    {
        using ConfigDir dir = new();
        WriteLegacy(dir, "demo-c",
            """
            {"TargetKind":"Node","NodeName":"round_team_alive","EdgeSource":null,"EdgeDest":null,
             "EdgeLabel":null,"EdgeConditionLabel":null,"Condition":null,"Enabled":true}
            """);

        GraphBreakpointStore.MigrateLegacyFile();

        string? key = new GraphBreakpointStore().Load("demo-c").Single().NodeKey;
        await Assert.That(GraphNodeKey.TryParse(key, out GraphNodeKey parsed)).IsTrue();
        await Assert.That(parsed.IsPerPlayer).IsFalse();
        await Assert.That(parsed.Name).IsEqualTo("round_team_alive");
    }

    [Test]
    public async Task TheLegacyFileIsRemoved_SoTheCarryRunsOnce()
    {
        using ConfigDir dir = new();
        WriteLegacy(dir, "demo-d",
            """
            {"TargetKind":"Node","NodeName":"Alive","EdgeSource":null,"EdgeDest":null,
             "EdgeLabel":null,"EdgeConditionLabel":null,"Condition":null,"Enabled":true}
            """);

        GraphBreakpointStore.MigrateLegacyFile();
        await Assert.That(File.Exists(AppPaths.LegacyGraphBreakpointsFile!)).IsFalse();

        // A second call is a no-op rather than a clobber.
        GraphBreakpointStore.MigrateLegacyFile();
        await Assert.That(new GraphBreakpointStore().Load("demo-d").Count).IsEqualTo(1);
    }

    [Test]
    public async Task AnExistingV2Entry_WinsOverTheLegacyOne()
    {
        using ConfigDir dir = new();
        GraphBreakpointStore store = new();
        store.Save("demo-e",
        [
            new GraphBreakpoint
            {
                TargetKind = GraphBreakpointTarget.Node, NodeKey = GraphNodeKey.ForGameScope("NewerNode").ToString()
            }
        ]);
        WriteLegacy(dir, "demo-e",
            """
            {"TargetKind":"Node","NodeName":"OlderNode","EdgeSource":null,"EdgeDest":null,
             "EdgeLabel":null,"EdgeConditionLabel":null,"Condition":null,"Enabled":true}
            """);

        GraphBreakpointStore.MigrateLegacyFile();

        PersistedGraphBreakpoint kept = new GraphBreakpointStore().Load("demo-e").Single();
        await Assert.That(kept.NodeKey).IsEqualTo(GraphNodeKey.ForGameScope("NewerNode").ToString())
            .Because("a set the user edited under the new build is not overwritten by the old file");
    }

    [Test]
    public async Task AnUnreadableLegacyFile_IsDiscardedRatherThanRetriedForever()
    {
        using ConfigDir dir = new();
        await File.WriteAllTextAsync(AppPaths.LegacyGraphBreakpointsFile!, "{ this is not json");

        GraphBreakpointStore.MigrateLegacyFile();

        await Assert.That(File.Exists(AppPaths.LegacyGraphBreakpointsFile!)).IsFalse()
            .Because("re-attempting a failing parse on every launch is worse than losing a broken file");
    }

    /// <summary>
    ///     The legacy file is the only copy of what it holds until the new file is written. A carry whose write
    ///     failed leaves it for the next launch, and only a file that cannot be parsed is given up on.
    /// </summary>
    [Test]
    public async Task ALegacyFileWhoseCarryCouldNotBeWritten_IsKeptForTheNextLaunch()
    {
        using ConfigDir dir = new();
        WriteLegacy(dir, "demo-f",
            """
            {"TargetKind":"Node","NodeName":"Alive","EdgeSource":null,"EdgeDest":null,
             "EdgeLabel":null,"EdgeConditionLabel":null,"Condition":"value > 2","Enabled":true}
            """);
        // A folder where the new file goes: nothing can be written there.
        Directory.CreateDirectory(AppPaths.GraphBreakpointsFile!);

        GraphBreakpointStore.MigrateLegacyFile();

        await Assert.That(File.Exists(AppPaths.LegacyGraphBreakpointsFile!)).IsTrue()
            .Because("nothing was carried, so the breakpoints exist nowhere else");

        Directory.Delete(AppPaths.GraphBreakpointsFile!, true);
        GraphBreakpointStore.MigrateLegacyFile();

        using (Assert.Multiple())
        {
            await Assert.That(new GraphBreakpointStore().Load("demo-f").Single().Condition).IsEqualTo("value > 2");
            await Assert.That(File.Exists(AppPaths.LegacyGraphBreakpointsFile!)).IsFalse();
        }
    }

    /// <summary>
    ///     The file holds every demo's breakpoints. One that does not read is not an empty one: a save for one
    ///     demo keeps the bytes aside rather than replacing them with that demo alone.
    /// </summary>
    [Test]
    public async Task ASaveOverAFileThatDoesNotRead_KeepsItAside()
    {
        using ConfigDir dir = new();
        string path = AppPaths.GraphBreakpointsFile!;
        const string Cut = """{"demo-other":[{"TargetKind":"Node","NodeKey":"g:Alive","Condition":"value > 2""";
        await File.WriteAllTextAsync(path, Cut);
        GraphBreakpointStore store = new(path);
        await Assert.That(store.Load("demo-other").Count).IsEqualTo(0).Because("a load is still best-effort");

        store.Save("demo-x", [Record("a").ToBreakpoint()]);

        string[] aside = Directory.GetFiles(dir.Path, Path.GetFileName(path) + ".unreadable-*");
        using (Assert.Multiple())
        {
            await Assert.That(aside.Length).IsEqualTo(1);
            await Assert.That(aside.Length == 1 ? await File.ReadAllTextAsync(aside[0]) : null).IsEqualTo(Cut);
            await Assert.That(store.Load("demo-x").Single().NodeKey).IsEqualTo("g:a");
        }
    }

    [Test]
    public async Task APerPlayerKeyWrittenBeforeTheOccurrenceCounter_LoadsAndStillResolves()
    {
        // The second identity change, and it needs no file at all. Giving a per-player key an
        // occurrence could have orphaned every breakpoint in GraphBreakpoints.v2.json; instead the
        // occurrence is omitted at zero, so what earlier builds wrote IS the first copy's form. The
        // carry is therefore the absence of a rewrite, and this is what says so.
        using ConfigDir dir = new();
        await File.WriteAllTextAsync(
            AppPaths.GraphBreakpointsFile!,
            """
            {"demo-f":[{"TargetKind":"Node","NodeKey":"p0:7:wallbang_kills","EdgeSourceKey":null,
              "EdgeDestKey":null,"EdgeLabel":null,"EdgeConditionLabel":null,
              "Condition":"value > 1","Enabled":true}]}
            """);

        PersistedGraphBreakpoint bp = new GraphBreakpointStore().Load("demo-f").Single();

        await Assert.That(bp.Condition).IsEqualTo("value > 1")
            .Because("the hand-authored condition is the expensive part of a breakpoint");
        await Assert.That(GraphNodeKey.TryParse(bp.NodeKey, out GraphNodeKey parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(GraphNodeKey.ForPlayer(0, 7, 0, "wallbang_kills"))
            .Because("an un-suffixed per-player key is the first copy, which is one of the two the "
                + "same string used to match at once");
    }

    [Test]
    public async Task Merge_ReplacesOneDemo_AndLeavesEveryOtherAlone()
    {
        Dictionary<string, List<PersistedGraphBreakpoint>> existing = new(StringComparer.Ordinal)
        {
            ["keep"] = [Record("a")], ["replace"] = [Record("b")]
        };

        Dictionary<string, List<PersistedGraphBreakpoint>> merged =
            GraphBreakpointStore.Merge(existing, "replace", [Record("c")]);

        await Assert.That(merged["keep"].Single().NodeKey).IsEqualTo("g:a");
        await Assert.That(merged["replace"].Single().NodeKey).IsEqualTo("g:c");
        await Assert.That(existing["replace"].Single().NodeKey).IsEqualTo("g:b")
            .Because("Merge is documented as pure; neither argument may be mutated");
    }

    [Test]
    public async Task Merge_WithNoEntries_RemovesTheDemoEntirely()
    {
        Dictionary<string, List<PersistedGraphBreakpoint>> existing = new(StringComparer.Ordinal)
        {
            ["gone"] = [Record("a")]
        };

        await Assert.That(GraphBreakpointStore.Merge(existing, "gone", []).ContainsKey("gone")).IsFalse()
            .Because("demos that never had a breakpoint must not accumulate keys");
    }

    private static PersistedGraphBreakpoint Record(string name) => new(
        GraphBreakpointTarget.Node, $"g:{name}", null, null, null, null, null, true);

    private static void WriteLegacy(ConfigDir dir, string demoKey, string recordJson) =>
        File.WriteAllText(
            Path.Combine(dir.Path, "GraphBreakpoints.json"),
            $$"""{"{{demoKey}}":[{{recordJson}}]}""");

    /// <summary>A throwaway app-data root, so these tests never touch the real one.</summary>
    private sealed class ConfigDir : IDisposable
    {
        private readonly string? _previous;

        public ConfigDir()
        {
            _previous = Environment.GetEnvironmentVariable(AppPaths.ConfigDirEnvVar);
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dvn-bp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, _previous);
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
