#region

using System.Collections.Concurrent;
using System.Reflection;
using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The shell's unknown-message census listens on the engine's process-global event, which every parse
///     raises: library scans and highlight passes as well as the open's own parse. It used to keep every
///     occurrence for as long as the app ran, so a long session over a big library grew the bag without
///     bound, between opens and after the open's census had been built alike.
/// </summary>
[NotInParallel]
public class UnknownMessageAccumulatorTests
{
    [Test]
    public async Task AParseOutsideAnOpen_IsNotAccumulated() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using MainViewModel vm = new(library: TestLibraries.Empty());
            Action<UnknownMessageInfo> handler = (Action<UnknownMessageInfo>)typeof(MainViewModel)
                .GetField("_onUnknownMessageType", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm)!;
            ConcurrentBag<UnknownMessageInfo> bag = (ConcurrentBag<UnknownMessageInfo>)typeof(MainViewModel)
                .GetField("_unknownAccumulator", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm)!;

            // What a background library or highlight parse raises while no open is parsing.
            handler(new UnknownMessageInfo(12, 999, "Unknown(999)", 0, 4));

            await Assert.That(bag.IsEmpty).IsTrue()
                .Because("only the open's own parse feeds the open's census");
        });
}
