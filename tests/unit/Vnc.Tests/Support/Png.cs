using System.Buffers.Binary;
using System.IO.Compression;

namespace Matgate.Display.Vnc.Tests;

// Writes a framebuffer as a PNG file - for looking at what the client saw when a test against a real server
// fails. Only what that needs: 8 bits a channel, no alpha, no filtering.
internal static class Png
{
    public static void Save(string path, byte[] bgrx, int width, int height)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(file, "IHDR", header);

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = (y * width + x) * 4;
                    row[1 + x * 3] = bgrx[pixel + 2];
                    row[2 + x * 3] = bgrx[pixel + 1];
                    row[3 + x * 3] = bgrx[pixel];
                }

                zlib.Write(row);
            }
        }

        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        file.Write(number);
        var name = System.Text.Encoding.ASCII.GetBytes(type);
        file.Write(name);
        file.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(data, Crc(name, 0xFFFFFFFF)) ^ 0xFFFFFFFF);
        file.Write(number);
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc;
    }
}
