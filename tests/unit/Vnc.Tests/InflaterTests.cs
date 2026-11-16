using System.IO.Compression;
using Xunit;

namespace Matgate.Display.Vnc.Tests;

// The zlib streams that last for a whole connection. What matters is the seam between two rectangles: the
// unpacker takes its input in blocks of its own size, so the last bytes of a rectangle's piece - its end
// mark - can still be on the line when the decoder already has all its pixels. They belong to the stream
// all the same; lost, the next rectangle cannot be unpacked.
public class InflaterTests
{
    // Pieces of every length around the sizes at which the unpacker fetches its next block: for some of them
    // the end mark falls behind the block the pixels came out of.
    [Theory]
    [InlineData(8100, 8300)]
    [InlineData(16280, 16480)]
    [InlineData(24480, 24680)]
    public void A_piece_whose_end_falls_behind_what_was_needed_keeps_the_stream_in_step(int from, int to)
    {
        var random = new Random(from);
        for (var size = from; size < to; size++)
        {
            // Not compressed at all, so the piece is as long as its data plus a few bytes.
            using var packer = new Packer(CompressionLevel.NoCompression);
            using var inflater = new VncInflater();
            var first = new byte[size];
            var second = new byte[100];
            random.NextBytes(first);
            random.NextBytes(second);
            var firstPiece = packer.Pack(first);
            var secondPiece = packer.Pack(second);
            var reader = new Packet().Bytes(firstPiece).U8(0x77).Bytes(secondPiece).U8(0x78).Reader();

            inflater.Begin(reader, firstPiece.Length);
            Assert.Equal(first, Take(inflater, size));
            inflater.End();
            Assert.Equal(0x77, reader.ReadByte());

            inflater.Begin(reader, secondPiece.Length);
            Assert.Equal(second, Take(inflater, 100));
            inflater.End();
            Assert.Equal(0x78, reader.ReadByte());
        }
    }

    [Fact]
    public void Many_pieces_of_all_sizes_in_one_stream()
    {
        var random = new Random(99);
        using var packer = new Packer();
        using var inflater = new VncInflater();
        var stream = new Packet();
        var pieces = new List<(byte[] Data, int Length)>();
        for (var index = 0; index < 300; index++)
        {
            // Half noise, half zeros: compressible, but not to nothing.
            var data = new byte[random.Next(1, 30000)];
            random.NextBytes(data.AsSpan(0, data.Length / 2));
            var piece = packer.Pack(data);
            pieces.Add((data, piece.Length));
            stream.Bytes(piece).U8(index);
        }

        var reader = stream.Reader();
        for (var index = 0; index < pieces.Count; index++)
        {
            inflater.Begin(reader, pieces[index].Length);
            Assert.Equal(pieces[index].Data, Take(inflater, pieces[index].Data.Length));
            inflater.End();
            Assert.Equal((byte)index, reader.ReadByte());
        }
    }

    [Fact]
    public void More_than_the_decoder_takes_is_thrown_away_and_the_stream_goes_on()
    {
        var random = new Random(7);
        using var packer = new Packer(CompressionLevel.NoCompression);
        using var inflater = new VncInflater();
        var first = new byte[50000];
        var second = new byte[300];
        random.NextBytes(first);
        random.NextBytes(second);
        var firstPiece = packer.Pack(first);
        var secondPiece = packer.Pack(second);
        var reader = new Packet().Bytes(firstPiece).U8(0x77).Bytes(secondPiece).Reader();

        inflater.Begin(reader, firstPiece.Length);
        Assert.Equal(first.AsSpan(0, 10).ToArray(), Take(inflater, 10));
        inflater.End();
        Assert.Equal(0x77, reader.ReadByte());

        inflater.Begin(reader, secondPiece.Length);
        Assert.Equal(second, Take(inflater, 300));
        inflater.End();
    }

    [Fact]
    public void A_reset_starts_with_a_new_stream()
    {
        using var inflater = new VncInflater();
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        for (var round = 0; round < 3; round++)
        {
            using var packer = new Packer();
            var piece = packer.Pack(data);
            inflater.Reset();
            inflater.Begin(new Packet().Bytes(piece).Reader(), piece.Length);
            Assert.Equal(data, Take(inflater, data.Length));
            inflater.End();
        }
    }

    [Fact]
    public void A_stream_the_server_ends_cannot_go_on()
    {
        // A zlib stream that is closed properly, as a file would be - not what a VNC server sends.
        var finished = new MemoryStream();
        using (var zlib = new ZLibStream(finished, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(new byte[100]);
        }

        using var inflater = new VncInflater();
        var reader = new Packet().Bytes(finished.ToArray()).Bytes(finished.ToArray()).Reader();
        inflater.Begin(reader, finished.Length);
        Assert.Equal(new byte[100], Take(inflater, 100));
        inflater.End();

        inflater.Begin(reader, finished.Length);
        Assert.Throws<VncException>(() => inflater.Take(100));
    }

    [Fact]
    public void A_stream_the_server_ends_with_a_lot_behind_it_is_refused_at_once()
    {
        var finished = new MemoryStream();
        using (var zlib = new ZLibStream(finished, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(new byte[100]);
        }

        using var inflater = new VncInflater();
        var piece = new Packet().Bytes(finished.ToArray()).Zeros(20000);
        inflater.Begin(piece.Reader(), piece.Length);
        Assert.Equal(new byte[100], Take(inflater, 100));
        Assert.Throws<VncException>(inflater.End);
    }

    [Fact]
    public void What_is_no_zlib_data_is_refused()
    {
        var random = new Random(5);
        for (var round = 0; round < 200; round++)
        {
            var garbage = new byte[random.Next(1, 500)];
            random.NextBytes(garbage);
            using var inflater = new VncInflater();
            inflater.Begin(new Packet().Bytes(garbage).Reader(), garbage.Length);

            // Noise is not a zlib stream; should it happen to begin like one, it unpacks to less than this.
            Assert.Throws<VncException>(() => Take(inflater, 60000));
        }
    }

    [Fact]
    public void A_piece_that_is_cut_off_is_refused()
    {
        using var packer = new Packer();
        var piece = packer.Pack(new byte[5000]);

        // The length says more than the line brings.
        using var inflater = new VncInflater();
        inflater.Begin(new Packet().Bytes(piece.AsSpan(0, piece.Length - 3).ToArray()).Reader(), piece.Length);
        Assert.Throws<VncException>(() =>
        {
            Take(inflater, 5000);
            inflater.End();
        });
    }

    private static byte[] Take(VncInflater inflater, int count)
    {
        var data = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            // In pieces of odd sizes, as a decoder takes rows and single bytes.
            var step = Math.Min(count - offset, 1 + offset % 4099);
            if (step == 1)
            {
                data[offset] = inflater.ReadByte();
            }
            else
            {
                inflater.Take(step).CopyTo(data.AsSpan(offset));
            }

            offset += step;
        }

        return data;
    }
}
