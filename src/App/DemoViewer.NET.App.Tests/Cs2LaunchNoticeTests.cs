#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.LiveSync;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The VAC consent strip is skipped only when no real CS2 launches. The engine treats a blank mock path as
///     no mock, so the notice has to as well, or a whitespace path skips consent and then launches real CS2.
/// </summary>
public class Cs2LaunchNoticeTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\t")]
    public async Task BlankMockPath_IsNotAcknowledged(string? path)
    {
        LiveSyncSettings settings = new()
        {
            ExternalMockServerPath = path
        };

        await Assert.That(settings.UsesMock()).IsFalse();
        await Assert.That(Cs2LaunchNotice.IsAcknowledged(settings)).IsFalse();
    }

    [Test]
    public async Task MockOrAcknowledged_SkipsTheStrip()
    {
        await Assert.That(Cs2LaunchNotice.IsAcknowledged(new LiveSyncSettings
        {
            ExternalMockServerPath = "C:\\mock\\mock_server.exe"
        })).IsTrue();
        await Assert.That(Cs2LaunchNotice.IsAcknowledged(new LiveSyncSettings
        {
            MockMode = true
        })).IsTrue();
        await Assert.That(Cs2LaunchNotice.IsAcknowledged(new LiveSyncSettings
        {
            LaunchNoticeAcknowledged = true
        })).IsTrue();
    }
}
