using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Matgate.Display.Rdp.Graphics.Tests;

// One test vector out of TestData: what a server sends and the picture FreeRDP's own decoder makes of it.
// harness/vectors.c writes the files and says how each vector comes about.
internal sealed class Vector
{
    public const int BitmapKind = 1;
    public const int PointerKind = 2;

    public required string Name { get; init; }

    public required int Kind { get; init; }

    // Bitmaps: the colour depth of the update. Pointers: the depth of the XOR mask.
    public required int BitsPerPixel { get; init; }

    public required bool Compressed { get; init; }

    // The size of the bitmap in the data; for a pointer its size.
    public required int Width { get; init; }

    public required int Height { get; init; }

    // The picture without the padding a server adds to the width: what the destination rectangle of a real
    // update would cover.
    public required int ContentWidth { get; init; }

    public required int ContentHeight { get; init; }

    // Bitmaps: the bitmapDataStream. Pointers: the XOR mask.
    public required byte[] Data { get; init; }

    // Pointers: the AND mask.
    public required byte[] Mask { get; init; }

    // 256 entries 0xFFRRGGBB, where the depth needs a palette.
    public uint[]? Palette { get; init; }

    // Bitmaps: BGRX, top-down, Width x Height. Pointers: RGBA, top-down.
    public required byte[] Expected { get; init; }

    // A second picture of the same vector: FreeRDP's own expansion of 5-6-5 (which is not bit replication), or
    // the picture the IronRDP project expects of one of its streams.
    public byte[]? Reference { get; init; }

    // "rle24/encoder", "planar/formats" ...: codec and how the bytes were made.
    public string Group
    {
        get
        {
            var parts = Name.Split('/');
            return parts.Length > 1 ? parts[0] + "/" + parts[1] : parts[0];
        }
    }

    public override string ToString() => Name;
}

internal static class Vectors
{
    private static readonly Lazy<IReadOnlyList<Vector>> LazyBitmaps = new(() => Load("bitmaps.bin.br"));
    private static readonly Lazy<IReadOnlyList<Vector>> LazyPointers = new(() => Load("pointers.bin.br"));

    public static IReadOnlyList<Vector> Bitmaps => LazyBitmaps.Value;

    public static IReadOnlyList<Vector> Pointers => LazyPointers.Value;

    private static IReadOnlyList<Vector> Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", file);
        using var compressed = File.OpenRead(path);
        using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        brotli.CopyTo(plain);
        ReadOnlySpan<byte> data = plain.GetBuffer().AsSpan(0, (int)plain.Length);

        if (data.Length < 12 || !data[..4].SequenceEqual("MGFX"u8) || BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) != 1)
        {
            throw new InvalidDataException(file + " is not a vector file");
        }

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var position = 12;
        var vectors = new List<Vector>(count);
        for (var i = 0; i < count; i++)
        {
            var name = Encoding.ASCII.GetString(Bytes(data, ref position, U16(data, ref position)));
            var kind = data[position++];
            var bitsPerPixel = data[position++];
            var flags = data[position++];
            position++;
            var width = U16(data, ref position);
            var height = U16(data, ref position);
            var contentWidth = U16(data, ref position);
            var contentHeight = U16(data, ref position);
            var stream = Bytes(data, ref position, (int)U32(data, ref position)).ToArray();
            var mask = Bytes(data, ref position, (int)U32(data, ref position)).ToArray();
            uint[]? palette = null;
            if ((flags & 4) != 0)
            {
                palette = new uint[256];
                for (var entry = 0; entry < 256; entry++)
                {
                    palette[entry] = U32(data, ref position);
                }
            }

            var expected = Bytes(data, ref position, (int)U32(data, ref position)).ToArray();
            var reference = Bytes(data, ref position, (int)U32(data, ref position)).ToArray();
            vectors.Add(new Vector
            {
                Name = name,
                Kind = kind,
                BitsPerPixel = bitsPerPixel,
                Compressed = (flags & 1) != 0,
                Width = width,
                Height = height,
                ContentWidth = contentWidth,
                ContentHeight = contentHeight,
                Data = stream,
                Mask = mask,
                Palette = palette,
                Expected = expected,
                Reference = (flags & 8) != 0 ? reference : null,
            });
        }

        if (position != data.Length)
        {
            throw new InvalidDataException(file + " has bytes after its last vector");
        }

        return vectors;
    }

    private static int U16(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
        position += 2;
        return value;
    }

    private static uint U32(ReadOnlySpan<byte> data, ref int position)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        position += 4;
        return value;
    }

    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> data, ref int position, int length)
    {
        var bytes = data.Slice(position, length);
        position += length;
        return bytes;
    }
}
