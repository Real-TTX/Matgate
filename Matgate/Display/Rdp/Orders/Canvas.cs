using System.Buffers;
using System.Runtime.InteropServices;
using Matgate.Display.Rdp.Graphics;

namespace Matgate.Display.Rdp.Orders;

// The pattern of a drawing order: one colour, or an 8 x 8 brush anchored at its origin. Colours are framebuffer
// pixels as stored (Bgrx.Store), opaque.
internal readonly ref struct Pattern
{
    private readonly ReadOnlySpan<uint> _cells;
    private readonly int _originX;
    private readonly int _originY;

    private Pattern(uint solid, ReadOnlySpan<uint> cells, int originX, int originY)
    {
        Solid = solid;
        _cells = cells;
        _originX = originX;
        _originY = originY;
    }

    // No pattern at all (the orders without a brush): every pattern bit is zero.
    public static Pattern None => new(0, default, 0, 0);

    public static Pattern OfColor(uint color) => new(color, default, 0, 0);

    // cells: 64 pixels, row by row from the top.
    public static Pattern OfBrush(ReadOnlySpan<uint> cells, int originX, int originY) => new(0, cells, originX, originY);

    public bool IsSolid => _cells.IsEmpty;

    public uint Solid { get; }

    public uint At(int x, int y) => _cells.IsEmpty ? Solid : _cells[(((y - _originY) & 7) * 8) + ((x - _originX) & 7)];
}

