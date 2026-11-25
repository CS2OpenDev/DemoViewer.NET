#region

using CS2OpenSchema.Protos;
using DemoViewer.NET.Models;
using Google.Protobuf;
using Google.Protobuf.Reflection;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The inspector's decode of a payload the parser left as raw bytes. The parser hands such a payload over
///     in a wrapper message, and the inspector is the first thing that ever parses the bytes inside, so a
///     broken demo's bytes reach it unchecked.
/// </summary>
public class PayloadNodeBuilderTests
{
    [Test]
    public async Task ADeferredPayload_ThatDoesNotDecode_FallsBackToTheRawScan()
    {
        // Field 1, a varint the scanner reads; then field 1 again, length-delimited, claiming 127 bytes that
        // are not there.
        byte[] cut = [0x08, 0x01, 0x0A, 0x7F];
        Deferred wrapper = new(CMsg_CVars.Descriptor, cut);
        await Assert.That(() => CMsg_CVars.Parser.ParseFrom(cut)).Throws<InvalidProtocolBufferException>();

        IReadOnlyList<PayloadNode> nodes = PayloadNodeBuilder.Build(wrapper, cut);

        await Assert.That(nodes).IsNotNull();
    }

    [Test]
    public async Task ADeferredPayload_ThatDecodes_IsShownAsItsRealMessage()
    {
        CMsg_CVars real = new();
        real.Cvars.Add(new CMsg_CVars.Types.CVar { Name = "sv_cheats", Value = "0" });
        byte[] bytes = real.ToByteArray();

        IReadOnlyList<PayloadNode> nodes = PayloadNodeBuilder.Build(new Deferred(CMsg_CVars.Descriptor, bytes), bytes);

        await Assert.That(nodes.Count).IsEqualTo(PayloadNodeBuilder.Build(real, bytes).Count);
        await Assert.That(nodes.Count).IsGreaterThan(0);
    }

    // What the parser's own wrapper is to the builder: a message whose type is not the one its descriptor
    // declares, and which writes the payload's bytes as they are.
    private sealed class Deferred(MessageDescriptor descriptor, byte[] bytes) : IMessage
    {
        public MessageDescriptor Descriptor => descriptor;

        public void MergeFrom(CodedInputStream input)
        {
        }

        public void WriteTo(CodedOutputStream output)
        {
            foreach (byte b in bytes)
            {
                output.WriteRawTag(b);
            }
        }

        public int CalculateSize() => bytes.Length;
    }
}
