using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Channels;

// Cuts a channel message into the chunks a Virtual Channel PDU carries (MS-RDPBCGR 2.2.6.1 and 3.1.5.2.1).
public static class ChannelChunks
{
    // CHANNEL_PDU_HEADER: the total length of the message and the flags, 4 bytes each.
    public const int HeaderLength = 8;

    // CHANNEL_CHUNK_LENGTH: what a chunk may carry unless the server's Virtual Channel Capability Set says more.
    public const int DefaultChunkSize = 1600;

    // The largest VCChunkSize a server may announce (MS-RDPBCGR 2.2.7.1.10).
    public const int MaxChunkSize = 16256;

    public const uint FlagFirst = 0x00000001;
    public const uint FlagLast = 0x00000002;
    public const uint FlagShowProtocol = 0x00000010;
    public const uint FlagSuspend = 0x00000020;
    public const uint FlagResume = 0x00000040;
    public const uint FlagCompressed = 0x00200000;

    // The chunk size to send with, from the VCChunkSize of the server's capability set (0 when it had none).
    // A value outside what the specification allows is not believed.
    public static int ChunkSize(uint announced) =>
        announced is >= DefaultChunkSize and <= MaxChunkSize ? (int)announced : DefaultChunkSize;

    // Splits a message into chunks, each prefixed with its CHANNEL_PDU_HEADER. The span handed to emit is only
    // good during the call (one buffer serves all chunks). An empty message is one chunk without data.
    //
    // showProtocol sets CHANNEL_FLAG_SHOW_PROTOCOL on every chunk and belongs to the channels announced with
    // CHANNEL_OPTION_SHOW_PROTOCOL, and only to them. The specification asks for the flag on all chunked data
    // (3.1.5.2.1), but a Windows server takes it as "this endpoint reads the chunk headers itself" and hands
    // them to an endpoint that never asked for them; FreeRDP and IronRDP go by the channel's option as well.
    public static void Split(ReadOnlySpan<byte> message, int chunkSize, bool showProtocol, Action<ReadOnlySpan<byte>> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        var buffer = new byte[HeaderLength + Math.Min(chunkSize, message.Length)];
        var offset = 0;
        do
        {
            var count = Math.Min(chunkSize, message.Length - offset);
            var flags = showProtocol ? FlagShowProtocol : 0;
            if (offset == 0)
            {
                flags |= FlagFirst;
            }

            if (offset + count == message.Length)
            {
                flags |= FlagLast;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)message.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), flags);
            message.Slice(offset, count).CopyTo(buffer.AsSpan(HeaderLength));
            emit(buffer.AsSpan(0, HeaderLength + count));
            offset += count;
        }
        while (offset < message.Length);
    }
}
