namespace Matgate.Display.Rdp.Orders;

// Text: the GlyphIndex primary order (MS-RDPEGDI 2.2.2.2.1.1.2.13) and the Cache Glyph secondary orders
// (2.2.2.2.1.2.5, 2.2.2.2.1.2.6). Never announced - Windows therefore sends text as bitmaps - but xrdp draws its
// own windows with them regardless, so they are read and drawn.
internal sealed partial class OrderDecoder
{
    private const int GlyphIndexType = 0x1B;
    private const int CacheGlyph = 0x03;

    // flAccel.
    private const int VerticalText = 0x04;
    private const int CharIncEqualsWidth = 0x20;

    // Fragment operations in the glyph bytes.
    private const int FragmentAdd = 0xFF;
    private const int FragmentUse = 0xFE;

    private readonly GlyphCache _glyphs = new();
    private GlyphIndexFields _glyphIndex = new();

    internal sealed class GlyphIndexFields
    {
        public int CacheId;
        public int Accel;
        public int CharInc;
        public int OpaqueRedundant;
        public int BackColor;
        public int ForeColor;
        public int BackgroundLeft;
        public int BackgroundTop;
        public int BackgroundRight;
        public int BackgroundBottom;
        public int OpaqueLeft;
        public int OpaqueTop;
        public int OpaqueRight;
        public int OpaqueBottom;
        public readonly BrushFields Brush = new();
        public int X;
        public int Y;
        public readonly byte[] Data = new byte[255];
        public int DataLength;
    }

    // GlyphIndex: cacheId, flAccel, ulCharInc, fOpRedundant, BackColor (the text), ForeColor (the opaque
    // rectangle), BkLeft/Top/Right/Bottom, OpLeft/Top/Right/Bottom, the brush (always solid, unused), X, Y and the
    // glyph bytes. None of its fields is a Coord field: they are 16-bit values even with TS_DELTA_COORDINATES.
    private bool GlyphIndex(ref OrderReader reader, uint fields, ref Canvas canvas)
    {
        var order = _glyphIndex;
        if (Has(fields, 1))
        {
            order.CacheId = reader.U8();
        }

        if (Has(fields, 2))
        {
            order.Accel = reader.U8();
        }

        if (Has(fields, 3))
        {
            order.CharInc = reader.U8();
        }

        if (Has(fields, 4))
        {
            order.OpaqueRedundant = reader.U8();
        }

        if (Has(fields, 5))
        {
            order.BackColor = reader.U24();
        }

        if (Has(fields, 6))
        {
            order.ForeColor = reader.U24();
        }

        ReadValue(ref reader, fields, 7, ref order.BackgroundLeft);
        ReadValue(ref reader, fields, 8, ref order.BackgroundTop);
        ReadValue(ref reader, fields, 9, ref order.BackgroundRight);
        ReadValue(ref reader, fields, 10, ref order.BackgroundBottom);
        ReadValue(ref reader, fields, 11, ref order.OpaqueLeft);
        ReadValue(ref reader, fields, 12, ref order.OpaqueTop);
        ReadValue(ref reader, fields, 13, ref order.OpaqueRight);
        ReadValue(ref reader, fields, 14, ref order.OpaqueBottom);
        ReadBrush(ref reader, fields, 15, order.Brush);
        ReadValue(ref reader, fields, 20, ref order.X);
        ReadValue(ref reader, fields, 21, ref order.Y);
        if (Has(fields, 22))
        {
            // VARIABLE1_FIELD: one byte of length, then the bytes.
            int length = reader.U8();
            reader.Bytes(length).CopyTo(order.Data);
            order.DataLength = length;
        }

        if (reader.Failed)
        {
            return false;
        }

        if (!_offscreen)
        {
            DrawText(ref canvas, order);
        }

        return true;
    }

    private static void ReadValue(ref OrderReader reader, uint fields, int number, ref int value)
    {
        if (Has(fields, number))
        {
            value = reader.I16();
        }
    }

