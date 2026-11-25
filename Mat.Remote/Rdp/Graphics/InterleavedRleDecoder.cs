using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Matgate.Display.Rdp.Graphics;

// Interleaved RLE (MS-RDPBCGR 2.2.9.1.1.3.1.2.4, decoder pseudo-code in 3.1.9): the compression of bitmaps with
// 8, 15, 16 and 24 bits per pixel. The data is a series of orders, each a header byte with a code and a run
// length, sometimes followed by a longer run length and by pixels or bit masks. Scan lines come bottom-up.
//
// An order only ever refers to the pixel directly above the one it writes, so two scan lines are all the state
// the decoder keeps; every finished line goes straight into the framebuffer. What a hostile server can claim as
// the size of the bitmap therefore costs two lines of memory, not a picture of it.
internal static class InterleavedRleDecoder
{
    // Regular form: the code is the three high bits of the header, the run length the five low bits.
    private const int RegularBackgroundRun = 0x0;
    private const int RegularForegroundRun = 0x1;
    private const int RegularFgBgImage = 0x2;
    private const int RegularColorRun = 0x3;
    private const int RegularColorImage = 0x4;

    // Lite form: four bits of code, four bits of run length.
    private const int LiteSetForegroundRun = 0xC;
    private const int LiteSetFgBgImage = 0xD;
    private const int LiteDitheredRun = 0xE;

    // MEGA_MEGA form: the header is the code, a 16-bit run length follows.
    private const int MegaMegaBackgroundRun = 0xF0;
    private const int MegaMegaForegroundRun = 0xF1;
    private const int MegaMegaFgBgImage = 0xF2;
    private const int MegaMegaColorRun = 0xF3;
    private const int MegaMegaColorImage = 0xF4;
    private const int MegaMegaSetForegroundRun = 0xF6;
    private const int MegaMegaSetFgBgImage = 0xF7;
    private const int MegaMegaDitheredRun = 0xF8;

    // Single-byte orders.
    private const int SpecialFgBg1 = 0xF9;
    private const int SpecialFgBg2 = 0xFA;
    private const int WhitePixel = 0xFD;
    private const int BlackPixel = 0xFE;

    // The bit masks of the two special orders: eight pixels each.
    private static ReadOnlySpan<byte> SpecialMask1 => [0x03];

    private static ReadOnlySpan<byte> SpecialMask2 => [0x05];

    public static bool Decode(ReadOnlySpan<byte> data, int bitsPerPixel, int width, int height, ReadOnlySpan<uint> palette, BitmapTarget target)
    {
        int bytesPerPixel;
        uint white;
        switch (bitsPerPixel)
        {
            case 8:
                if (palette.Length < 256)
                {
                    return false;
                }

                // "White" is the last palette entry, whatever colour the palette has there.
                bytesPerPixel = 1;
                white = 0xFF;
                break;

            case 15:
                bytesPerPixel = 2;
                white = 0x7FFF;
                break;

            case 16:
                bytesPerPixel = 2;
                white = 0xFFFF;
                break;

            case 24:
                bytesPerPixel = 3;
                white = 0xFFFFFF;
                break;

            default:
                return false;
        }

        var lines = ArrayPool<uint>.Shared.Rent(width * 2);
        try
        {
            var canvas = new Canvas(lines.AsSpan(0, width), lines.AsSpan(width, width), height, bitsPerPixel, palette, target);
            return Decode(data, bytesPerPixel, white, ref canvas);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(lines);
        }
    }

