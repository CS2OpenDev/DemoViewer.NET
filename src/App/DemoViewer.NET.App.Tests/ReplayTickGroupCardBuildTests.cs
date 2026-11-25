#region

using System.Reflection;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Models;
using DemoViewer.NET.ViewModels.Replay;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The tick-group card build runs off the UI thread and nothing awaits it. A throw in it (the card
///     factory, or the decode in front of it) used to fault a task nobody observed: the Parser tab sat
///     empty under a header promising N messages, and nothing was logged or shown.
/// </summary>
[NotInParallel]
public class ReplayTickGroupCardBuildTests
{
    [Test]
    public async Task AFailingCardBuild_SaysSo_RatherThanLeavingTheTabBlank() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            DemoFrame frame = new()
            {
                CommandKind = EDemoCommands.DemSyncTick,
                FrameNumber = 0,
                ServerTick = 5,
                HeaderLength = 0,
                RawLength = 0,
                RawStart = 0,
                IsCompressed = false
            };
            // The message list is the engine's internal backing; a decoded frame is the only public way to
            // fill it, and this test needs one message and no demo.
            ((List<NetMessage>)typeof(DemoFrame)
                    .GetProperty("MessageList", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(frame)!)
                .Add(new NetMessage { MessageTypeName = "CNETMsg_SetConVar", Payload = new CNETMsg_SetConVar() });

            List<string> headers = [];
            List<bool> hasCards = [];
            ReplayTabViewModel vm = new()
            {
                ParserCardFactory = (_, _, _) => throw new InvalidOperationException("card factory failed"),
                ParserHeaderSink = headers.Add,
                ParserHasMessageCardsSink = hasCards.Add
            };

            vm.SelectedTickGroup = new TickGroup
            {
                Tick = 5, GameTick = 5, StartFrameIndex = 0, EndFrameIndex = 0, Frames = [frame]
            };

            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (headers.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            await Assert.That(headers.Count).IsEqualTo(2)
                .Because("the failure has to reach the header, not vanish into a discarded task");
            await Assert.That(headers[1]).Contains("Couldn't");
            await Assert.That(hasCards[^1]).IsFalse();
        });
}
