namespace DemoViewer.NET.LiveSync.Tests;

/// <summary>
///     <see cref="ServoLogic.Decide" />: the drift servo bends speed AROUND the user's base speed.
///     Under the "timescale-set" capability DV's speed is mirrored to CS2's timescale, so a servo
///     that returned absolute ~1x values would leave DV and CS2 running at different rates.
/// </summary>
[Category("Unit")]
public class ServoLogicTests
{
    [Test]
    public async Task AtNaturalSpeed_BandsAreUnchanged()
    {
        await Assert.That(ServoLogic.Decide(4, false)).IsEqualTo((ServoLogic.Correction.None, 1.0));
        await Assert.That(ServoLogic.Decide(4, true)).IsEqualTo((ServoLogic.Correction.RestoreSpeed, 1.0));
        await Assert.That(ServoLogic.Decide(64, false)).IsEqualTo((ServoLogic.Correction.AdjustSpeed, 1.25));
        await Assert.That(ServoLogic.Decide(500, false)).IsEqualTo((ServoLogic.Correction.HardResync, 1.0));
    }

    [Test]
    public async Task AtAMirroredBaseSpeed_EveryCorrectionStaysRelativeToIt()
    {
        // DV and CS2 both at 2x: bending must stay around 2x, and leaving the servo (restore or
        // hard resync) must return to 2x, never to an absolute 1x CS2 is not running at.
        await Assert.That(ServoLogic.Decide(64, false, 2.0)).IsEqualTo((ServoLogic.Correction.AdjustSpeed, 2.5));
        await Assert.That(ServoLogic.Decide(-64, false, 2.0)).IsEqualTo((ServoLogic.Correction.AdjustSpeed, 1.5));
        await Assert.That(ServoLogic.Decide(4, true, 2.0)).IsEqualTo((ServoLogic.Correction.RestoreSpeed, 2.0));
        await Assert.That(ServoLogic.Decide(500, false, 2.0)).IsEqualTo((ServoLogic.Correction.HardResync, 2.0));

        // The clamp scales with the base too: 0.75x..1.5x of it.
        await Assert.That(ServoLogic.Decide(128, false, 2.0).Speed).IsEqualTo(3.0);
        await Assert.That(ServoLogic.Decide(-128, false, 2.0).Speed).IsEqualTo(1.5);
    }
}