    private static bool Decode(ReadOnlySpan<byte> data, int bytesPerPixel, uint white, ref Canvas canvas)
    {
        // At 24 bits the lines hold finished framebuffer pixels (the X byte set), so that a finished line is one
        // copy. A value that is written as a pixel gets the byte; the foreground colour, which is only ever
        // XORed onto pixels, must not have it.
        var opaque = bytesPerPixel == 3 ? Bgrx.Opaque : 0;
        var black = opaque;
        var foreground = white;
        var firstLine = true;
        var insertForeground = false;
        var position = 0;

        while (position < data.Length)
        {
            // The first scan line has nothing above it and follows rules of its own. An order that starts on it
            // keeps those rules for all of its pixels, also where it runs over into the second line: that is what
            // the specification's decoder does, and what an encoder therefore counts on.
            if (firstLine && canvas.Line > 0)
            {
                firstLine = false;
                insertForeground = false;
            }

            var header = data[position++];
            int code;
            int count;
            if ((header & 0xC0) != 0xC0)
            {
                code = header >> 5;
                count = header & 0x1F;
                if (count == 0)
                {
                    // A MEGA run: the length is the next byte, counted on from what the header could have held.
                    if (position >= data.Length)
                    {
                        return false;
                    }

                    count = data[position++] + (code == RegularFgBgImage ? 1 : 32);
                }
                else if (code == RegularFgBgImage)
                {
                    count *= 8;
                }
            }
            else if ((header & 0xF0) != 0xF0)
            {
                code = header >> 4;
                count = header & 0x0F;
                if (count == 0)
                {
                    if (position >= data.Length)
                    {
                        return false;
                    }

                    count = data[position++] + (code == LiteSetFgBgImage ? 1 : 16);
                }
                else if (code == LiteSetFgBgImage)
                {
                    count *= 8;
                }
            }
            else
            {
                code = header;
                count = 0;
                if (header <= MegaMegaDitheredRun && header != 0xF5)
                {
                    if (data.Length - position < 2)
                    {
                        return false;
                    }

                    count = data[position] | (data[position + 1] << 8);
                    position += 2;
                }
            }

            if (code is RegularBackgroundRun or MegaMegaBackgroundRun)
            {
                if (canvas.Remaining < count)
                {
                    return false;
                }

                if (insertForeground)
                {
                    // Two background runs in a row would be one run, so an encoder only writes them when a single
                    // foreground pixel lies between: it is implied, and counted in the length of the second run.
                    if (count == 0)
                    {
                        return false;
                    }

                    if (firstLine)
                    {
                        canvas.Fill(1, foreground | opaque);
                    }
                    else
                    {
                        canvas.XorAbove(1, foreground);
                    }

                    count--;
                }

                if (firstLine)
                {
                    canvas.Fill(count, black);
                }
                else
                {
                    canvas.CopyAbove(count);
                }

                insertForeground = true;
                continue;
            }

            insertForeground = false;
            switch (code)
            {
                case RegularForegroundRun:
                case MegaMegaForegroundRun:
                case LiteSetForegroundRun:
                case MegaMegaSetForegroundRun:
                    if (code is LiteSetForegroundRun or MegaMegaSetForegroundRun)
                    {
                        if (data.Length - position < bytesPerPixel)
                        {
                            return false;
                        }

                        foreground = ReadPixel(data, position, bytesPerPixel);
                        position += bytesPerPixel;
                    }

                    if (canvas.Remaining < count)
                    {
                        return false;
                    }

                    if (firstLine)
                    {
                        canvas.Fill(count, foreground | opaque);
                    }
                    else
                    {
                        canvas.XorAbove(count, foreground);
                    }

                    break;

                case LiteDitheredRun:
                case MegaMegaDitheredRun:
                {
                    // The run length counts pairs of pixels here.
                    if (data.Length - position < bytesPerPixel * 2 || canvas.Remaining < count * 2)
                    {
                        return false;
                    }

                    var first = ReadPixel(data, position, bytesPerPixel) | opaque;
                    var second = ReadPixel(data, position + bytesPerPixel, bytesPerPixel) | opaque;
                    position += bytesPerPixel * 2;
                    canvas.Alternate(count, first, second);
                    break;
                }

                case RegularColorRun:
                case MegaMegaColorRun:
                    if (data.Length - position < bytesPerPixel || canvas.Remaining < count)
                    {
                        return false;
                    }

                    canvas.Fill(count, ReadPixel(data, position, bytesPerPixel) | opaque);
                    position += bytesPerPixel;
                    break;

                case RegularFgBgImage:
                case MegaMegaFgBgImage:
                case LiteSetFgBgImage:
                case MegaMegaSetFgBgImage:
                {
                    if (code is LiteSetFgBgImage or MegaMegaSetFgBgImage)
                    {
                        if (data.Length - position < bytesPerPixel)
                        {
                            return false;
                        }

                        foreground = ReadPixel(data, position, bytesPerPixel);
                        position += bytesPerPixel;
                    }

                    // One bit per pixel; the last mask byte may be used only in part.
                    var maskBytes = (count + 7) / 8;
                    if (data.Length - position < maskBytes || canvas.Remaining < count)
                    {
                        return false;
                    }

                    canvas.FgBgImage(data.Slice(position, maskBytes), count, foreground, opaque, firstLine);
                    position += maskBytes;
                    break;
                }

                case RegularColorImage:
                case MegaMegaColorImage:
                {
                    var bytes = count * bytesPerPixel;
                    if (data.Length - position < bytes || canvas.Remaining < count)
                    {
                        return false;
                    }

                    canvas.Copy(data.Slice(position, bytes), bytesPerPixel, opaque);
                    position += bytes;
                    break;
                }

                case SpecialFgBg1:
                case SpecialFgBg2:
                    if (canvas.Remaining < 8)
                    {
                        return false;
                    }

                    canvas.FgBgImage(code == SpecialFgBg1 ? SpecialMask1 : SpecialMask2, 8, foreground, opaque, firstLine);
                    break;

                case WhitePixel:
                    if (canvas.Remaining < 1)
                    {
                        return false;
                    }

                    canvas.Fill(1, white | opaque);
                    break;

                case BlackPixel:
                    if (canvas.Remaining < 1)
                    {
                        return false;
                    }

                    canvas.Fill(1, black);
                    break;

                default:
                    // 0xA0..0xBF, 0xF5, 0xFB, 0xFC and 0xFF are no orders.
                    return false;
            }
        }

        // Data that ends early leaves lines of the bitmap undefined: that is not a bitmap.
        return canvas.IsComplete;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadPixel(ReadOnlySpan<byte> data, int position, int bytesPerPixel) => bytesPerPixel switch
    {
        1 => data[position],
        2 => (uint)(data[position] | (data[position + 1] << 8)),
        _ => (uint)(data[position] | (data[position + 1] << 8) | (data[position + 2] << 16)),
    };

    // The scan line being written and the one above it. Every method writes a number of pixels the caller has
    // checked against Remaining, moves on line by line, and hands each finished line to the framebuffer.
    private ref struct Canvas
    {
        private readonly BitmapTarget _target;
        private readonly ReadOnlySpan<uint> _palette;
        private readonly int _bitsPerPixel;
        private readonly int _width;
        private readonly int _height;
        private Span<uint> _current;
        private Span<uint> _above;
        private int _x;
        private int _line;

        public Canvas(Span<uint> current, Span<uint> above, int height, int bitsPerPixel, ReadOnlySpan<uint> palette, BitmapTarget target)
        {
            _current = current;
            _above = above;
            _width = current.Length;
            _height = height;
            _bitsPerPixel = bitsPerPixel;
            _palette = palette;
            _target = target;
        }

        // Finished scan lines, counted from the bottom of the bitmap.
        public readonly int Line => _line;

        public readonly long Remaining => ((long)(_height - _line) * _width) - _x;

        public readonly bool IsComplete => _line == _height;

        public void Fill(int count, uint pixel)
        {
            while (count > 0)
            {
                var chunk = Math.Min(count, _width - _x);
                _current.Slice(_x, chunk).Fill(pixel);
                count -= chunk;
                Advance(chunk);
            }
        }

        public void CopyAbove(int count)
        {
            while (count > 0)
            {
                var chunk = Math.Min(count, _width - _x);
                _above.Slice(_x, chunk).CopyTo(_current.Slice(_x, chunk));
                count -= chunk;
                Advance(chunk);
            }
        }

        public void XorAbove(int count, uint foreground)
        {
            while (count > 0)
            {
                var chunk = Math.Min(count, _width - _x);
                var run = _current.Slice(_x, chunk);
                var above = _above.Slice(_x, chunk);
                for (var i = 0; i < run.Length; i++)
                {
                    run[i] = above[i] ^ foreground;
                }

                count -= chunk;
                Advance(chunk);
            }
        }

        public void Alternate(int pairs, uint first, uint second)
        {
            var count = pairs * 2;
            var odd = false;
            while (count > 0)
            {
                var chunk = Math.Min(count, _width - _x);
                var run = _current.Slice(_x, chunk);
                for (var i = 0; i < run.Length; i++)
                {
                    run[i] = odd ? second : first;
                    odd = !odd;
                }

                count -= chunk;
                Advance(chunk);
            }
        }

        // Pixels as they are in the data, source.Length / bytesPerPixel of them.
        public void Copy(ReadOnlySpan<byte> source, int bytesPerPixel, uint opaque)
        {
            while (!source.IsEmpty)
            {
                var chunk = Math.Min(source.Length / bytesPerPixel, _width - _x);
                var run = _current.Slice(_x, chunk);
                var bytes = source[..(chunk * bytesPerPixel)];
                switch (bytesPerPixel)
                {
                    case 1:
                        for (var i = 0; i < bytes.Length; i++)
                        {
                            run[i] = bytes[i];
                        }

                        break;

                    case 2:
                    {
                        var words = MemoryMarshal.Cast<byte, ushort>(bytes);
                        for (var i = 0; i < words.Length; i++)
                        {
                            run[i] = BitConverter.IsLittleEndian ? words[i] : BinaryPrimitives.ReverseEndianness(words[i]);
                        }

                        break;
                    }

                    default:
                        Bgrx.PackedBgr(bytes, run, opaque);
                        break;
                }

                source = source[bytes.Length..];
                Advance(chunk);
            }
        }

        // One bit per pixel, low bit first: set means the pixel above XOR the foreground colour, clear means the
        // pixel above. On the first scan line "the pixel above" is black.
        public void FgBgImage(ReadOnlySpan<byte> masks, int count, uint foreground, uint opaque, bool firstLine)
        {
            var index = 0;
            while (count > 0)
            {
                uint mask = masks[index++];
                var bits = Math.Min(count, 8);
                count -= bits;
                while (bits > 0)
                {
                    var chunk = Math.Min(bits, _width - _x);
                    var run = _current.Slice(_x, chunk);
                    if (firstLine)
                    {
                        for (var i = 0; i < run.Length; i++)
                        {
                            run[i] = ((mask >> i) & 1) != 0 ? foreground | opaque : opaque;
                        }
                    }
                    else
                    {
                        var above = _above.Slice(_x, chunk);
                        for (var i = 0; i < run.Length; i++)
                        {
                            run[i] = ((mask >> i) & 1) != 0 ? above[i] ^ foreground : above[i];
                        }
                    }

                    mask >>= chunk;
                    bits -= chunk;
                    Advance(chunk);
                }
            }
        }

        private void Advance(int pixels)
        {
            _x += pixels;
            if (_x < _width)
            {
                return;
            }

            // The data is bottom-up: the first line that gets finished is the last row of the bitmap.
            var destination = _target.Row(_height - 1 - _line);
            if (!destination.IsEmpty)
            {
                Bgrx.FromValues(_current[_target.FirstColumn..], destination, _bitsPerPixel, _palette);
            }

            var finished = _current;
            _current = _above;
            _above = finished;
            _x = 0;
            _line++;
        }
    }
}
