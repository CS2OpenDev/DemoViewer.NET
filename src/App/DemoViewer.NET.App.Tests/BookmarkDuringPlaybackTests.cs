#region

using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The play loop moves the controller's playhead without the shell's selected-frame fan-out (that
///     is deferred to Pause), so a bookmark taken mid-play must read the playhead, not the frame where
///     Play was pressed.
/// </summary>
[NotInParallel]
public class BookmarkDuringPlaybackTests
{
    [Test]
    public async Task Bookmark_WhilePlaying_RecordsThePlayheadFrame()
    {
        await HeadlessSession.RunOnUi(async () =>
        {
            MainViewModel vm = new(library: TestLibraries.Empty()) { HasFile = true };
            List<DemoFrame> frames = new(100);
            for (int i = 0; i < 100; i++)
            {
                frames.Add(new DemoFrame
                {
                    CommandKind = EDemoCommands.DemPacket,
                    FrameNumber = i,
                    ServerTick = i,
                    HeaderLength = 0,
                    RawLength = 0,
                    RawStart = 0,
                    IsCompressed = false
                });
            }

            foreach (DemoFrame f in frames)
            {
                vm.Frames.Add(f);
            }

            vm.ParserTab.FrameListSource = () => frames;
            vm.Playback.LoadDemo(frames, 64);
            try
            {
                // Land on frame 10 the way a discrete seek does, then let the "play loop" move the
                // playhead the way OnTimerTick does: CurrentFrameIndex only, no shell fan-out.
                vm.Playback.ApplyLightSeek!(10);
                vm.Playback.SyncPositionFromShell(10);
                vm.Playback.SyncPositionFromShell(40);

                vm.BookmarkCurrentFrameCommand.Execute(null);
                await Assert.That(vm.Bookmarks.Bookmarks.Count).IsEqualTo(1);
                await Assert.That(vm.Bookmarks.Bookmarks[0].FrameIndex).IsEqualTo(40);

                vm.Playback.SyncPositionFromShell(60);
                vm.BookmarkCurrentFrameCommand.Execute(null);
                await Assert.That(vm.Bookmarks.Bookmarks.Count).IsEqualTo(2);
                await Assert.That(vm.Bookmarks.Bookmarks.Any(b => b.FrameIndex == 60)).IsTrue();
            }
            finally
            {
                vm.Playback.Unload();
            }
        });
    }
}
