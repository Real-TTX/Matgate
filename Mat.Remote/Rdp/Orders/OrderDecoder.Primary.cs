using System.Buffers;
using Matgate.Display.Rdp.Graphics;

namespace Matgate.Display.Rdp.Orders;

// Primary drawing orders (MS-RDPEGDI 2.2.2.2.1.1.2). Their encoding leaves out whatever did not change since the
// last order of the same kind: the order type (TS_TYPE_CHANGE), each field (fieldFlags, whose trailing zero bytes
// are left out too), the bounds (each side absolute, as a delta, or not at all; or all four unchanged), and
// coordinates may come as one-byte deltas. So the decoder keeps the last value of every field of every kind of
// order, the last order type and the last bounds - the Primary Drawing Order History of 3.2.1.1.
internal sealed partial class OrderDecoder
{
    // TS_ENC_*_ORDER.
    private const int DstBltType = 0x00;
    private const int PatBltType = 0x01;
    private const int ScrBltType = 0x02;
    private const int LineToType = 0x09;
    private const int OpaqueRectType = 0x0A;
    private const int MemBltType = 0x0D;
    private const int Mem3BltType = 0x0E;
    private const int MultiOpaqueRectType = 0x12;

    // A DELTA_RECTS_FIELD holds at most 45 rectangles: 23 bytes of zero bits and at most 2 bytes for each of the
    // four values of each.
    private const int MaxDeltaRectangles = 45;
    private const int MaxDeltaBytes = 23 + (MaxDeltaRectangles * 8);

    // Brush styles (BrushStyle of PatBlt and Mem3Blt).
    private const int SolidBrush = 0x00;
    private const int NullBrush = 0x01;
    private const int HatchedBrush = 0x02;
    private const int PatternBrush = 0x03;
    private const int CachedBrushFlag = 0x80;

    // The six hatch brushes (HS_HORIZONTAL to HS_DIAGCROSS) as GDI draws them: rows from the top, the leftmost pixel
    // in the high bit, a set bit is the hatch line.
    private static readonly byte[] Hatches =
    [
        0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00,
        0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
        0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01,
        0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80,
        0x08, 0x08, 0x08, 0xFF, 0x08, 0x08, 0x08, 0x08,
        0x81, 0x42, 0x24, 0x18, 0x18, 0x24, 0x42, 0x81,
    ];

    private DstBltFields _dstBlt = new();
    private PatBltFields _patBlt = new();
    private ScrBltFields _scrBlt = new();
    private OpaqueRectFields _opaqueRect = new();
    private MultiOpaqueRectFields _multiOpaqueRect = new();
    private MemBltFields _memBlt = new();
    private MemBltFields _mem3Blt = new();
    private LineToFields _lineTo = new();
    private int _orderType;
    private int _boundsLeft;
    private int _boundsTop;
    private int _boundsRight;
    private int _boundsBottom;

    // Drawing goes to an offscreen surface (Switch Surface): never announced, so there is none to draw into.
    private bool _offscreen;

    internal sealed class BrushFields
    {
        public int OriginX;
        public int OriginY;
        public int Style;
        public int Hatch;
        public readonly byte[] Extra = new byte[7];
    }

