using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Matgate.Display.Rdp.Security;

// MD4 as in RFC 1320. NTLM turns the password into its key with it, and .NET offers no MD4: on Linux OpenSSL 3
// keeps it in the legacy provider, which is not loaded. As a hash it has been broken since the nineties - it is
// here because the protocol says so, not because anything should rely on it.
public static class Md4
{
    public const int HashSize = 16;
    private const int BlockSize = 64;

    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        var hash = new byte[HashSize];
        HashData(data, hash);
        return hash;
    }

    public static void HashData(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (destination.Length < HashSize)
        {
            throw new ArgumentException("The destination is too short for an MD4 hash.", nameof(destination));
        }

        Span<uint> state = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476];
        Span<uint> words = stackalloc uint[16];

        var remaining = data;
        while (remaining.Length >= BlockSize)
        {
            Transform(state, remaining[..BlockSize], words);
            remaining = remaining[BlockSize..];
        }

        // The rest, the 0x80 marker, zeros up to 56 mod 64, and the length in bits: one block, or two when the
        // rest leaves no room for the nine bytes of marker and length.
        Span<byte> tail = stackalloc byte[BlockSize * 2];
        tail.Clear();
        remaining.CopyTo(tail);
        tail[remaining.Length] = 0x80;
        var tailLength = remaining.Length + 9 <= BlockSize ? BlockSize : BlockSize * 2;
        BinaryPrimitives.WriteUInt64LittleEndian(tail[(tailLength - 8)..], (ulong)data.Length * 8);
        for (var offset = 0; offset < tailLength; offset += BlockSize)
        {
            Transform(state, tail.Slice(offset, BlockSize), words);
        }

        for (var i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * 4)..], state[i]);
        }

        // What was hashed is usually a password.
        CryptographicOperations.ZeroMemory(tail);
        words.Clear();
        state.Clear();
    }

    private static void Transform(Span<uint> state, ReadOnlySpan<byte> block, Span<uint> x)
    {
        for (var i = 0; i < 16; i++)
        {
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);
        }

        var a = state[0];
        var b = state[1];
        var c = state[2];
        var d = state[3];

        for (var i = 0; i < 16; i += 4)
        {
            a = BitOperations.RotateLeft(a + F(b, c, d) + x[i], 3);
            d = BitOperations.RotateLeft(d + F(a, b, c) + x[i + 1], 7);
            c = BitOperations.RotateLeft(c + F(d, a, b) + x[i + 2], 11);
            b = BitOperations.RotateLeft(b + F(c, d, a) + x[i + 3], 19);
        }

        for (var i = 0; i < 4; i++)
        {
            a = BitOperations.RotateLeft(a + G(b, c, d) + x[i] + 0x5A827999, 3);
            d = BitOperations.RotateLeft(d + G(a, b, c) + x[i + 4] + 0x5A827999, 5);
            c = BitOperations.RotateLeft(c + G(d, a, b) + x[i + 8] + 0x5A827999, 9);
            b = BitOperations.RotateLeft(b + G(c, d, a) + x[i + 12] + 0x5A827999, 13);
        }

        // The third round takes the words in the order 0 8 4 12, 2 10 6 14, 1 9 5 13, 3 11 7 15.
        ReadOnlySpan<int> thirdRound = [0, 2, 1, 3];
        foreach (var i in thirdRound)
        {
            a = BitOperations.RotateLeft(a + H(b, c, d) + x[i] + 0x6ED9EBA1, 3);
            d = BitOperations.RotateLeft(d + H(a, b, c) + x[i + 8] + 0x6ED9EBA1, 9);
            c = BitOperations.RotateLeft(c + H(d, a, b) + x[i + 4] + 0x6ED9EBA1, 11);
            b = BitOperations.RotateLeft(b + H(c, d, a) + x[i + 12] + 0x6ED9EBA1, 15);
        }

        state[0] += a;
        state[1] += b;
        state[2] += c;
        state[3] += d;
    }

    private static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);

    private static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);

    private static uint H(uint x, uint y, uint z) => x ^ y ^ z;
}