// The framebuffer as the drawing orders see it: pixels as uints, a clipping rectangle (the framebuffer, narrowed
// by an order's bounds), and the bounding box of everything drawn. Every method takes rectangles that Clip has
// already cut down, and Clip is the one place where the server's coordinates meet the framebuffer's size - so no
// write can land outside it, whatever the server sends.
internal ref struct Canvas
{
    private readonly Span<uint> _pixels;
    private int _clipLeft;
    private int _clipTop;
    private int _clipRight;
    private int _clipBottom;
    private int _dirtyLeft;
    private int _dirtyTop;
    private int _dirtyRight;
    private int _dirtyBottom;

    // framebuffer: BGRX, 4 bytes a pixel, top-down, stride width * 4. A framebuffer that cannot be what width and
    // height claim becomes an empty canvas: the orders are still read (the caches must stay in step with the
    // server), nothing is drawn.
    public Canvas(Span<byte> framebuffer, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 > framebuffer.Length)
        {
            width = 0;
            height = 0;
        }

        Width = width;
        Height = height;
        _pixels = MemoryMarshal.Cast<byte, uint>(framebuffer[..(width * height * 4)]);
        _clipRight = width;
        _clipBottom = height;
        _dirtyLeft = int.MaxValue;
        _dirtyTop = int.MaxValue;
        _dirtyRight = int.MinValue;
        _dirtyBottom = int.MinValue;
    }

    public readonly int Width { get; }

    public readonly int Height { get; }

    // The X byte as stored: every pixel written is opaque.
    private static uint OpaqueBits => Bgrx.Store(Bgrx.Opaque);

    public void ClipToScreen()
    {
        _clipLeft = 0;
        _clipTop = 0;
        _clipRight = Width;
        _clipBottom = Height;
    }

    // Bounds as on the wire: right and bottom inclusive.
    public void ClipToBounds(int left, int top, int right, int bottom)
    {
        _clipLeft = Math.Clamp(left, 0, Width);
        _clipTop = Math.Clamp(top, 0, Height);
        _clipRight = (int)Math.Clamp(right + 1L, _clipLeft, Width);
        _clipBottom = (int)Math.Clamp(bottom + 1L, _clipTop, Height);
    }

    // Narrows the clip further to [left, right) x [top, bottom), for the rest of the current order.
    public void NarrowClip(long left, long top, long right, long bottom)
    {
        _clipLeft = (int)Math.Clamp(left, _clipLeft, _clipRight);
        _clipTop = (int)Math.Clamp(top, _clipTop, _clipBottom);
        _clipRight = (int)Math.Clamp(right, _clipLeft, _clipRight);
        _clipBottom = (int)Math.Clamp(bottom, _clipTop, _clipBottom);
    }

    // The part of the rectangle (x, y, width, height) inside the clip, as [x0, x1) x [y0, y1). False when empty.
    public readonly bool Clip(long x, long y, long width, long height, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = (int)Math.Clamp(x, _clipLeft, _clipRight);
        y0 = (int)Math.Clamp(y, _clipTop, _clipBottom);
        x1 = (int)Math.Clamp(x + width, x0, _clipRight);
        y1 = (int)Math.Clamp(y + height, y0, _clipBottom);
        return x1 > x0 && y1 > y0;
    }

    public readonly bool TryGetDirty(out int x, out int y, out int width, out int height)
    {
        x = _dirtyLeft;
        y = _dirtyTop;
        width = _dirtyRight - _dirtyLeft;
        height = _dirtyBottom - _dirtyTop;
        return _dirtyRight > _dirtyLeft && _dirtyBottom > _dirtyTop;
    }

    private void Touch(int x0, int y0, int x1, int y1)
    {
        _dirtyLeft = Math.Min(_dirtyLeft, x0);
        _dirtyTop = Math.Min(_dirtyTop, y0);
        _dirtyRight = Math.Max(_dirtyRight, x1);
        _dirtyBottom = Math.Max(_dirtyBottom, y1);
    }

    private readonly Span<uint> Row(int y, int x0, int x1) => _pixels.Slice((y * Width) + x0, x1 - x0);

    // Pattern and destination only: DstBlt, PatBlt, OpaqueRect, MultiOpaqueRect. [x0, x1) x [y0, y1) from Clip.
    public void Fill(int x0, int y0, int x1, int y1, int rop, scoped Pattern pattern)
    {
        if (rop == Rop.Destination)
        {
            return;
        }

        for (var y = y0; y < y1; y++)
        {
            Operate(Row(y, x0, x1), default, rop, pattern, x0, y);
        }

        Touch(x0, y0, x1, y1);
    }

    // A source in memory of its own (a cached bitmap): the source pixel for (x0, y0) is source[offset], rows
    // `stride` apart. The caller has clipped the rectangle to the source as well.
    public void Blit(int x0, int y0, int x1, int y1, ReadOnlySpan<uint> source, int offset, int stride, int rop, scoped Pattern pattern)
    {
        if (rop == Rop.Destination)
        {
            return;
        }

        var width = x1 - x0;
        for (var y = y0; y < y1; y++)
        {
            Operate(Row(y, x0, x1), source.Slice(offset + ((y - y0) * stride), width), rop, pattern, x0, y);
        }

        Touch(x0, y0, x1, y1);
    }

    // The same with one row of source at a time, for sources that need converting first (8-bit cached bitmaps).
    public readonly void BlitRow(int x0, int y, ReadOnlySpan<uint> sourceRow, int rop, scoped Pattern pattern)
    {
        Operate(Row(y, x0, x0 + sourceRow.Length), sourceRow, rop, pattern, x0, y);
    }

    public void MarkDrawn(int x0, int y0, int x1, int y1) => Touch(x0, y0, x1, y1);

    // ScrBlt: the source is the framebuffer itself, at (sourceX, sourceY) for (x0, y0); it may overlap the
    // destination. The caller has clipped both rectangles to the framebuffer.
    public void ScreenBlit(int x0, int y0, int x1, int y1, int sourceX, int sourceY, int rop, scoped Pattern pattern)
    {
        if (rop == Rop.Destination)
        {
            return;
        }

        var width = x1 - x0;
        var height = y1 - y0;
        if (rop == Rop.SrcCopy)
        {
            // Rows in the order that never reads a row already overwritten; within a row CopyTo copies as if
            // through a temporary buffer.
            if (y0 <= sourceY)
            {
                for (var row = 0; row < height; row++)
                {
                    Row(sourceY + row, sourceX, sourceX + width).CopyTo(Row(y0 + row, x0, x1));
                }
            }
            else
            {
                for (var row = height - 1; row >= 0; row--)
                {
                    Row(sourceY + row, sourceX, sourceX + width).CopyTo(Row(y0 + row, x0, x1));
                }
            }

            Touch(x0, y0, x1, y1);
            return;
        }

        // Any other operation reads source and destination together: the source goes aside first.
        var copy = ArrayPool<uint>.Shared.Rent(width * height);
        try
        {
            for (var row = 0; row < height; row++)
            {
                Row(sourceY + row, sourceX, sourceX + width).CopyTo(copy.AsSpan(row * width, width));
            }

            for (var row = 0; row < height; row++)
            {
                Operate(Row(y0 + row, x0, x1), copy.AsSpan(row * width, width), rop, pattern, x0, y0 + row);
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(copy);
        }

        Touch(x0, y0, x1, y1);
    }

    // LineTo: a one-pixel line from start to end, the end point itself not drawn (as GDI's LineTo), each pixel
    // combined with the pen by the operation; every pixel is checked against the clip.
    public void Line(int startX, int startY, int endX, int endY, int rop, uint pen)
    {
        if (rop == Rop.Destination)
        {
            return;
        }

        var opaque = OpaqueBits;
        var x = startX;
        var y = startY;
        var deltaX = Math.Abs(endX - startX);
        var deltaY = -Math.Abs(endY - startY);
        var stepX = startX < endX ? 1 : -1;
        var stepY = startY < endY ? 1 : -1;
        var error = deltaX + deltaY;
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        while (x != endX || y != endY)
        {
            if (x >= _clipLeft && x < _clipRight && y >= _clipTop && y < _clipBottom)
            {
                ref var pixel = ref _pixels[(y * Width) + x];
                pixel = Rop.Apply(rop, pen, 0, pixel) | opaque;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }

            var doubled = 2 * error;
            if (doubled >= deltaY)
            {
                error += deltaY;
                x += stepX;
            }

            if (doubled <= deltaX)
            {
                error += deltaX;
                y += stepY;
            }
        }

        if (right > left)
        {
            Touch(left, top, right, bottom);
        }
    }

    // A glyph with its top-left corner at (left, top): its set bits in `color`, the rest left as it is.
    public void Glyph(long left, long top, Glyph glyph, uint color)
    {
        if (!Clip(left, top, glyph.Width, glyph.Height, out var x0, out var y0, out var x1, out var y1))
        {
            return;
        }

        var bits = glyph.Bits;
        var stride = glyph.Stride;
        for (var y = y0; y < y1; y++)
        {
            var row = Row(y, x0, x1);
            var bitRow = (int)(y - top) * stride;
            for (var x = x0; x < x1; x++)
            {
                var column = (int)(x - left);
                if ((bits[bitRow + (column >> 3)] & (0x80 >> (column & 7))) != 0)
                {
                    row[x - x0] = color;
                }
            }
        }

        Touch(x0, y0, x1, y1);
    }

    // One row of any operation. source: the source pixels for this row, empty for the orders without one. x, y:
    // where the row starts on the screen, for the brush's alignment.
    private static void Operate(Span<uint> destination, ReadOnlySpan<uint> source, int rop, scoped Pattern pattern, int x, int y)
    {
        var opaque = OpaqueBits;
        switch (rop)
        {
            case Rop.Blackness:
                destination.Fill(opaque);
                return;
            case Rop.Whiteness:
                destination.Fill(uint.MaxValue);
                return;
            case Rop.SrcCopy when !source.IsEmpty:
                // Sources are opaque already: cached bitmaps are decoded so, the framebuffer is kept so.
                source.CopyTo(destination);
                return;
            case Rop.PatCopy when pattern.IsSolid:
                destination.Fill(pattern.Solid | opaque);
                return;
            case Rop.DstInvert:
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = ~destination[i] | opaque;
                }

                return;
            case Rop.PatInvert when pattern.IsSolid:
                var solid = pattern.Solid;
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (destination[i] ^ solid) | opaque;
                }

                return;
        }

        for (var i = 0; i < destination.Length; i++)
        {
            var sourcePixel = source.IsEmpty ? 0u : source[i];
            destination[i] = Rop.Apply(rop, pattern.At(x + i, y), sourcePixel, destination[i]) | opaque;
        }
    }
}
