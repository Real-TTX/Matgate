using System.Runtime.CompilerServices;

namespace Matgate.Display.Rdp.Orders;

// Raster operations. A ternary raster operation index (ROP3, MS-RDPEGDI 2.2.2.2.1.1.1.7) is the truth table of
// the operation itself: bit (P * 4 + S * 2 + D) of the index is the result for that combination of pattern,
// source and destination bit. So every one of the 256 operations is evaluated exactly, bit by bit on whole pixels,
// without a table of formulas - the common ones get a faster path in Canvas.
internal static class Rop
{
    public const int Blackness = 0x00;
    public const int DstInvert = 0x55;
    public const int PatInvert = 0x5A;
    public const int Destination = 0xAA;
    public const int SrcCopy = 0xCC;
    public const int PatCopy = 0xF0;
    public const int Whiteness = 0xFF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Apply(int rop, uint pattern, uint source, uint destination)
    {
        var result = 0u;
        if ((rop & 0x01) != 0)
        {
            result |= ~pattern & ~source & ~destination;
        }

        if ((rop & 0x02) != 0)
        {
            result |= ~pattern & ~source & destination;
        }

        if ((rop & 0x04) != 0)
        {
            result |= ~pattern & source & ~destination;
        }

        if ((rop & 0x08) != 0)
        {
            result |= ~pattern & source & destination;
        }

        if ((rop & 0x10) != 0)
        {
            result |= pattern & ~source & ~destination;
        }

        if ((rop & 0x20) != 0)
        {
            result |= pattern & ~source & destination;
        }

        if ((rop & 0x40) != 0)
        {
            result |= pattern & source & ~destination;
        }

        if ((rop & 0x80) != 0)
        {
            result |= pattern & source & destination;
        }

        return result;
    }

    // Whether the result depends on the pattern: the half of the table with P set differs from the half without.
    public static bool UsesPattern(int rop) => ((rop >> 4) & 0x0F) != (rop & 0x0F);

    public static bool UsesSource(int rop) => ((rop >> 2) & 0x33) != (rop & 0x33);

    public static bool UsesDestination(int rop) => ((rop >> 1) & 0x55) != (rop & 0x55);

    // A binary raster operation (ROP2, 2.2.2.2.1.1.1.6, values 1 to 16) as the ternary one that does the same with
    // the pen as the pattern. A ROP2 code minus one is its own truth table over (P * 2 + D).
    public static int FromRop2(int rop2)
    {
        var table = rop2 - 1;
        var rop3 = 0;
        for (var index = 0; index < 8; index++)
        {
            var pattern = (index >> 2) & 1;
            var destination = index & 1;
            if (((table >> ((pattern * 2) + destination)) & 1) != 0)
            {
                rop3 |= 1 << index;
            }
        }

        return rop3;
    }
}
