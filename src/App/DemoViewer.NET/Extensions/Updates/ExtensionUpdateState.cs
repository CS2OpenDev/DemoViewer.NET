#region

using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;

#endregion

namespace DemoViewer.NET.Extensions.Updates;

/// <summary>What a feed check found for one extension. One value per outcome; errors are values, not exceptions.</summary>
public enum ExtensionUpdateStatus
{
    /// <summary>The running copy's manifest could not be read, so there is nothing to compare the feed against.</summary>
    Unknown,

    /// <summary>The feed offers nothing newer than the running copy.</summary>
    UpToDate,

    /// <summary>A newer version this app can run is published (<see cref="ExtensionUpdateState.Offered" />).</summary>
    UpdateAvailable,

    /// <summary>A newer compatible copy is already staged on disk; the next start loads it (<see cref="ExtensionUpdateState.Pending" />).</summary>
    PendingRestart,

    /// <summary>Something newer is published but none of it can run on this app (<see cref="ExtensionUpdateState.Latest" />).</summary>
    NeedsNewerApp,

    /// <summary>The feed could not be fetched (offline, a server error, a timeout).</summary>
    FeedUnreachable,

    /// <summary>The feed was fetched and did not parse, or is for another extension.</summary>
    FeedInvalid,

    /// <summary>
    ///     The feed address answered 404: nothing has been published there yet, or the address is wrong. Not an
    ///     error to show, but not "up to date" either.
    /// </summary>
    NotPublished
}

/// <summary>
///     One extension's update state after a check. Settings reads it for the
///     update line under the extension's row.
/// </summary>
/// <param name="PackId">The extension id.</param>
/// <param name="Name">The extension's user-facing name, or its id when the manifest did not read.</param>
/// <param name="Installed">The running copy's version, or null when its manifest did not read.</param>
/// <param name="Source">Where the running copy came from.</param>
/// <param name="Status">The outcome.</param>
/// <param name="Offered">The highest newer version <see cref="ExtensionUpdateService.IsOffered" /> accepts; set for <see cref="ExtensionUpdateStatus.UpdateAvailable" />.</param>
/// <param name="Latest">The highest version the feed publishes, whether or not it is offered; null when the feed was not read or is empty.</param>
/// <param name="LatestCompatibility">Why <see cref="Latest" /> is not offered, when it is newer and not; null otherwise.</param>
/// <param name="Pending">A newer compatible version already staged on disk, which the next start loads; null when none.</param>
/// <param name="Error">The failure in user terms, for the two feed failures; null otherwise.</param>
/// <param name="LogDetail">The exception message behind <paramref name="Error" />, for the log only.</param>
/// <param name="CheckedAt">When the check ran.</param>
public sealed record ExtensionUpdateState(
    string PackId,
    string Name,
    SemVersion? Installed,
    PackSource Source,
    ExtensionUpdateStatus Status,
    ExtensionFeedEntry? Offered = null,
    ExtensionFeedEntry? Latest = null,
    PackCompatibility? LatestCompatibility = null,
    SemVersion? Pending = null,
    string? Error = null,
    string? LogDetail = null,
    DateTimeOffset? CheckedAt = null);

/// <summary>What <see cref="ExtensionUpdateService.DownloadAndStageAsync" /> did.</summary>
public enum StageOutcome
{
    /// <summary>The version is now under <c>extensions/&lt;id&gt;/&lt;version&gt;/</c>; the next start loads it.</summary>
    Installed,

    /// <summary>That directory already existed; nothing was changed.</summary>
    AlreadyInstalled,

    /// <summary>A check failed; nothing is staged and the staging folder is gone.</summary>
    Refused,

    /// <summary>The download was cancelled; nothing is staged.</summary>
    Cancelled
}

/// <summary>The result of one download, never thrown.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Directory">The installed version directory for the two installed outcomes; null otherwise.</param>
/// <param name="Detail">Why it was refused, in user terms, carrying at most a bare file name; null otherwise.</param>
public sealed record StageResult(
    StageOutcome Outcome,
    string? Directory,
    string? Detail = null);

/// <summary>Download progress for the Settings row and the queue item.</summary>
/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">The zip's size, from the feed.</param>
public readonly record struct ExtensionDownloadProgress(long BytesReceived, long TotalBytes);

/// <summary>What the catalogue check found for one first-party extension this build does not ship.</summary>
public enum InstallableStatus
{
    /// <summary>A version this app can run is published and nothing is on disk (<see cref="InstallableExtension.Offered" />).</summary>
    Available,

    /// <summary>A compatible copy is already under <c>extensions/&lt;id&gt;/</c>; the next start loads it (<see cref="InstallableExtension.Staged" />).</summary>
    PendingRestart,

    /// <summary>A copy is on disk and this launch refused it (<see cref="InstallableExtension.Rejected" />); Remove is the way out.</summary>
    NotLoaded,

    /// <summary>Something is published but none of it can run on this app (<see cref="InstallableExtension.Latest" />).</summary>
    NeedsNewerApp,

    /// <summary>The feed answered 404, or publishes nothing.</summary>
    NotPublished,

    /// <summary>The feed could not be fetched.</summary>
    FeedUnreachable,

    /// <summary>The feed was fetched and did not parse, or is for another extension.</summary>
    FeedInvalid
}

/// <summary>One catalogue entry after a check: what Settings lists under "Available extensions".</summary>
/// <param name="Id">The extension id.</param>
/// <param name="Name">Its name from the feed, from a copy on disk, or the id.</param>
/// <param name="Offered">The highest version <see cref="ExtensionUpdateService.IsOffered" /> accepts; set for <see cref="InstallableStatus.Available" />.</param>
/// <param name="Latest">The highest version the feed publishes, offered or not.</param>
/// <param name="LatestCompatibility">Why <see cref="Latest" /> is not offered, when it is not; null otherwise.</param>
/// <param name="Staged">The highest compatible version already under <c>extensions/&lt;id&gt;/</c>, which the next start loads.</param>
/// <param name="Rejected">Why the copy on disk did not load this launch; null when none, or when it loaded.</param>
/// <param name="Status">The outcome.</param>
/// <param name="Error">The feed failure in user terms; null otherwise.</param>
/// <param name="LogDetail">The exception message behind <paramref name="Error" />, for the log only.</param>
/// <param name="CheckedAt">When the check ran.</param>
public sealed record InstallableExtension(
    string Id,
    string Name,
    ExtensionFeedEntry? Offered,
    ExtensionFeedEntry? Latest,
    PackCompatibility? LatestCompatibility,
    SemVersion? Staged,
    LoadOutcome? Rejected,
    InstallableStatus Status,
    string? Error = null,
    string? LogDetail = null,
    DateTimeOffset? CheckedAt = null);
