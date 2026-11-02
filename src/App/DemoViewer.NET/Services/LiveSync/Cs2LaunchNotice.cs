#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.Services.LiveSync;

/// <summary>
///     The one copy of what a CSVG launch does to the user's machine, shared by Live Sync and Reels. CSVG always
///     starts CS2 with <c>-insecure</c> (the plugin cannot load under VAC), so both paths ask before the first
///     launch, and keep <see cref="Caption" /> on screen next to the button afterwards.
/// </summary>
public static class Cs2LaunchNotice
{
    /// <summary>The confirm-strip copy shown before CS2 is launched or the install is patched.</summary>
    public const string Message =
        "Live Sync and Reels launch CS2 with -insecure. VAC is disabled for that session, so you can't join " +
        "VAC-secured servers until you restart CS2 normally. DemoViewer also installs a plugin in your CS2 " +
        "folder, patches gameinfo.gi and game/core/cfg, and backs up your CS2 settings in Steam userdata. " +
        "It restores all of these when it's done.";

    /// <summary>The standing one-liner beside the launch controls.</summary>
    public const string Caption =
        "Launches CS2 with -insecure (VAC off for that session) and temporarily modifies your CS2 install.";

    /// <summary>
    ///     True when the confirm strip can be skipped: the user chose "don't show again", or mock mode means no
    ///     real CS2 is launched at all.
    /// </summary>
    public static bool IsAcknowledged(LiveSyncSettings settings) =>
        settings.LaunchNoticeAcknowledged || settings.UsesMock();
}
