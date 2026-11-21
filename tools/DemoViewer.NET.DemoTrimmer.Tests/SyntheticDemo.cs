#region

using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using Google.Protobuf;

#endregion

namespace DemoViewer.NET.DemoTrimmer.Tests;

/// <summary>
///     The smallest container the trimmer will accept: a file header, one empty <c>DEM_Packet</c>, and
///     a <c>DEM_FileInfo</c> the header points at. Enough to drive <see cref="DemoTrimWriter" /> through
///     its own file handling without a 170 MB demo, which is what lets these cases run in the fast tier.
/// </summary>
internal static class SyntheticDemo
{
    /// <summary>The tick the one packet frame is stamped with.</summary>
    public const int PacketTick = 480;

    /// <summary>Builds the raw bytes and the parsed view over them.</summary>
    /// <param name="roundStartTicks">The source's <c>round_start_ticks</c>.</param>
    /// <param name="events">Game events to hang on the demo.</param>
    /// <param name="corruptFrame">
    ///     When true the frame claims bytes past the end of the file, so copying it throws after the
    ///     output has been opened.
    /// </param>
    /// <param name="withFileInfo">False builds an unfinalised recording: no tail, header offsets zero.</param>
    public static (byte[] Raw, ParsedDemo Demo) Build(
        IEnumerable<int>? roundStartTicks = null, IReadOnlyList<GameEvent>? events = null,
        bool corruptFrame = false, bool withFileInfo = true)
    {
        using MemoryStream ms = new();
        DemoFormat.WriteFileHeader(ms, 0, 0);

        int frameStart = (int)ms.Position;
        DemoFormat.WriteFrame(ms, DemoFormat.CommandIdByName["DEM_Packet"], PacketTick, false, []);
        int frameLength = (int)ms.Position - frameStart;

        int fileInfoOffset = (int)ms.Position;
        if (withFileInfo)
        {
            CDemoFileInfo info = new()
            {
                PlaybackTicks = 2000, GameInfo = new CGameInfo { Cs = new CGameInfo.Types.CCSGameInfo() }
            };
            info.GameInfo.Cs.RoundStartTicks.AddRange(roundStartTicks ?? []);
            DemoFormat.WriteFrame(ms, DemoFormat.CommandIdByName["DEM_FileInfo"], 2000, false, info.ToByteArray());
        }

        byte[] raw = ms.ToArray();
        if (withFileInfo)
        {
            BitConverter.TryWriteBytes(raw.AsSpan(8, 4), fileInfoOffset);
        }

        DemoFrame frame = new()
        {
            CommandKind = EDemoCommands.DemPacket,
            FrameNumber = 0,
            ServerTick = PacketTick,
            HeaderLength = frameLength,
            RawLength = frameLength,
            RawStart = corruptFrame ? raw.Length + 64 : frameStart,
            IsCompressed = false
        };

        ParsedDemo demo = new(
            [frame], events ?? [], new Dictionary<int, PlayerInfo>(), null,
            "de_test", 2000, 1f / 64, "test", "test", "csgo", 0, 0, 0, "valve_demo_2", "", "",
            DemoProfile.Unknown);
        return (raw, demo);
    }

    /// <summary>The <c>round_start_ticks</c> an emitted file's <c>DEM_FileInfo</c> carries.</summary>
    /// <param name="path">The emitted file.</param>
    /// <param name="demo">The demo it was trimmed from.</param>
    public static IReadOnlyList<int> RoundStartTicks(string path, ParsedDemo demo)
    {
        byte[] raw = File.ReadAllBytes(path);
        RawFrame fileInfo = DemoTail.Read(raw, demo).FileInfo
            ?? throw new InvalidDataException("the emitted file has no DEM_FileInfo.");
        return [.. CDemoFileInfo.Parser.ParseFrom(DemoTail.Payload(raw, fileInfo)).GameInfo.Cs.RoundStartTicks];
    }
}
