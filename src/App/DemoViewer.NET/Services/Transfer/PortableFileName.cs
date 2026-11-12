#region

using System.Globalization;
using System.Text;

#endregion

namespace DemoViewer.NET.Services.Transfer;

/// <summary>
///     Turns a file name from another machine into one that is valid on NTFS, FAT32, exFAT, APFS and ext4 alike.
///     <see cref="Path.GetInvalidFileNameChars" /> cannot do this: off Windows it rejects only NUL and <c>/</c>,
///     so a name made on Linux could not be written to a Windows drive or a FAT stick.
/// </summary>
public static class PortableFileName
{
    /// <summary>
    ///     The longest name, in UTF-8 bytes, a sanitized name may have. ext4 allows 255 bytes and NTFS 255 UTF-16
    ///     units; the margin leaves room for a <c> (n)</c> suffix and a temp-file prefix.
    /// </summary>
    public const int MaxUtf8Bytes = 200;

    // Windows refuses these as a whole name and as the part before the first dot, in any case.
    private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    /// <summary>
    ///     A portable form of <paramref name="name" />: <c>&lt;&gt;:"/\|?*</c> and control characters become
    ///     <c>_</c>, leading spaces and trailing dots and spaces go, a Windows device name gets a <c>_</c> prefix,
    ///     the result is NFC-normalized and cut to <see cref="MaxUtf8Bytes" /> keeping the extension. An empty
    ///     result falls back to <paramref name="fallback" />.
    /// </summary>
    public static string Sanitize(string? name, string fallback = "demo.dem")
    {
        ArgumentException.ThrowIfNullOrEmpty(fallback);
        if (string.IsNullOrEmpty(name))
        {
            return fallback;
        }

        StringBuilder builder = new(name.Length);
        foreach (char c in name.Normalize(NormalizationForm.FormC))
        {
            builder.Append(c < 0x20 || c == 0x7F || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_'
                : c);
        }

        string result = TrimEnds(builder.ToString());
        if (result.Length == 0 || result.All(c => c == '_'))
        {
            return fallback;
        }

        if (IsReservedDeviceName(result))
        {
            result = "_" + result;
        }

        return Truncate(result, MaxUtf8Bytes);
    }

    /// <summary>
    ///     True when the part of <paramref name="name" /> before its first dot is a Windows device name such as
    ///     <c>CON</c>, <c>nul</c> or <c>COM1</c>, which Windows will not create as a file in any case.
    /// </summary>
    public static bool IsReservedDeviceName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? name : name[..dot];
        return _reserved.Contains(stem.TrimEnd(' '));
    }

    /// <summary>
    ///     <paramref name="sanitizedName" /> when <paramref name="isTaken" /> says it is free, else the first of
    ///     <c>name (2).ext</c>, <c>name (3).ext</c>… that is. The caller's check should compare case-insensitively
    ///     (<see cref="PathSafety.CaseInsensitivePaths" />), since the target may be a case-insensitive disk.
    /// </summary>
    public static string MakeUnique(string sanitizedName, Func<string, bool> isTaken)
    {
        ArgumentException.ThrowIfNullOrEmpty(sanitizedName);
        ArgumentNullException.ThrowIfNull(isTaken);
        if (!isTaken(sanitizedName))
        {
            return sanitizedName;
        }

        string extension = Path.GetExtension(sanitizedName);
        string stem = sanitizedName[..^extension.Length];
        for (int n = 2;; n++)
        {
            string suffix = " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + extension;
            string candidate = Truncate(stem, MaxUtf8Bytes - Encoding.UTF8.GetByteCount(suffix), keepExtension: false)
                               + suffix;
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }

    private static string TrimEnds(string value)
    {
        // Windows drops trailing dots and spaces silently, so "a." and "a" would collide; a leading space is
        // legal but invisible in every file list.
        return value.TrimStart(' ').TrimEnd('.', ' ');
    }

    private static string Truncate(string value, int maxBytes, bool keepExtension = true)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        string extension = keepExtension ? Path.GetExtension(value) : "";
        if (Encoding.UTF8.GetByteCount(extension) > maxBytes / 2)
        {
            extension = "";
        }

        int budget = maxBytes - Encoding.UTF8.GetByteCount(extension);
        string stem = value[..^extension.Length];
        StringBuilder kept = new();
        int used = 0;
        // Cut on text-element boundaries so a surrogate pair or a combining sequence is never split.
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(stem);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int bytes = Encoding.UTF8.GetByteCount(element);
            if (used + bytes > budget)
            {
                break;
            }

            kept.Append(element);
            used += bytes;
        }

        return TrimEnds(kept.ToString()) + extension;
    }
}

/// <summary>
///     The names already used in one target folder, compared case-insensitively, handing out portable unique
///     names for incoming files.
/// </summary>
public sealed class PortableNameSet
{
    private readonly HashSet<string> _taken = new(PathSafety.CaseInsensitivePaths);

    /// <param name="existing">The names already in the folder.</param>
    public PortableNameSet(IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        foreach (string name in existing)
        {
            _taken.Add(name.Normalize(NormalizationForm.FormC));
        }
    }

    /// <summary>The set of the file names in <paramref name="folder" />, or an empty set when it does not exist.</summary>
    public static PortableNameSet ForFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        return new PortableNameSet(Directory.Exists(folder)
            ? Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>()
            : []);
    }

    /// <summary>Sanitizes <paramref name="originalName" />, makes it unique in the set, records it and returns it.</summary>
    public string Claim(string? originalName, string fallback = "demo.dem")
    {
        string name = PortableFileName.MakeUnique(PortableFileName.Sanitize(originalName, fallback), _taken.Contains);
        _taken.Add(name);
        return name;
    }
}
