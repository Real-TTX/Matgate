using System.Buffers.Binary;

namespace Matgate.Display.Rdp.Channels.Tests;

// Static channel chunking, MS-RDPBCGR 2.2.6.1 and 3.1.5.2.
public class ChannelChunkTests
{
    private const uint First = 0x00000001;
    private const uint Last = 0x00000002;
    private const uint ShowProtocol = 0x00000010;
    private const uint Compressed = 0x00200000;

    private static List<byte[]> Split(byte[] message, int chunkSize, bool showProtocol = false)
    {
        var chunks = new List<byte[]>();
        ChannelChunks.Split(message, chunkSize, showProtocol, chunk => chunks.Add(chunk.ToArray()));
        return chunks;
    }

    private static byte[] Chunk(uint total, uint flags, params byte[] data)
    {
        var chunk = new byte[8 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, total);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), flags);
        data.CopyTo(chunk, 8);
        return chunk;
    }

    private static byte[] Pattern(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)(i * 31 + (i >> 8));
        }

        return data;
    }

    // A message that is not disturbed by what came before it goes through in one piece.
    private static void StillWorks(ChannelReassembler reassembler)
    {
        var message = Pattern(3000);
        ReadOnlyMemory<byte> result = default;
        var complete = 0;
        foreach (var chunk in Split(message, 1600))
        {
            if (reassembler.Add(chunk, out var done))
            {
                complete++;
                result = done;
            }
        }

        Assert.Equal(1, complete);
        Hex.Same(message, result.Span);
    }

    public static TheoryData<int, int> SizesAndChunks()
    {
        var data = new TheoryData<int, int>();
        foreach (var size in new[] { 0, 1, 1599, 1600, 1601, 1_000_000 })
        {
            foreach (var chunk in new[] { 1600, 1000, 16256, 7 })
            {
                data.Add(size, chunk);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SizesAndChunks))]
    public void A_message_survives_being_split_and_put_together(int size, int chunkSize)
    {
        var message = Pattern(size);
        var chunks = Split(message, chunkSize);

        Assert.Equal(Math.Max(1, (size + chunkSize - 1) / chunkSize), chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(chunks[i].AsSpan(4));
            Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32LittleEndian(chunks[i]));
            Assert.Equal(i == 0, (flags & First) != 0);
            Assert.Equal(i == chunks.Count - 1, (flags & Last) != 0);
            Assert.Equal(0u, flags & ~(First | Last));
            Assert.Equal(i == chunks.Count - 1 ? size - chunkSize * i : chunkSize, chunks[i].Length - 8);
        }

        var reassembler = new ChannelReassembler();
        for (var i = 0; i < chunks.Count; i++)
        {
            var complete = reassembler.Add(chunks[i], out var result);
            Assert.Equal(i == chunks.Count - 1, complete);
            if (complete)
            {
                Hex.Same(message, result.Span);
            }
        }

        StillWorks(reassembler);
    }

    // MS-RDPBCGR 3.1.5.2.2.1: 2,062 bytes at a chunk size of 1,000 are three Virtual Channel PDUs.
    [Fact]
    public void Spec_example_of_2062_bytes_in_chunks_of_1000()
    {
        var chunks = Split(Pattern(2062), 1000);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(new[] { 1000, 1000, 62 }, chunks.Select(chunk => chunk.Length - 8));
        Assert.All(chunks, chunk => Assert.Equal(2062u, BinaryPrimitives.ReadUInt32LittleEndian(chunk)));
        Assert.Equal(new[] { First, 0u, Last }, chunks.Select(chunk => BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4))));
    }

    // MS-RDPBCGR 4.4 Annotated Server-to-Client Virtual Channel PDU: the decrypted static virtual channel data.
    [Fact]
    public void Spec_example_of_a_virtual_channel_pdu()
    {
        var chunk = Hex.Bytes("08 00 00 00 03 00 00 00 03 00 01 00 00 00 00 00");

        Assert.True(new ChannelReassembler().Add(chunk, out var message));
        Hex.Same("03 00 01 00 00 00 00 00", message.Span);

        // And the other way round: the same message leaves as the same chunk.
        Hex.Same(chunk, Assert.Single(Split(message.ToArray(), 1600)));
    }

    [Fact]
    public void Show_protocol_is_on_every_chunk_when_asked_and_on_none_otherwise()
    {
        Assert.All(Split(Pattern(5000), 1600, true), chunk => Assert.Equal(ShowProtocol, BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)) & ShowProtocol));
        Assert.All(Split(Pattern(5000), 1600), chunk => Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)) & ShowProtocol));
        Hex.Same("00 00 00 00 13 00 00 00", Assert.Single(Split([], 1600, true)));
    }

    [Fact]
    public void The_chunk_size_follows_the_server_within_what_the_specification_allows()
    {
        Assert.Equal(1600, ChannelChunks.ChunkSize(0));
        Assert.Equal(1600, ChannelChunks.ChunkSize(1599));
        Assert.Equal(1600, ChannelChunks.ChunkSize(1600));
        Assert.Equal(8192, ChannelChunks.ChunkSize(8192));
        Assert.Equal(16256, ChannelChunks.ChunkSize(16256));
        Assert.Equal(1600, ChannelChunks.ChunkSize(16257));
        Assert.Equal(1600, ChannelChunks.ChunkSize(uint.MaxValue));
    }

    [Fact]
    public void Split_refuses_a_chunk_size_that_is_none()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelChunks.Split([1, 2, 3], 0, false, _ => { }));
        Assert.Throws<ArgumentNullException>(() => ChannelChunks.Split([1, 2, 3], 1600, false, null!));
    }

    public static TheoryData<string, byte[][]> BrokenSequences() => new()
    {
        { "shorter than the header", [[1, 0, 0, 0, 3, 0, 0]] },
        { "no bytes at all", [[]] },
        { "a last chunk without a first", [Chunk(4, Last, 1, 2, 3, 4)] },
        { "a middle chunk without a first", [Chunk(8, 0, 1, 2, 3, 4)] },
        { "one chunk with less than it announces", [Chunk(5, First | Last, 1, 2, 3, 4)] },
        { "one chunk with more than it announces", [Chunk(3, First | Last, 1, 2, 3, 4)] },
        { "a first chunk with more than it announces", [Chunk(3, First, 1, 2, 3, 4), Chunk(3, Last)] },
        { "more data than announced", [Chunk(6, First, 1, 2, 3, 4), Chunk(6, Last, 5, 6, 7)] },
        { "the end before the announced length", [Chunk(8, First, 1, 2, 3, 4), Chunk(8, Last, 5, 6)] },
        { "another length in the middle", [Chunk(8, First, 1, 2, 3, 4), Chunk(9, Last, 5, 6, 7, 8)] },
        { "a compressed chunk", [Chunk(4, First | Last | Compressed, 1, 2, 3, 4)] },
        { "a compressed chunk in the middle", [Chunk(8, First, 1, 2, 3, 4), Chunk(8, Last | Compressed, 5, 6, 7, 8)] },
        { "more than 64 MB", [Chunk(64 * 1024 * 1024 + 1, First, 1, 2, 3, 4)] },
        { "four gigabytes", [Chunk(uint.MaxValue, First | Last, 1, 2, 3, 4)] },
    };

    [Theory]
    [MemberData(nameof(BrokenSequences))]
    public void A_chunk_that_does_not_fit_is_dropped_and_the_next_message_is_whole(string what, byte[][] chunks)
    {
        var log = new List<string>();
        var reassembler = new ChannelReassembler { Log = log.Add };

        foreach (var chunk in chunks)
        {
            Assert.False(reassembler.Add(chunk, out var message), what);
            Assert.True(message.IsEmpty);
        }

        Assert.NotEmpty(log);
        StillWorks(reassembler);
    }

    [Fact]
    public void A_first_chunk_in_the_middle_of_a_message_starts_a_new_one()
    {
        var log = new List<string>();
        var reassembler = new ChannelReassembler { Log = log.Add };

        Assert.False(reassembler.Add(Chunk(8, First, 1, 2, 3, 4), out _));
        Assert.False(reassembler.Add(Chunk(6, First, 9, 8, 7), out _));
        Assert.True(reassembler.Add(Chunk(6, Last, 6, 5, 4), out var message));

        Hex.Same("09 08 07 06 05 04", message.Span);
        Assert.Single(log);
    }

    // MS-RDPBCGR 3.1.5.2.2: without FIRST and LAST, and with no sequence under way, the data is complete as it is.
    [Fact]
    public void A_chunk_without_flags_outside_a_sequence_is_a_message_when_its_length_says_so()
    {
        var reassembler = new ChannelReassembler();

        Assert.True(reassembler.Add(Chunk(4, 0, 1, 2, 3, 4), out var message));
        Hex.Same("01 02 03 04", message.Span);
        StillWorks(reassembler);
    }

    [Fact]
    public void Suspend_and_resume_are_not_data_and_leave_a_message_under_way_alone()
    {
        var reassembler = new ChannelReassembler();

        Assert.False(reassembler.Add(Chunk(8, First, 1, 2, 3, 4), out _));
        Assert.False(reassembler.Add(Chunk(0, 0x20), out _));
        Assert.False(reassembler.Add(Chunk(0, 0x40), out _));
        Assert.True(reassembler.Add(Chunk(8, Last, 5, 6, 7, 8), out var message));

        Hex.Same("01 02 03 04 05 06 07 08", message.Span);
    }

    [Fact]
    public void An_announced_length_reserves_nothing_until_the_bytes_arrive()
    {
        var reassembler = new ChannelReassembler();
        var chunk = Chunk(ChannelReassembler.MaxMessageLength, First, 1);
        var before = GC.GetAllocatedBytesForCurrentThread();

        // A server that announces 64 MB over and over and sends a byte each time.
        for (var i = 0; i < 100; i++)
        {
            Assert.False(reassembler.Add(chunk, out _));
        }

        // 100 buffers of 64 KB and nothing like 100 times 64 MB.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 100 * 80 * 1024);
        StillWorks(reassembler);
    }

    [Fact]
    public void A_refused_length_reserves_nothing_at_all()
    {
        var reassembler = new ChannelReassembler();
        var chunk = Chunk(uint.MaxValue, First | Last, new byte[1600]);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
        {
            Assert.False(reassembler.Add(chunk, out _));
        }

        // A line for the log each time, and not a byte of the chunks.
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 512 * 1024);
    }

    [Fact]
    public void A_reassembler_can_be_held_to_less_than_64_MB()
    {
        var reassembler = new ChannelReassembler(100);

        Assert.True(reassembler.Add(Chunk(100, First | Last, new byte[100]), out _));
        Assert.False(reassembler.Add(Chunk(101, First | Last, new byte[101]), out _));
        Assert.True(reassembler.Add(Chunk(100, First | Last, new byte[100]), out _));
    }

    [Fact]
    public void A_finished_message_belongs_to_the_caller()
    {
        var reassembler = new ChannelReassembler();
        var chunks = Split(Pattern(4000), 1600);
        ReadOnlyMemory<byte> first = default;
        foreach (var chunk in chunks)
        {
            reassembler.Add(chunk, out first);
        }

        var copy = first.ToArray();
        foreach (var chunk in Split(new byte[4000], 1600))
        {
            reassembler.Add(chunk, out _);
        }

        // The second message did not land in the memory of the first.
        Hex.Same(copy, first.Span);
    }

    [Fact]
    public void Reset_forgets_a_message_under_way()
    {
        var reassembler = new ChannelReassembler();
        Assert.False(reassembler.Add(Chunk(8, First, 1, 2, 3, 4), out _));

        reassembler.Reset();

        Assert.False(reassembler.Add(Chunk(8, Last, 5, 6, 7, 8), out _));
        StillWorks(reassembler);
    }
}
