#region

using DemoViewer.NET.Extensions;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>What an import needs to hand the extensions their stores: which are loaded, which are on, and the roots.</summary>
/// <param name="Extensions">Each loaded extension's transfer declarations, in pack order.</param>
/// <param name="IsEnabled">Whether a feature id is switched on here.</param>
/// <param name="ConfigRoot">The root config stores resolve against, or null on a build with no filesystem.</param>
/// <param name="CacheRoot">The root cache stores resolve against, or null on a build with no filesystem.</param>
internal sealed record TransferExtensionHost(
    Func<IReadOnlyList<HostDataTransfer>> Extensions,
    Func<string, bool> IsEnabled,
    string? ConfigRoot,
    string? CacheRoot);
