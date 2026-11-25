#region

using DemoViewer.NET.Debugging;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The parser-breakpoint check runs on the entity seek's pool thread while the debugger panel edits
///     the breakpoint list on the UI thread.
/// </summary>
public class DebuggerServiceTests
{
    /// <summary>
    ///     A toggle in the panel is a RemoveAt and an Insert on the live collection; a check enumerating
    ///     that collection mid-replay threw "Collection was modified" out of the seek, which then reported
    ///     it could not seek the entity state.
    /// </summary>
    [Test]
    public async Task CheckParserState_SurvivesTheListBeingEditedMeanwhile()
    {
        DebuggerService service = new();
        service.Add(BreakpointKind.PacketIndex, -1); // never matches, so every check walks the whole list
        for (int i = 0; i < 8; i++)
        {
            service.Add(BreakpointKind.FrameNumber, i);
        }

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
        Exception? thrown = null;
        Thread checker = new(() =>
        {
            try
            {
                for (int packet = 0; !stop.IsCancellationRequested; packet++)
                {
                    service.CheckParserState(packet, false, 0, packet);
                }
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });
        checker.Start();

        while (!stop.IsCancellationRequested && checker.IsAlive)
        {
            Breakpoint bp = service.Breakpoints[^1];
            service.Breakpoints.RemoveAt(service.Breakpoints.Count - 1);
            service.Breakpoints.Insert(service.Breakpoints.Count, bp);
        }

        checker.Join();
        await Assert.That(thrown).IsNull();
    }

    [Test]
    public async Task CheckParserState_SeesABreakpointAddedBeforeIt_AndNotOneRemoved()
    {
        DebuggerService service = new();
        service.Add(BreakpointKind.PacketIndex, 7);

        service.CheckParserState(7, false, 0, 42);
        using (Assert.Multiple())
        {
            await Assert.That(service.LastHit?.Kind).IsEqualTo(BreakpointKind.PacketIndex);
            await Assert.That(service.LastHitFrameIndex).IsEqualTo(42);
        }

        service.Continue();
        service.Remove(service.Breakpoints[0].Id);
        service.CheckParserState(7, false, 0, 43);
        await Assert.That(service.LastHit).IsNull();
    }
}