    internal sealed class DstBltFields
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int Rop;
    }

    internal sealed class PatBltFields
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int Rop;
        public int BackColor;
        public int ForeColor;
        public readonly BrushFields Brush = new();
    }

    internal sealed class ScrBltFields
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int Rop;
        public int SourceX;
        public int SourceY;
    }

    internal sealed class OpaqueRectFields
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int Color;
    }

    internal sealed class MultiOpaqueRectFields
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int Color;
        public int Count;
        public readonly byte[] Deltas = new byte[MaxDeltaBytes];

        // -1: the last list sent was longer than any valid one.
        public int DeltasLength;
    }

    // MemBlt, and Mem3Blt with the brush fields as well.
    internal sealed class MemBltFields
    {
        public int CacheId;
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int Rop;
        public int SourceX;
        public int SourceY;
        public int CacheIndex;
        public int BackColor;
        public int ForeColor;
        public readonly BrushFields Brush = new();
    }

    internal sealed class LineToFields
    {
        public int BackMode;
        public int StartX;
        public int StartY;
        public int EndX;
        public int EndY;
        public int BackColor;
        public int Rop2;
        public int PenStyle;
        public int PenWidth;
        public int PenColor;
    }

    // The initial state both sides agree on: PatBlt as the last order type, zero bounds, every field zero.
    private void ResetPrimary()
    {
        _orderType = PatBltType;
        _boundsLeft = 0;
        _boundsTop = 0;
        _boundsRight = 0;
        _boundsBottom = 0;
        _offscreen = false;
        _dstBlt = new DstBltFields();
        _patBlt = new PatBltFields();
        _scrBlt = new ScrBltFields();
        _opaqueRect = new OpaqueRectFields();
        _multiOpaqueRect = new MultiOpaqueRectFields();
        _memBlt = new MemBltFields();
        _mem3Blt = new MemBltFields();
        _lineTo = new LineToFields();
        _glyphIndex = new GlyphIndexFields();
    }

    // Field encoding bytes of each kind this decoder reads: ceil((fields + 1) / 8). Zero for the kinds it cannot.
    private static int FieldBytes(int orderType) => orderType switch
    {
        DstBltType => 1,
        PatBltType => 2,
        ScrBltType => 1,
        LineToType => 2,
        OpaqueRectType => 1,
        MemBltType => 2,
        Mem3BltType => 3,
        MultiOpaqueRectType => 2,
        GlyphIndexType => 3,
        _ => 0,
    };

    private static bool Has(uint fields, int number) => (fields & (1u << (number - 1))) != 0;

    // A Coord field (2.2.2.2.1.1.1.1): a signed 16-bit value, or a signed one-byte delta to the last one. The
    // result stays a 16-bit value, as on the wire.
    private static int Coord(ref OrderReader reader, bool delta, int previous) =>
        delta ? (short)(previous + reader.I8()) : reader.I16();

    private bool Primary(ref OrderReader reader, int control, ref Canvas canvas)
    {
        if ((control & TypeChangeFlag) != 0)
        {
            _orderType = reader.U8();
        }

        var fieldBytes = FieldBytes(_orderType);
        if (fieldBytes == 0)
        {
            if (!reader.Failed)
            {
                Count(_orderType & 0x1F);
                Report("primary " + _orderType, $"a primary order this client cannot read (type 0x{_orderType:X2}); the rest of its update is dropped");
            }

            return false;
        }

        // The zero flags count field bytes left out at the end because they are zero.
        var present = Math.Max(0, fieldBytes - ((control >> 6) & 0x03));
        var fields = 0u;
        for (var i = 0; i < present; i++)
        {
            fields |= (uint)reader.U8() << (8 * i);
        }

        if ((control & BoundsFlag) != 0)
        {
            if ((control & ZeroBoundsDeltasFlag) == 0)
            {
                ReadBounds(ref reader);
            }

            canvas.ClipToBounds(_boundsLeft, _boundsTop, _boundsRight, _boundsBottom);
        }
        else
        {
            canvas.ClipToScreen();
        }

        var delta = (control & DeltaCoordinatesFlag) != 0;
        var ok = _orderType switch
        {
            DstBltType => DstBlt(ref reader, fields, delta, ref canvas),
            PatBltType => PatBlt(ref reader, fields, delta, ref canvas),
            ScrBltType => ScrBlt(ref reader, fields, delta, ref canvas),
            LineToType => LineTo(ref reader, fields, delta, ref canvas),
            OpaqueRectType => OpaqueRect(ref reader, fields, delta, ref canvas),
            MemBltType => MemBlt(ref reader, fields, delta, ref canvas, _memBlt, false),
            Mem3BltType => MemBlt(ref reader, fields, delta, ref canvas, _mem3Blt, true),
            MultiOpaqueRectType => MultiOpaqueRect(ref reader, fields, delta, ref canvas),
            _ => GlyphIndex(ref reader, fields, ref canvas),
        };
        if (!ok)
        {
            Report("truncated primary", "a primary order that ends early; the rest of its update is dropped");
            return false;
        }

        Count(_orderType);
        return true;
    }

    // The bounds: a description byte, then left, top, right, bottom - each absolute (16 bits), a delta (8 bits) or
    // left as it was. Right and bottom are inclusive.
    private void ReadBounds(ref OrderReader reader)
    {
        var description = reader.U8();
        _boundsLeft = Bound(ref reader, description, 0x01, 0x10, _boundsLeft);
        _boundsTop = Bound(ref reader, description, 0x02, 0x20, _boundsTop);
        _boundsRight = Bound(ref reader, description, 0x04, 0x40, _boundsRight);
        _boundsBottom = Bound(ref reader, description, 0x08, 0x80, _boundsBottom);
    }

    private static int Bound(ref OrderReader reader, int description, int absolute, int delta, int previous)
    {
        // With both flags set the absolute one is to be ignored.
        if ((description & delta) != 0)
        {
            return (short)(previous + reader.I8());
        }

        return (description & absolute) != 0 ? reader.I16() : previous;
    }

    private void ReadRectangle(ref OrderReader reader, uint fields, bool delta, int first, ref int left, ref int top, ref int width, ref int height)
    {
        if (Has(fields, first))
        {
            left = Coord(ref reader, delta, left);
        }

        if (Has(fields, first + 1))
        {
            top = Coord(ref reader, delta, top);
        }

        if (Has(fields, first + 2))
        {
            width = Coord(ref reader, delta, width);
        }

        if (Has(fields, first + 3))
        {
            height = Coord(ref reader, delta, height);
        }
    }

    // BrushOrgX, BrushOrgY, BrushStyle, BrushHatch, BrushExtra as five consecutive fields from `first` on.
    private static void ReadBrush(ref OrderReader reader, uint fields, int first, BrushFields brush)
    {
        if (Has(fields, first))
        {
            brush.OriginX = reader.I8();
        }

        if (Has(fields, first + 1))
        {
            brush.OriginY = reader.I8();
        }

        if (Has(fields, first + 2))
        {
            brush.Style = reader.U8();
        }

        if (Has(fields, first + 3))
        {
            brush.Hatch = reader.U8();
        }

        if (Has(fields, first + 4))
        {
            reader.Bytes(7).CopyTo(brush.Extra);
        }
    }

    // DstBlt (2.2.2.2.1.1.2.1): nLeftRect, nTopRect, nWidth, nHeight, bRop.
    private bool DstBlt(ref OrderReader reader, uint fields, bool delta, ref Canvas canvas)
    {
        var order = _dstBlt;
        ReadRectangle(ref reader, fields, delta, 1, ref order.Left, ref order.Top, ref order.Width, ref order.Height);
        if (Has(fields, 5))
        {
            order.Rop = reader.U8();
        }

        if (reader.Failed)
        {
            return false;
        }

        if (Rop.UsesPattern(order.Rop) || Rop.UsesSource(order.Rop))
        {
            Report("rop dstblt", $"a DstBlt with raster operation 0x{order.Rop:X2}, which needs more than the destination; pattern and source count as black");
        }

        if (!_offscreen && canvas.Clip(order.Left, order.Top, order.Width, order.Height, out var x0, out var y0, out var x1, out var y1))
        {
            canvas.Fill(x0, y0, x1, y1, order.Rop, Pattern.None);
        }

        return true;
    }

    // PatBlt (2.2.2.2.1.1.2.3): nLeftRect, nTopRect, nWidth, nHeight, bRop, BackColor, ForeColor, then the brush.
    private bool PatBlt(ref OrderReader reader, uint fields, bool delta, ref Canvas canvas)
    {
        var order = _patBlt;
        ReadRectangle(ref reader, fields, delta, 1, ref order.Left, ref order.Top, ref order.Width, ref order.Height);
        if (Has(fields, 5))
        {
            order.Rop = reader.U8();
        }

        if (Has(fields, 6))
        {
            order.BackColor = reader.U24();
        }

        if (Has(fields, 7))
        {
            order.ForeColor = reader.U24();
        }

        ReadBrush(ref reader, fields, 8, order.Brush);
        if (reader.Failed)
        {
            return false;
        }

        if (_offscreen || !canvas.Clip(order.Left, order.Top, order.Width, order.Height, out var x0, out var y0, out var x1, out var y1))
        {
            return true;
        }

        if (Rop.UsesSource(order.Rop))
        {
            Report("rop patblt", $"a PatBlt with raster operation 0x{order.Rop:X2}, which needs a source; it counts as black");
        }

        Span<uint> cells = stackalloc uint[64];
        if (!TryBrush(order.Brush, order.BackColor, order.ForeColor, order.Rop, cells, out var solid, out var usable))
        {
            return true;
        }

        var pattern = solid is { } color ? Pattern.OfColor(color) : usable ? Pattern.OfBrush(cells, order.Brush.OriginX, order.Brush.OriginY) : Pattern.None;
        canvas.Fill(x0, y0, x1, y1, order.Rop, pattern);
        return true;
    }

    // The brush of a PatBlt or Mem3Blt as 64 pixels (or one colour). False when the order cannot be drawn: a brush
    // this client never announced (cached brushes) or does not know. usable false with true: the null brush, which
    // is fine as long as the operation ignores the pattern.
    private bool TryBrush(BrushFields brush, int backWire, int foreWire, int rop, Span<uint> cells, out uint? solid, out bool usable)
    {
        solid = null;
        usable = false;
        if ((brush.Style & CachedBrushFlag) != 0)
        {
            Report("cached brush", "a cached brush, which this client never announced; the order is skipped");
            return false;
        }

        switch (brush.Style)
        {
            case SolidBrush:
                solid = Color(foreWire);
                usable = true;
                return true;
            case NullBrush:
                if (Rop.UsesPattern(rop))
                {
                    // A hollow brush paints nothing.
                    return false;
                }

                return true;
            case HatchedBrush when brush.Hatch < 6:
                // The hatch lines in the foreground colour, the gaps in the background colour.
                FillCells(Hatches.AsSpan(brush.Hatch * 8, 8), Color(foreWire), Color(backWire), cells);
                usable = true;
                return true;
            case PatternBrush:
            {
                // BrushHatch is the bottom row, BrushExtra the seven above it from the bottom up. A set bit takes
                // the background colour, a clear one the foreground colour - GDI's rule for monochrome patterns.
                Span<byte> rows = stackalloc byte[8];
                for (var row = 0; row < 7; row++)
                {
                    rows[row] = brush.Extra[6 - row];
                }

                rows[7] = (byte)brush.Hatch;
                FillCells(rows, Color(backWire), Color(foreWire), cells);
                usable = true;
                return true;
            }

            default:
                Report("brush " + brush.Style, $"a brush of style 0x{brush.Style:X2} (hatch {brush.Hatch}), which this client does not know; the order is skipped");
                return false;
        }
    }

    // Eight rows of eight bits, the leftmost pixel in the high bit: `set` where a bit is one, `clear` elsewhere.
    private static void FillCells(ReadOnlySpan<byte> rows, uint set, uint clear, Span<uint> cells)
    {
        for (var row = 0; row < 8; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                cells[(row * 8) + column] = ((rows[row] >> (7 - column)) & 1) != 0 ? set : clear;
            }
        }
    }

    // ScrBlt (2.2.2.2.1.1.2.7): nLeftRect, nTopRect, nWidth, nHeight, bRop, nXSrc, nYSrc.
    private bool ScrBlt(ref OrderReader reader, uint fields, bool delta, ref Canvas canvas)
    {
        var order = _scrBlt;
        ReadRectangle(ref reader, fields, delta, 1, ref order.Left, ref order.Top, ref order.Width, ref order.Height);
        if (Has(fields, 5))
        {
            order.Rop = reader.U8();
        }

        if (Has(fields, 6))
        {
            order.SourceX = Coord(ref reader, delta, order.SourceX);
        }

        if (Has(fields, 7))
        {
            order.SourceY = Coord(ref reader, delta, order.SourceY);
        }

        if (reader.Failed)
        {
            return false;
        }

        if (Rop.UsesPattern(order.Rop))
        {
            Report("rop scrblt", $"a ScrBlt with raster operation 0x{order.Rop:X2}, which needs a pattern; it counts as black");
        }

        if (_offscreen || !canvas.Clip(order.Left, order.Top, order.Width, order.Height, out var x0, out var y0, out var x1, out var y1))
        {
            return true;
        }

        // Only where the source lies on the screen: the rest of the destination has no defined source.
        var offsetX = order.SourceX - order.Left;
        var offsetY = order.SourceY - order.Top;
        x0 = Math.Max(x0, -offsetX);
        y0 = Math.Max(y0, -offsetY);
        x1 = Math.Min(x1, canvas.Width - offsetX);
        y1 = Math.Min(y1, canvas.Height - offsetY);
        if (x1 > x0 && y1 > y0)
        {
            canvas.ScreenBlit(x0, y0, x1, y1, x0 + offsetX, y0 + offsetY, order.Rop, Pattern.None);
        }

        return true;
    }

    // OpaqueRect (2.2.2.2.1.1.2.5): nLeftRect, nTopRect, nWidth, nHeight, then the colour one byte a field.
    private bool OpaqueRect(ref OrderReader reader, uint fields, bool delta, ref Canvas canvas)
    {
        var order = _opaqueRect;
        ReadRectangle(ref reader, fields, delta, 1, ref order.Left, ref order.Top, ref order.Width, ref order.Height);
        order.Color = ReadColorBytes(ref reader, fields, 5, order.Color);
        if (reader.Failed)
        {
            return false;
        }

        if (!_offscreen && canvas.Clip(order.Left, order.Top, order.Width, order.Height, out var x0, out var y0, out var x1, out var y1))
        {
            canvas.Fill(x0, y0, x1, y1, Rop.PatCopy, Pattern.OfColor(Color(order.Color)));
        }

        return true;
    }

    // RedOrPaletteIndex, Green, Blue as three fields of one byte each, from `first` on.
    private static int ReadColorBytes(ref OrderReader reader, uint fields, int first, int color)
    {
        for (var i = 0; i < 3; i++)
        {
            if (Has(fields, first + i))
            {
                color = (color & ~(0xFF << (8 * i))) | (reader.U8() << (8 * i));
            }
        }

        return color;
    }

    // MultiOpaqueRect (2.2.2.2.1.1.2.6): nLeftRect, nTopRect, nWidth, nHeight, the colour, nDeltaEntries,
    // CodedDeltaList. The delta rectangles are what is filled; the first four fields only bound them.
    private bool MultiOpaqueRect(ref OrderReader reader, uint fields, bool delta, ref Canvas canvas)
    {
        var order = _multiOpaqueRect;
        ReadRectangle(ref reader, fields, delta, 1, ref order.Left, ref order.Top, ref order.Width, ref order.Height);
        order.Color = ReadColorBytes(ref reader, fields, 5, order.Color);
        if (Has(fields, 8))
        {
            order.Count = reader.U8();
        }

        if (Has(fields, 9))
        {
            // VARIABLE2_FIELD: two bytes of length, then the DELTA_RECTS_FIELD.
            int length = reader.U16();
            var list = reader.Bytes(length);
            if (length <= MaxDeltaBytes)
            {
                list.CopyTo(order.Deltas);
                order.DeltasLength = length;
            }
            else
            {
                order.DeltasLength = -1;
            }
        }

        if (reader.Failed)
        {
            return false;
        }

        if (order.Count > MaxDeltaRectangles || order.DeltasLength < 0)
        {
            Report("multiopaquerect", "a MultiOpaqueRect with more rectangles than the specification allows; it is skipped");
            return true;
        }

        Span<int> rectangles = stackalloc int[MaxDeltaRectangles * 4];
        if (!DeltaRectangles(order.Deltas.AsSpan(0, order.DeltasLength), order.Count, rectangles))
        {
            Report("multiopaquerect list", "a MultiOpaqueRect whose rectangles end early; it is skipped");
            return true;
        }

        if (_offscreen)
        {
            return true;
        }

        var pattern = Pattern.OfColor(Color(order.Color));
        for (var i = 0; i < order.Count; i++)
        {
            if (canvas.Clip(rectangles[i * 4], rectangles[(i * 4) + 1], rectangles[(i * 4) + 2], rectangles[(i * 4) + 3], out var x0, out var y0, out var x1, out var y1))
            {
                canvas.Fill(x0, y0, x1, y1, Rop.PatCopy, pattern);
            }
        }

        return true;
    }

    // DELTA_RECTS_FIELD (2.2.2.2.1.1.1.5) into (left, top, width, height) quadruples. Left and top are deltas to the
    // previous rectangle, width and height are values of their own; a zero bit keeps the previous value. The
    // rectangle before the first is (0, 0, 0, 0).
    private static bool DeltaRectangles(ReadOnlySpan<byte> list, int count, Span<int> rectangles)
    {
        var reader = new OrderReader(list);
        var zeroBits = reader.Bytes((count + 1) / 2);
        int left = 0, top = 0, width = 0, height = 0;
        for (var i = 0; i < count && !reader.Failed; i++)
        {
            var flags = zeroBits[i / 2] << (4 * (i % 2));
            if ((flags & 0x80) == 0)
            {
                left += reader.DeltaValue();
            }

            if ((flags & 0x40) == 0)
            {
                top += reader.DeltaValue();
            }

            if ((flags & 0x20) == 0)
            {
                width = reader.DeltaValue();
            }

            if ((flags & 0x10) == 0)
            {
                height = reader.DeltaValue();
            }

            rectangles[i * 4] = left;
            rectangles[(i * 4) + 1] = top;
            rectangles[(i * 4) + 2] = width;
            rectangles[(i * 4) + 3] = height;
        }

        return !reader.Failed;
    }

    // MemBlt (2.2.2.2.1.1.2.9): cacheId, nLeftRect, nTopRect, nWidth, nHeight, bRop, nXSrc, nYSrc, cacheIndex.
    // Mem3Blt (2.2.2.2.1.1.2.10) has BackColor, ForeColor and the brush between nYSrc and cacheIndex.
    private bool MemBlt(ref OrderReader reader, uint fields, bool delta, ref Canvas canvas, MemBltFields order, bool withBrush)
    {
        if (Has(fields, 1))
        {
            order.CacheId = reader.U16();
        }

        ReadRectangle(ref reader, fields, delta, 2, ref order.Left, ref order.Top, ref order.Width, ref order.Height);
        if (Has(fields, 6))
        {
            order.Rop = reader.U8();
        }

        if (Has(fields, 7))
        {
            order.SourceX = Coord(ref reader, delta, order.SourceX);
        }

        if (Has(fields, 8))
        {
            order.SourceY = Coord(ref reader, delta, order.SourceY);
        }

        var indexField = 9;
        if (withBrush)
        {
            if (Has(fields, 9))
            {
                order.BackColor = reader.U24();
            }

            if (Has(fields, 10))
            {
                order.ForeColor = reader.U24();
            }

            ReadBrush(ref reader, fields, 11, order.Brush);
            indexField = 16;
        }

        if (Has(fields, indexField))
        {
            order.CacheIndex = reader.U16();
        }

        if (reader.Failed)
        {
            return false;
        }

        if (_offscreen)
        {
            return true;
        }

        if (!withBrush)
        {
            if (Rop.UsesPattern(order.Rop))
            {
                Report("rop memblt", $"a MemBlt with raster operation 0x{order.Rop:X2}, which needs a pattern; it counts as black");
            }

            DrawCached(ref canvas, order, Pattern.None);
            return true;
        }

        Span<uint> cells = stackalloc uint[64];
        if (!TryBrush(order.Brush, order.BackColor, order.ForeColor, order.Rop, cells, out var solid, out var usable))
        {
            return true;
        }

        var pattern = solid is { } color ? Pattern.OfColor(color) : usable ? Pattern.OfBrush(cells, order.Brush.OriginX, order.Brush.OriginY) : Pattern.None;
        DrawCached(ref canvas, order, pattern);
        return true;
    }

    // The cached bitmap of a MemBlt or Mem3Blt onto the screen. nXSrc and nYSrc count from the bitmap's top-left
    // corner: the specification calls nYSrc inverted because Windows keeps bitmaps bottom-up, but measured from the
    // top it is the plain offset, which is how Windows, xrdp and FreeRDP use it.
    private void DrawCached(ref Canvas canvas, MemBltFields order, scoped Pattern pattern)
    {
        var cacheId = order.CacheId & 0xFF;
        if (cacheId == 0xFF)
        {
            Report("offscreen memblt", "a MemBlt from an offscreen bitmap, which this client never announced; it is skipped");
            return;
        }

        var bitmap = _cache.Get(cacheId, order.CacheIndex);
        if (bitmap is null)
        {
            Report("missing bitmap", $"a MemBlt of a bitmap that is not in the cache (cache {cacheId}, entry {order.CacheIndex}); it is skipped");
            return;
        }

        if (!canvas.Clip(order.Left, order.Top, order.Width, order.Height, out var x0, out var y0, out var x1, out var y1))
        {
            return;
        }

        // Only where the source lies inside the bitmap.
        var offsetX = order.SourceX - order.Left;
        var offsetY = order.SourceY - order.Top;
        x0 = Math.Max(x0, -offsetX);
        y0 = Math.Max(y0, -offsetY);
        x1 = Math.Min(x1, bitmap.Width - offsetX);
        y1 = Math.Min(y1, bitmap.Height - offsetY);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var first = ((y0 + offsetY) * bitmap.Width) + x0 + offsetX;
        if (!bitmap.Indexed)
        {
            canvas.Blit(x0, y0, x1, y1, bitmap.Pixels, first, bitmap.Width, order.Rop, pattern);
            return;
        }

        // An 8-bit bitmap: through the colour table the MemBlt names, or the session's palette when the server
        // never sent that table.
        var table = order.CacheId >> 8;
        var palette = table < ColorTables && _colorTables[table].Length == 256 ? _colorTables[table] : _palette;
        var width = x1 - x0;
        var row = ArrayPool<uint>.Shared.Rent(width);
        try
        {
            for (var y = y0; y < y1; y++)
            {
                var source = bitmap.Pixels.AsSpan(first + ((y - y0) * bitmap.Width), width);
                for (var x = 0; x < width; x++)
                {
                    row[x] = Bgrx.Store(palette[(int)(Bgrx.Store(source[x]) & 0xFF)] | Bgrx.Opaque);
                }

                canvas.BlitRow(x0, y, row.AsSpan(0, width), order.Rop, pattern);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(row);
        }

        canvas.MarkDrawn(x0, y0, x1, y1);
    }

    // LineTo (2.2.2.2.1.1.2.11): BackMode, nXStart, nYStart, nXEnd, nYEnd, BackColor, bRop2, PenStyle, PenWidth,
    // PenColor. Always a solid line one pixel wide, which is all the order may describe.
    private bool LineTo(ref OrderReader reader, uint fields, bool delta, ref Canvas canvas)
    {
        var order = _lineTo;
        if (Has(fields, 1))
        {
            order.BackMode = reader.U16();
        }

        ReadRectangle(ref reader, fields, delta, 2, ref order.StartX, ref order.StartY, ref order.EndX, ref order.EndY);
        if (Has(fields, 6))
        {
            order.BackColor = reader.U24();
        }

        if (Has(fields, 7))
        {
            order.Rop2 = reader.U8();
        }

        if (Has(fields, 8))
        {
            order.PenStyle = reader.U8();
        }

        if (Has(fields, 9))
        {
            order.PenWidth = reader.U8();
        }

        if (Has(fields, 10))
        {
            order.PenColor = reader.U24();
        }

        if (reader.Failed)
        {
            return false;
        }

        var rop2 = order.Rop2;
        if (rop2 is < 1 or > 16)
        {
            Report("rop2", $"a LineTo with binary raster operation {rop2}, which does not exist; it is drawn as R2_COPYPEN");
            rop2 = 13;
        }

        if (!_offscreen)
        {
            canvas.Line(order.StartX, order.StartY, order.EndX, order.EndY, Rop.FromRop2(rop2), Color(order.PenColor));
        }

        return true;
    }
}
