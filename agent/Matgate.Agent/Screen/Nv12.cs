namespace Matgate.Agent.Screen;

// The desktop comes as BGRA, the H.264 encoder takes NV12 (a full-size Y plane, then U and V interleaved at
// half size). BT.709 with limited range, which is what the encoder announces and the browser assumes for
// HD video. A 2x downscale is folded into the same pass for screens wider than the viewer wants.
internal static unsafe class Nv12
{
    public static int Size(int width, int height) => width * height * 3 / 2;

    // Output size for a source size and a downscale factor: even, as 4:2:0 needs.
    public static (int Width, int Height) Scaled(int sourceWidth, int sourceHeight, int scale)
    {
        return ((sourceWidth / scale) & ~1, (sourceHeight / scale) & ~1);
    }

    public static void FromBgra(byte* source, int pitch, int scale, byte[] target, int width, int height)
    {
        fixed (byte* pinned = target)
        {
            var destination = (nint)pinned;
            var bgra = (nint)source;
            // Row pairs in parallel: a 1080p frame is converted in a few milliseconds.
            Parallel.For(0, (height / 2 + 15) / 16, block =>
            {
                var first = block * 16;
                var last = Math.Min(height / 2, first + 16);
                for (var pair = first; pair < last; pair++)
                {
                    if (scale == 1)
                    {
                        ConvertPair((byte*)bgra, pitch, (byte*)destination, width, height, pair);
                    }
                    else
                    {
                        ConvertPairScaled((byte*)bgra, pitch, scale, (byte*)destination, width, height, pair);
                    }
                }
            });
        }
    }

    private static void ConvertPair(byte* source, int pitch, byte* target, int width, int height, int pair)
    {
        var y0 = pair * 2;
        var row0 = source + (long)y0 * pitch;
        var row1 = row0 + pitch;
        var luma0 = target + (long)y0 * width;
        var luma1 = luma0 + width;
        var chroma = target + (long)width * height + (long)pair * width;
        for (var x = 0; x < width; x += 2)
        {
            var p00 = row0 + x * 4;
            var p01 = p00 + 4;
            var p10 = row1 + x * 4;
            var p11 = p10 + 4;
            luma0[x] = Luma(p00[2], p00[1], p00[0]);
            luma0[x + 1] = Luma(p01[2], p01[1], p01[0]);
            luma1[x] = Luma(p10[2], p10[1], p10[0]);
            luma1[x + 1] = Luma(p11[2], p11[1], p11[0]);
            var r = (p00[2] + p01[2] + p10[2] + p11[2] + 2) >> 2;
            var g = (p00[1] + p01[1] + p10[1] + p11[1] + 2) >> 2;
            var b = (p00[0] + p01[0] + p10[0] + p11[0] + 2) >> 2;
            chroma[x] = Cb(r, g, b);
            chroma[x + 1] = Cr(r, g, b);
        }
    }

    // Each output pixel is the average of a scale x scale block of the source.
    private static void ConvertPairScaled(byte* source, int pitch, int scale, byte* target, int width, int height, int pair)
    {
        var area = scale * scale;
        var chroma = target + (long)width * height + (long)pair * width;
        for (var x = 0; x < width; x += 2)
        {
            int sumR = 0, sumG = 0, sumB = 0;
            for (var dy = 0; dy < 2; dy++)
            {
                var y = pair * 2 + dy;
                var luma = target + (long)y * width;
                for (var dx = 0; dx < 2; dx++)
                {
                    int pr = 0, pg = 0, pb = 0;
                    for (var sy = 0; sy < scale; sy++)
                    {
                        var p = source + (long)(y * scale + sy) * pitch + (long)(x + dx) * scale * 4;
                        for (var sx = 0; sx < scale; sx++, p += 4)
                        {
                            pb += p[0];
                            pg += p[1];
                            pr += p[2];
                        }
                    }

                    pr /= area;
                    pg /= area;
                    pb /= area;
                    luma[x + dx] = Luma(pr, pg, pb);
                    sumR += pr;
                    sumG += pg;
                    sumB += pb;
                }
            }

            chroma[x] = Cb((sumR + 2) >> 2, (sumG + 2) >> 2, (sumB + 2) >> 2);
            chroma[x + 1] = Cr((sumR + 2) >> 2, (sumG + 2) >> 2, (sumB + 2) >> 2);
        }
    }

    private static byte Luma(int r, int g, int b) => (byte)(16 + ((47 * r + 157 * g + 16 * b + 128) >> 8));

    private static byte Cb(int r, int g, int b) => (byte)(128 + ((-26 * r - 87 * g + 113 * b + 128) >> 8));

    private static byte Cr(int r, int g, int b) => (byte)(128 + ((112 * r - 102 * g - 10 * b + 128) >> 8));
}
