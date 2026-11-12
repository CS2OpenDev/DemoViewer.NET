namespace DemoViewer.NET.Services.Transfer;

/// <summary>What a <see cref="TransferWarning" /> is about, so the wizard can group and word it.</summary>
public enum TransferWarningKind
{
    /// <summary>The processing queue is working on demos; they may not be included (best effort).</summary>
    DemosInFlight,

    /// <summary>Demos the library has not hashed yet; their data cannot be exported.</summary>
    NotHashed,

    /// <summary>Demos selected for export whose row or files are gone.</summary>
    Missing,

    /// <summary>The bundle's rulesets or extension versions differ from this machine's; the data will be recomputed.</summary>
    StaleOnArrival,

    /// <summary>The bundle was written by another app version.</summary>
    VersionMismatch,

    /// <summary>The target folder does not exist.</summary>
    TargetMissing,

    /// <summary>The target folder is inside the app's data or a library folder.</summary>
    TargetProtected,

    /// <summary>The target folder cannot be written.</summary>
    TargetNotWritable,

    /// <summary>The target drive has too little free space.</summary>
    InsufficientSpace,

    /// <summary>The target drive's free space could not be read.</summary>
    SpaceUnknown,

    /// <summary>The target drive is FAT32 and a file is 4 GiB or larger.</summary>
    Fat32FileTooLarge,

    /// <summary>A shared file (breakpoints, review queue) could not be read and is left out.</summary>
    SharedFileSkipped,

    /// <summary>
    ///     The bundle carries stores of an extension that is not installed or is switched off here; that data is
    ///     skipped.
    /// </summary>
    ExtensionUnavailable,

    /// <summary>Anything else.</summary>
    Other
}

/// <summary>A finding shown before or after a transfer.</summary>
/// <param name="Kind">What it is about.</param>
/// <param name="Message">The text the wizard shows, in user terms.</param>
/// <param name="Blocking">True when the transfer cannot start until it is resolved.</param>
/// <param name="Sha256">The demo it is about, or null when it is about the whole transfer.</param>
public sealed record TransferWarning(
    TransferWarningKind Kind,
    string Message,
    bool Blocking = false,
    string? Sha256 = null);
