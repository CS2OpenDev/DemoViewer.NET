#region

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

#endregion

namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     Reads and writes the cache's JSON sidecars as UTF-8 bytes, gzipped or plain. Readers sniff the gzip
///     magic rather than trusting the file name, so a plain file under a <c>.gz</c> name still reads.
/// </summary>
public static class SidecarJson
{
    // Guards the rent sized from the gzip trailer, which a corrupt file can set to anything.
    private const int MaxTrailerHint = 256 * 1024 * 1024;

    // The most a gzipped sidecar may inflate to. A stream's compressed size says nothing about this: a
    // megabyte of gzip can hold a gigabyte, and a record out of a transfer bundle is whatever the bundle's
    // author put there. Far above any record, table or index this app writes.
    private const int MaxInflated = 256 * 1024 * 1024;

    /// <summary>True when <paramref name="bytes" /> starts with the gzip magic.</summary>
    private static bool IsGzip(ReadOnlySpan<byte> bytes) => bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    /// <summary>Compact UTF-8 JSON, gzipped.</summary>
    public static byte[] SerializeGzip<T>(T value, JsonSerializerOptions? options) =>
        Gzip(JsonSerializer.SerializeToUtf8Bytes(value, options));

    /// <summary>Gzips a buffer.</summary>
    public static byte[] Gzip(ReadOnlySpan<byte> bytes)
    {
        using MemoryStream buffer = new();
        using (GZipStream gzip = new(buffer, CompressionLevel.Optimal, true))
        {
            gzip.Write(bytes);
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     Plain JSON re-written without whitespace, keeping every property whether or not a model knows
    ///     it. Throws on bad JSON.
    /// </summary>
    public static byte[] Minify(byte[] json)
    {
        int bom = SkipBom(json).Length == json.Length ? 0 : 3;
        using JsonDocument document = JsonDocument.Parse(json.AsMemory(bom));
        ArrayBufferWriter<byte> buffer = new(json.Length);
        using (Utf8JsonWriter writer = new(buffer))
        {
            document.WriteTo(writer);
        }

        return [.. buffer.WrittenSpan];
    }

    /// <summary>
    ///     The value <paramref name="bytes" /> hold, gzipped or plain. Throws on a bad stream or bad JSON; the
    ///     callers own the "bad file = not cached" rule.
    /// </summary>
    public static T? Deserialize<T>(ArraySegment<byte> bytes, JsonSerializerOptions? options) =>
        DeserializeWithin<T>(bytes, options, MaxInflated);

    /// <summary><see cref="Deserialize{T}" /> with the inflated size limit given.</summary>
    internal static T? DeserializeWithin<T>(ArraySegment<byte> bytes, JsonSerializerOptions? options, int maxInflated)
    {
        if (!IsGzip(bytes))
        {
            return JsonSerializer.Deserialize<T>(SkipBom(bytes), options);
        }

        byte[] inflated = ArrayPool<byte>.Shared.Rent(Math.Min(InflatedHint(bytes), maxInflated));
        try
        {
            int length = Inflate(bytes, ref inflated, maxInflated);
            return JsonSerializer.Deserialize<T>(SkipBom(inflated.AsSpan(0, length)), options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inflated);
        }
    }

    /// <summary>Reads a whole file into a pooled buffer and deserializes it. Throws like <see cref="Deserialize{T}" />.</summary>
    public static T? ReadFile<T>(string path, JsonSerializerOptions? options)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
            FileOptions.SequentialScan);
        long size = stream.Length;
        if (size > int.MaxValue)
        {
            throw new InvalidDataException("sidecar too large");
        }

        byte[] raw = ArrayPool<byte>.Shared.Rent(Math.Max(1, (int)size));
        try
        {
            int read = stream.ReadAtLeast(raw.AsSpan(0, (int)size), (int)size, false);
            return Deserialize<T>(new ArraySegment<byte>(raw, 0, read), options);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    // The gzip trailer's ISIZE: the inflated length mod 2^32 for a single-member stream.
    private static int InflatedHint(ReadOnlySpan<byte> gzipped)
    {
        if (gzipped.Length < 18)
        {
            return 4096;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(gzipped[^4..]);
        return size is > 0 and <= MaxTrailerHint ? (int)size : 64 * 1024;
    }

    // Throws InvalidDataException once the stream holds more than max bytes, before reading further.
    private static int Inflate(ArraySegment<byte> gzipped, ref byte[] target, int max)
    {
        using MemoryStream input = new(gzipped.Array!, gzipped.Offset, gzipped.Count, false);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        int length = 0;
        while (true)
        {
            // A rented buffer can be longer than was asked for; the limit still ends the read.
            int room = Math.Min(target.Length, max);
            if (length == room)
            {
                if (room == max)
                {
                    if (gzip.ReadByte() < 0)
                    {
                        return length;
                    }

                    throw new InvalidDataException("sidecar inflates past the size limit");
                }

                byte[] grown = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)target.Length * 2, max));
                target.AsSpan(0, length).CopyTo(grown);
                ArrayPool<byte>.Shared.Return(target);
                target = grown;
                room = Math.Min(target.Length, max);
            }

            int read = gzip.Read(target, length, room - length);
            if (read == 0)
            {
                return length;
            }

            length += read;
        }
    }

    private static ReadOnlySpan<byte> SkipBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;
}