    // The opaque rectangle first (the background rectangle when fOpRedundant says they are the same), then the
    // glyphs over it in the text colour, clipped to the background rectangle. Rectangles are inclusive.
    private void DrawText(ref Canvas canvas, GlyphIndexFields order)
    {
        var (left, top, right, bottom) = order.OpaqueRedundant != 0
            ? (order.BackgroundLeft, order.BackgroundTop, order.BackgroundRight, order.BackgroundBottom)
            : (order.OpaqueLeft, order.OpaqueTop, order.OpaqueRight, order.OpaqueBottom);
        if (right > left && bottom > top
            && canvas.Clip(left, top, right - left + 1L, bottom - top + 1L, out var x0, out var y0, out var x1, out var y1))
        {
            canvas.Fill(x0, y0, x1, y1, Rop.PatCopy, Pattern.OfColor(Color(order.ForeColor)));
        }

        if (order.BackgroundRight > order.BackgroundLeft && order.BackgroundBottom > order.BackgroundTop)
        {
            canvas.NarrowClip(order.BackgroundLeft, order.BackgroundTop, order.BackgroundRight + 1L, order.BackgroundBottom + 1L);
        }

        var color = Color(order.BackColor);
        var x = order.X;
        var y = order.Y;
        ReadOnlySpan<byte> text = order.Data.AsSpan(0, order.DataLength);
        var i = 0;
        while (i < text.Length)
        {
            switch (text[i])
            {
                case FragmentAdd:
                {
                    // The glyph bytes since the start of this run become a fragment: index, size.
                    if (i + 2 >= text.Length)
                    {
                        Report("fragment add", "a GlyphIndex order with a broken fragment; the rest of its text is dropped");
                        return;
                    }

                    var size = text[i + 2];
                    if (size <= i)
                    {
                        _glyphs.PutFragment(text[i + 1], text[..size]);
                    }

                    text = text[(i + 3)..];
                    i = 0;
                    break;
                }

                case FragmentUse:
                {
                    if (i + 1 >= text.Length)
                    {
                        Report("fragment use", "a GlyphIndex order with a broken fragment; the rest of its text is dropped");
                        return;
                    }

                    var fragment = _glyphs.GetFragment(text[i + 1]);
                    var used = 2;
                    if (UsesDeltas(order) && i + 2 < text.Length)
                    {
                        // The distance to this fragment. It applies when the fragment's own first glyph does not
                        // say where it goes (a delta of zero) - as rdesktop found Windows to send it.
                        used = 3;
                        if (fragment is { Length: > 1 } && fragment[1] == 0)
                        {
                            Advance(order, text[i + 2], ref x, ref y);
                        }
                    }

                    if (fragment is null)
                    {
                        Report("missing fragment", "a GlyphIndex order uses a glyph fragment that was never sent");
                    }
                    else
                    {
                        DrawGlyphs(ref canvas, order, fragment, color, ref x, ref y);
                    }

                    text = text[Math.Min(i + used, text.Length)..];
                    i = 0;
                    break;
                }

                default:
                    i = DrawGlyph(ref canvas, order, text, i, color, ref x, ref y);
                    break;
            }
        }
    }

    private static bool UsesDeltas(GlyphIndexFields order) => order.CharInc == 0 && (order.Accel & CharIncEqualsWidth) == 0;

    private static void Advance(GlyphIndexFields order, int distance, ref int x, ref int y)
    {
        if ((order.Accel & VerticalText) != 0)
        {
            y += distance;
        }
        else
        {
            x += distance;
        }
    }

    // The glyphs of a fragment, which hold no fragment operations of their own.
    private void DrawGlyphs(ref Canvas canvas, GlyphIndexFields order, ReadOnlySpan<byte> glyphs, uint color, ref int x, ref int y)
    {
        var i = 0;
        while (i < glyphs.Length && glyphs[i] < FragmentUse)
        {
            i = DrawGlyph(ref canvas, order, glyphs, i, color, ref x, ref y);
        }
    }

