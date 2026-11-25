#region

using DemoViewer.NET.Models;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Common;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Frame bookmarks. A bookmark is a frame index, which means something in one demo only, so the file keeps
///     them per demo and the panel shows the open demo's.
/// </summary>
public class BookmarkStoreTests
{
    private const string DemoA = "demo-a";
    private const string DemoB = "demo-b";

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"dv-bookmarks-{Guid.NewGuid():N}", "Bookmarks.v2.json");

    private static void Cleanup(string file)
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Test]
    public async Task ABookmark_BelongsToTheDemoItWasSetOn()
    {
        string file = TempFile();
        try
        {
            BookmarksViewModel panel = new(new FrameNavigationViewModel(), new BookmarkStore(file));
            panel.SetDemo(DemoA);
            panel.Add(120345, 7700, "the clutch");

            panel.SetDemo(DemoB);
            using (Assert.Multiple())
            {
                await Assert.That(panel.Bookmarks.Count).IsEqualTo(0).Because("another demo's frame is not this one's");
                await Assert.That(panel.Selected).IsNull();
            }

            panel.Add(120345, 9100, "same frame, other match");
            panel.SetDemo(DemoA);

            using (Assert.Multiple())
            {
                await Assert.That(panel.Bookmarks.Single().Label).IsEqualTo("the clutch")
                    .Because("a bookmark on the same frame of another demo replaces nothing here");
                await Assert.That(new BookmarkStore(file).Load(DemoB).Single().Label)
                    .IsEqualTo("same frame, other match");
            }
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task SwitchingDemos_SeeksNowhere()
    {
        string file = TempFile();
        try
        {
            List<int> seeks = [];
            FrameNavigationViewModel nav = new() { SeekToFrameHandler = seeks.Add };
            BookmarksViewModel panel = new(nav, new BookmarkStore(file));
            panel.SetDemo(DemoA);
            panel.Add(40, 300, "a");
            panel.Selected = panel.Bookmarks.Single();
            seeks.Clear();

            panel.SetDemo(DemoB);
            panel.SetDemo(DemoA);

            await Assert.That(seeks.Count).IsEqualTo(0);
            await Assert.That(panel.Selected).IsNull();
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task ClickingTheSelectedBookmarkAgain_SeeksAgain()
    {
        string file = TempFile();
        try
        {
            List<int> seeks = [];
            FrameNavigationViewModel nav = new() { SeekToFrameHandler = seeks.Add };
            BookmarksViewModel panel = new(nav, new BookmarkStore(file));
            panel.SetDemo(DemoA);
            panel.Add(100, 200, "a");
            Bookmark bookmark = panel.Bookmarks.Single();

            // The list keeps the row it selected, so a second click on it changes no selection.
            panel.Selected = bookmark;
            panel.Selected = bookmark;
            panel.JumpCommand.Execute(bookmark);

            await Assert.That(seeks).IsEquivalentTo([100, 100])
                .Because("a bookmark jumped to once must be reachable again after the playhead moves on");
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task WithNoDemoOpen_NothingIsWritten()
    {
        string file = TempFile();
        try
        {
            BookmarksViewModel panel = new(new FrameNavigationViewModel(), new BookmarkStore(file));
            panel.Add(3, 30, "nowhere");

            await Assert.That(File.Exists(file)).IsFalse();
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task RemovingADemosLastBookmark_DropsItsKey_AndLeavesTheOthers()
    {
        string file = TempFile();
        try
        {
            BookmarkStore store = new(file);
            store.Save(DemoA, [new Bookmark(1, 10, "a")]);
            store.Save(DemoB, [new Bookmark(2, 20, "b")]);

            store.Save(DemoA, []);

            using (Assert.Multiple())
            {
                await Assert.That(store.Load(DemoA).Count).IsEqualTo(0);
                await Assert.That(store.Load(DemoB).Single().FrameIndex).IsEqualTo(2);
                await Assert.That(await File.ReadAllTextAsync(file)).DoesNotContain(DemoA);
            }
        }
        finally
        {
            Cleanup(file);
        }
    }

    /// <summary>
    ///     A file that does not read is not an empty one. The save that follows keeps its bytes aside instead
    ///     of replacing every demo's bookmarks with the one list in hand.
    /// </summary>
    [Test]
    public async Task ASaveOverAFileThatDoesNotRead_KeepsItAside()
    {
        string file = TempFile();
        try
        {
            const string Cut = "{\"demo-other\":[{\"FrameIndex\":12,\"Tick\":640,\"Label\":\"entry";
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, Cut);
            BookmarkStore store = new(file);
            await Assert.That(store.Load(DemoA).Count).IsEqualTo(0).Because("a load is still best-effort");

            store.Save(DemoA, [new Bookmark(1, 10, "a")]);

            string[] aside = Directory.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + ".unreadable-*");
            using (Assert.Multiple())
            {
                await Assert.That(aside.Length).IsEqualTo(1);
                await Assert.That(aside.Length == 1 ? await File.ReadAllTextAsync(aside[0]) : null).IsEqualTo(Cut);
                await Assert.That(store.Load(DemoA).Single().Label).IsEqualTo("a");
            }
        }
        finally
        {
            Cleanup(file);
        }
    }

    /// <summary>
    ///     The serializer reads a JSON null as a demo's list and as an item of one, whatever the types say. Neither
    ///     is a bookmark: the demo has none, or one fewer, and the panel never sees a null.
    /// </summary>
    [Test]
    public async Task ANullListOrANullBookmarkInTheFile_IsNotABookmark()
    {
        string file = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file,
                """{"demo-a":null,"demo-b":[null,{"FrameIndex":5,"Tick":10,"Label":"x"}]}""");
            BookmarkStore store = new(file);
            BookmarksViewModel panel = new(new FrameNavigationViewModel(), store);

            using (Assert.Multiple())
            {
                await Assert.That(store.Load(DemoA) is { Count: 0 }).IsTrue();
                await Assert.That(store.Load(DemoB).Select(b => b?.Label ?? "(null)")).IsEquivalentTo(["x"]);
                await Assert.That(() => panel.SetDemo(DemoA)).ThrowsNothing();
            }

            panel.SetDemo(DemoB);
            panel.Add(9, 20, "y");
            await Assert.That(new BookmarkStore(file).Load(DemoB).Select(b => b.Label)).IsEquivalentTo(["x", "y"]);
        }
        finally
        {
            Cleanup(file);
        }
    }

    /// <summary>
    ///     The list every demo shared before bookmarks were kept per demo cannot be matched to a demo. The panel
    ///     says so once, until dismissed, and the old file is left as it was.
    /// </summary>
    [Test]
    public async Task OlderSharedBookmarks_AreNoticedOnce_AndLeftOnDisk()
    {
        string file = TempFile();
        try
        {
            string dir = Path.GetDirectoryName(file)!;
            string legacy = Path.Combine(dir, "SessionState.json");
            const string Old =
                """[{"FrameIndex":1,"Tick":2,"Label":"a"},{"FrameIndex":3,"Tick":4,"Label":"b"}]""";
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(legacy, Old);

            BookmarksViewModel panel = new(new FrameNavigationViewModel(), new BookmarkStore(file));
            using (Assert.Multiple())
            {
                await Assert.That(panel.HasLegacyNotice).IsTrue();
                await Assert.That(panel.LegacyNotice).IsEqualTo(
                    "Bookmarks are now saved per demo; 2 older bookmarks from before this change could not be matched to a demo.");
            }

            panel.SetDemo(DemoA);
            await Assert.That(panel.HasLegacyNotice).IsTrue().Because("opening a demo is not a dismissal");
            panel.DismissLegacyNoticeCommand.Execute(null);

            BookmarksViewModel next = new(new FrameNavigationViewModel(), new BookmarkStore(file));
            using (Assert.Multiple())
            {
                await Assert.That(panel.HasLegacyNotice).IsFalse();
                await Assert.That(next.HasLegacyNotice).IsFalse().Because("a dismissal is remembered");
                await Assert.That(await File.ReadAllTextAsync(legacy)).IsEqualTo(Old);
            }
        }
        finally
        {
            Cleanup(file);
        }
    }

    [Test]
    public async Task WithNoOlderBookmarks_ThereIsNoNotice()
    {
        string file = TempFile();
        try
        {
            string dir = Path.GetDirectoryName(file)!;
            BookmarksViewModel none = new(new FrameNavigationViewModel(), new BookmarkStore(file));
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, "SessionState.json"), "[]");
            BookmarksViewModel empty = new(new FrameNavigationViewModel(), new BookmarkStore(file));
            await File.WriteAllTextAsync(Path.Combine(dir, "SessionState.json"), "[{\"FrameIndex\"");
            BookmarksViewModel unreadable = new(new FrameNavigationViewModel(), new BookmarkStore(file));

            using (Assert.Multiple())
            {
                await Assert.That(none.HasLegacyNotice).IsFalse();
                await Assert.That(empty.HasLegacyNotice).IsFalse();
                await Assert.That(unreadable.HasLegacyNotice).IsFalse();
            }
        }
        finally
        {
            Cleanup(file);
        }
    }
}
