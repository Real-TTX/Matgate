using System.Runtime.InteropServices;

namespace Matgate.Display.Rdp.Graphics;

// Where a decoded bitmap lands: the part of it that the destination rectangle and the framebuffer both cover.
// The bitmap's top-left pixel belongs at (destLeft, destTop). The decoders address rows and columns of the bitmap
// and never compute a framebuffer offset themselves, so the clipping lives in this one place.
internal readonly ref struct BitmapTarget
{
    private readonly Span<byte> _framebuffer;
    private readonly int _framebufferWidth;
    private readonly int _left;
    private readonly int _top;

    private BitmapTarget(Span<byte> framebuffer, int framebufferWidth, int left, int top, int firstColumn, int columnCount, int firstRow, int rowCount)
    {
        _framebuffer = framebuffer;
        _framebufferWidth = framebufferWidth;
        _left = left;
        _top = top;
        FirstColumn = firstColumn;
        ColumnCount = columnCount;
        FirstRow = firstRow;
        RowCount = rowCount;
    }

    // The visible columns of the bitmap: [FirstColumn, FirstColumn + ColumnCount).
    public int FirstColumn { get; }

    public int ColumnCount { get; }

    // The visible rows of the bitmap, counted from its top: [FirstRow, FirstRow + RowCount).
    public int FirstRow { get; }

    public int RowCount { get; }

    // False when the rectangle or the framebuffer cannot be what they claim to be. A rectangle that lies entirely
    // outside the framebuffer is fine: the target is then empty and the decoder only checks the data.
    public static bool TryCreate(Span<byte> framebuffer, int framebufferWidth, int framebufferHeight, int width, int height,
        int destLeft, int destTop, int destRight, int destBottom, out BitmapTarget target)
    {
        target = default;
        // Counted in pixels: the product of two ints fits a long, four times that need not.
        if (framebufferWidth < 0 || framebufferHeight < 0 || (long)framebufferWidth * framebufferHeight > framebuffer.Length / 4)
        {
            return false;
        }

        // Inclusive bounds as on the wire; long because the caller's four ints may be anything.
        var rectangleWidth = (long)destRight - destLeft + 1;
        var rectangleHeight = (long)destBottom - destTop + 1;
        if (rectangleWidth <= 0 || rectangleHeight <= 0)
        {
            return false;
        }

        // Servers pad the bitmap (its width to a multiple of four): what lies beyond the rectangle is not shown.
        // A rectangle larger than its bitmap gets the pixels there are.
        var firstColumn = Math.Max(0L, -(long)destLeft);
        var endColumn = Math.Min(Math.Min(width, rectangleWidth), (long)framebufferWidth - destLeft);
        var firstRow = Math.Max(0L, -(long)destTop);
        var endRow = Math.Min(Math.Min(height, rectangleHeight), (long)framebufferHeight - destTop);
        if (endColumn <= firstColumn || endRow <= firstRow)
        {
            target = new BitmapTarget(framebuffer, framebufferWidth, 0, 0, 0, 0, 0, 0);
            return true;
        }

        target = new BitmapTarget(framebuffer, framebufferWidth, destLeft, destTop, (int)firstColumn, (int)(endColumn - firstColumn),
            (int)firstRow, (int)(endRow - firstRow));
        return true;
    }

    // The framebuffer pixels for the visible columns of one bitmap row (counted from the top of the bitmap);
    // empty when no part of the row is visible. Values go in through Bgrx.Store.
    public Span<uint> Row(int bitmapRow)
    {
        if (bitmapRow < FirstRow || bitmapRow >= FirstRow + RowCount)
        {
            return default;
        }

        // Inside the framebuffer by construction: TryCreate clipped rows and columns against it.
        var pixel = ((long)(_top + bitmapRow) * _framebufferWidth) + _left + FirstColumn;
        return MemoryMarshal.Cast<byte, uint>(_framebuffer.Slice((int)(pixel * 4), ColumnCount * 4));
    }
}
