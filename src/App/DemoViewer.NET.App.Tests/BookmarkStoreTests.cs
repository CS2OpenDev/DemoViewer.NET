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
}