    // The glyph whose index is at bytes[i], with its delta when the text has them; returns where the next one is.
    private int DrawGlyph(ref Canvas canvas, GlyphIndexFields order, ReadOnlySpan<byte> bytes, int i, uint color, ref int x, ref int y)
    {
        var glyph = _glyphs.Get(order.CacheId, bytes[i]);
        i++;
        if (UsesDeltas(order) && i < bytes.Length)
        {
            // The distance from the previous glyph; 0x80 announces two more bytes with a larger one.
            int distance = bytes[i++];
            if ((distance & 0x80) != 0 && i + 1 < bytes.Length)
            {
                distance = bytes[i] | (bytes[i + 1] << 8);
                i += 2;
            }

            Advance(order, distance, ref x, ref y);
        }

        if (glyph is null)
        {
            Report("missing glyph", $"a GlyphIndex order uses a glyph that was never sent (cache {order.CacheId})");
            return i;
        }

        canvas.Glyph((long)x + glyph.X, (long)y + glyph.Y, glyph, color);
        if (order.CharInc != 0)
        {
            Advance(order, order.CharInc, ref x, ref y);
        }
        else if ((order.Accel & CharIncEqualsWidth) != 0)
        {
            Advance(order, glyph.Width, ref x, ref y);
        }

        return i;
    }

    // Cache Glyph, revision 1 or 2 by the GLYPH_ORDER_REV2 flag in extraFlags.
    private void CacheGlyphs(ref OrderReader body, int extraFlags)
    {
        if ((extraFlags & 0x20) != 0)
        {
            CacheGlyphsRev2(ref body, extraFlags);
            return;
        }

        if ((extraFlags & 0x4000) != 0)
        {
            // xrdp's anti-aliased glyphs (eight bits a pixel), meant for its own clients only.
            Report("alpha glyphs", "a Cache Glyph order with eight-bit glyphs; it is skipped");
            return;
        }

        // cacheId, cGlyphs, then per glyph: cacheIndex, x, y, cx, cy (16 bits each), the bitmap.
        int cacheId = body.U8();
        int count = body.U8();
        for (var i = 0; i < count; i++)
        {
            int index = body.U16();
            int x = body.I16();
            int y = body.I16();
            int width = body.U16();
            int height = body.U16();
            if (!StoreGlyph(ref body, cacheId, index, x, y, width, height))
            {
                return;
            }
        }
    }

    // Revision 2: cacheId, flags and cGlyphs in extraFlags; per glyph cacheIndex (8 bits), x and y (two-byte
    // signed), cx and cy (two-byte unsigned), the bitmap.
    private void CacheGlyphsRev2(ref OrderReader body, int extraFlags)
    {
        var cacheId = extraFlags & 0x0F;
        var count = extraFlags >> 8;
        for (var i = 0; i < count; i++)
        {
            int index = body.U8();
            var x = TwoByteSigned(ref body);
            var y = TwoByteSigned(ref body);
            var width = body.TwoByteUnsigned();
            var height = body.TwoByteUnsigned();
            if (!StoreGlyph(ref body, cacheId, index, x, y, width, height))
            {
                return;
            }
        }
    }

    // TWO_BYTE_SIGNED_ENCODING (2.2.2.2.1.2.1.3): a continuation bit, a sign bit, 6 or 14 bits of magnitude.
    private static int TwoByteSigned(ref OrderReader reader)
    {
        var first = reader.U8();
        var value = first & 0x3F;
        if ((first & 0x80) != 0)
        {
            value = (value << 8) | reader.U8();
        }

        return (first & 0x40) != 0 ? -value : value;
    }

    // The bitmap of one glyph: rows of whole bytes, the whole padded to a multiple of four bytes.
    private bool StoreGlyph(ref OrderReader body, int cacheId, int index, int x, int y, int width, int height)
    {
        var size = ((width + 7) / 8) * height;
        var bits = body.Bytes((size + 3) & ~3);
        if (body.Failed)
        {
            Report("glyph short", "a Cache Glyph order that ends early; the rest of it is skipped");
            return false;
        }

        if (size > GlyphCache.MaxGlyphBytes || !_glyphs.Put(cacheId, index, new Glyph(x, y, width, height, bits[..size].ToArray())))
        {
            Report("glyph limits", $"a glyph beyond this client's limits (cache {cacheId}, entry {index}, {width}x{height}); it is skipped");
        }

        return true;
    }
}
