using System.Buffers.Binary;

namespace Matgate.Display.Vnc;

// DES, for the one place the protocol needs it: VNC's password check. Written out here because nothing can be
// relied on to bring it - OpenSSL 3 keeps single DES in its legacy provider, which a container does not load,
// and System.Security.Cryptography.DES fails there. Single blocks, encryption only; two blocks a connection,
// so the plain textbook form (FIPS 46-3) is fast enough.
internal static class VncDes
{
    // Bit positions count from 1 at the most significant bit, as the standard writes them.
    private static ReadOnlySpan<byte> InitialPermutation =>
    [
        58, 50, 42, 34, 26, 18, 10, 2, 60, 52, 44, 36, 28, 20, 12, 4,
        62, 54, 46, 38, 30, 22, 14, 6, 64, 56, 48, 40, 32, 24, 16, 8,
        57, 49, 41, 33, 25, 17, 9, 1, 59, 51, 43, 35, 27, 19, 11, 3,
        61, 53, 45, 37, 29, 21, 13, 5, 63, 55, 47, 39, 31, 23, 15, 7,
    ];

    private static ReadOnlySpan<byte> FinalPermutation =>
    [
        40, 8, 48, 16, 56, 24, 64, 32, 39, 7, 47, 15, 55, 23, 63, 31,
        38, 6, 46, 14, 54, 22, 62, 30, 37, 5, 45, 13, 53, 21, 61, 29,
        36, 4, 44, 12, 52, 20, 60, 28, 35, 3, 43, 11, 51, 19, 59, 27,
        34, 2, 42, 10, 50, 18, 58, 26, 33, 1, 41, 9, 49, 17, 57, 25,
    ];

    private static ReadOnlySpan<byte> Expansion =>
    [
        32, 1, 2, 3, 4, 5, 4, 5, 6, 7, 8, 9, 8, 9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
        16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25, 24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32, 1,
    ];

    private static ReadOnlySpan<byte> Permutation =>
    [
        16, 7, 20, 21, 29, 12, 28, 17, 1, 15, 23, 26, 5, 18, 31, 10,
        2, 8, 24, 14, 32, 27, 3, 9, 19, 13, 30, 6, 22, 11, 4, 25,
    ];

    private static ReadOnlySpan<byte> KeyChoice1 =>
    [
        57, 49, 41, 33, 25, 17, 9, 1, 58, 50, 42, 34, 26, 18, 10, 2, 59, 51, 43, 35, 27, 19, 11, 3, 60, 52, 44, 36,
        63, 55, 47, 39, 31, 23, 15, 7, 62, 54, 46, 38, 30, 22, 14, 6, 61, 53, 45, 37, 29, 21, 13, 5, 28, 20, 12, 4,
    ];

    private static ReadOnlySpan<byte> KeyChoice2 =>
    [
        14, 17, 11, 24, 1, 5, 3, 28, 15, 6, 21, 10, 23, 19, 12, 4, 26, 8, 16, 7, 27, 20, 13, 2,
        41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48, 44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
    ];

    private static ReadOnlySpan<byte> KeyShifts => [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1];

    // The eight substitution boxes, four rows of sixteen each.
    private static ReadOnlySpan<byte> Boxes =>
    [
        14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
        0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
        4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
        15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13,

        15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
        3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
        0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
        13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9,

        10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
        13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
        13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
        1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12,

        7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
        13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
        10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
        3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14,

        2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
        14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
        4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
        11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3,

        12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
        10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
        9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
        4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13,

        4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
        13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
        1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
        6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12,

        13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
        1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
        7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
        2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11,
    ];

    // The answer to a server's 16-byte challenge: both halves encrypted with the password as the key.
    public static void Respond(ReadOnlySpan<byte> password, ReadOnlySpan<byte> challenge, Span<byte> response)
    {
        // VNC takes the first eight bytes of the password, filled up with zeros - and reads the bits of each
        // byte in the opposite order to every other use of DES.
        Span<byte> key = stackalloc byte[8];
        key.Clear();
        for (var index = 0; index < 8 && index < password.Length; index++)
        {
            key[index] = Mirror(password[index]);
        }

        Span<ulong> subkeys = stackalloc ulong[16];
        Schedule(key, subkeys);
        for (var offset = 0; offset < 16; offset += 8)
        {
            var block = BinaryPrimitives.ReadUInt64BigEndian(challenge.Slice(offset, 8));
            BinaryPrimitives.WriteUInt64BigEndian(response.Slice(offset, 8), Encrypt(subkeys, block));
        }
    }

    // One block with a key in the usual bit order - what the published test vectors are written for.
    public static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block, Span<byte> output)
    {
        Span<ulong> subkeys = stackalloc ulong[16];
        Schedule(key, subkeys);
        BinaryPrimitives.WriteUInt64BigEndian(output, Encrypt(subkeys, BinaryPrimitives.ReadUInt64BigEndian(block)));
    }

    private static byte Mirror(byte value)
    {
        var mirrored = 0;
        for (var bit = 0; bit < 8; bit++)
        {
            mirrored = (mirrored << 1) | ((value >> bit) & 1);
        }

        return (byte)mirrored;
    }

    private static void Schedule(ReadOnlySpan<byte> key, Span<ulong> subkeys)
    {
        var chosen = Permute(BinaryPrimitives.ReadUInt64BigEndian(key), 64, KeyChoice1);
        var left = (uint)(chosen >> 28) & 0x0FFFFFFF;
        var right = (uint)chosen & 0x0FFFFFFF;
        for (var round = 0; round < 16; round++)
        {
            left = Rotate(left, KeyShifts[round]);
            right = Rotate(right, KeyShifts[round]);
            subkeys[round] = Permute(((ulong)left << 28) | right, 56, KeyChoice2);
        }
    }

    private static uint Rotate(uint half, int count) => ((half << count) | (half >> (28 - count))) & 0x0FFFFFFF;

    private static ulong Encrypt(ReadOnlySpan<ulong> subkeys, ulong block)
    {
        var permuted = Permute(block, 64, InitialPermutation);
        var left = (uint)(permuted >> 32);
        var right = (uint)permuted;
        foreach (var subkey in subkeys)
        {
            var next = left ^ Round(right, subkey);
            left = right;
            right = next;
        }

        // The halves change places once more before the last permutation.
        return Permute(((ulong)right << 32) | left, 64, FinalPermutation);
    }

    private static uint Round(uint half, ulong subkey)
    {
        var expanded = Permute(half, 32, Expansion) ^ subkey;
        uint substituted = 0;
        for (var box = 0; box < 8; box++)
        {
            var six = (int)(expanded >> (42 - box * 6)) & 0x3F;
            // The outer two bits pick the row, the inner four the column.
            var row = ((six & 0x20) >> 4) | (six & 1);
            var column = (six >> 1) & 0xF;
            substituted = (substituted << 4) | Boxes[box * 64 + row * 16 + column];
        }

        return (uint)Permute(substituted, 32, Permutation);
    }

    private static ulong Permute(ulong input, int inputBits, ReadOnlySpan<byte> table)
    {
        ulong output = 0;
        foreach (var position in table)
        {
            output = (output << 1) | ((input >> (inputBits - position)) & 1);
        }

        return output;
    }
}
