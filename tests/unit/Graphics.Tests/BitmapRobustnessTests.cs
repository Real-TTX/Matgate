using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Matgate.Display.Rdp.Graphics.Tests;

// The data comes from a remote server: whatever is in it, the decoder returns true or false, never throws,
// never runs on forever and never writes a byte outside the destination rectangle.
public class BitmapRobustnessTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryVectorCutAtEveryLengthIsRefusedOrComplete()
    {
        long decodes = 0;
        long accepted = 0;
        Parallel.ForEach(Vectors.Bitmaps, vector =>
        {
            var framebuffer = new TestFramebuffer(vector.Width + 4, vector.Height + 4);
            var complete = 0;
            for (var length = 0; length < vector.Data.Length; length++)
            {
                if (Decode(framebuffer, vector, vector.Data.AsSpan(0, length), $"{vector.Name} cut to {length} bytes"))
                {
                    // Nothing that mattered was cut off - the padding byte after raw planes, an order without
                    // pixels at the end. The picture must then be all there, drawn by this call alone.
                    framebuffer.Clear();
                    Assert.True(framebuffer.Decode(vector, vector.Data.AsSpan(0, length), 2, 2, vector.Width, vector.Height));
                    framebuffer.AssertPicture(vector.Expected, vector.Width, 2, 2, vector.Width, vector.Height, $"{vector.Name} cut to {length} bytes");
                    complete++;
                }
            }

            // A stray write in any of the calls is still there now.
            framebuffer.AssertUntouchedOutside(2, 2, vector.Width, vector.Height, vector.Name + ", cut short");
            Interlocked.Add(ref decodes, vector.Data.Length);
            Interlocked.Add(ref accepted, complete);
        });

        output.WriteLine($"{decodes} truncated streams, {accepted} of them still complete");
        Assert.True(decodes > 1_000_000);
    }

    // Valid streams with a few bytes changed, added or removed get much further into the decoders than noise
    // does: a run that is suddenly too long, a plane that ends in the middle of a line.
    [Fact]
    public void DamagedVectorsNeverThrowAndNeverWriteOutside()
    {
        long decodes = 0;
        long accepted = 0;
        Parallel.ForEach(Vectors.Bitmaps, (vector, _, index) =>
        {
            var random = new Random(unchecked((int)(index * 7919)));
            var framebuffer = new TestFramebuffer(vector.Width + 4, vector.Height + 4);
            var rounds = vector.Data.Length > 20000 ? 40 : 200;
            var ok = 0;
            for (var round = 0; round < rounds; round++)
            {
                var data = Damage(vector.Data, random);
                if (Decode(framebuffer, vector, data, $"{vector.Name}, damaged (round {round})"))
                {
                    ok++;
                }
            }

            framebuffer.AssertUntouchedOutside(2, 2, vector.Width, vector.Height, vector.Name + ", damaged");
            Interlocked.Add(ref decodes, rounds);
            Interlocked.Add(ref accepted, ok);
        });

        output.WriteLine($"{decodes} damaged streams, {accepted} of them decoded to some picture");
        Assert.True(decodes > 300_000);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(15, false)]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(8, true)]
    [InlineData(15, true)]
    [InlineData(16, true)]
    [InlineData(24, true)]
    [InlineData(32, true)]
    [InlineData(4, true)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public void RandomBuffersNeverThrowAndNeverWriteOutside(int bitsPerPixel, bool compressed)
    {
        var random = new Random((bitsPerPixel * 2) + (compressed ? 1 : 0));
        var palette = new uint[256];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = 0xFF000000 | (uint)random.Next(0x1000000);
        }

        var framebuffer = new TestFramebuffer(96, 64);
        var accepted = 0;
        const int Rounds = 20000;
        for (var round = 0; round < Rounds; round++)
        {
            // Mostly small bitmaps, so that random orders have a chance to fill one; now and then anything.
            var width = random.Next(8) == 0 ? random.Next(1, 70000) : random.Next(1, 40);
            var height = random.Next(8) == 0 ? random.Next(1, 70000) : random.Next(1, 24);
            var data = new byte[random.Next(4) == 0 ? random.Next(0, 3000) : random.Next(0, 120)];
            random.NextBytes(data);
            if (compressed && bitsPerPixel == 32 && data.Length > 0 && random.Next(2) == 0)
            {
                // a format header that lets the planes begin
                data[0] = (byte)(data[0] & 0x3F);
            }

            var header = compressed && random.Next(3) == 0;
            if (header && data.Length >= 8 && random.Next(2) == 0)
            {
                // a header that fits its data
                data[2] = (byte)((data.Length - 8) & 0xFF);
                data[3] = (byte)((data.Length - 8) >> 8);
            }

            var left = random.Next(-50, 120);
            var top = random.Next(-50, 90);
            var right = left + random.Next(-2, 130);
            var bottom = top + random.Next(-2, 100);
            var usePalette = random.Next(10) != 0;

            bool ok;
            try
            {
                ok = BitmapDecoder.Decode(data, bitsPerPixel, compressed, header, width, height, framebuffer.Pixels, framebuffer.Width, framebuffer.Height,
                    left, top, right, bottom, usePalette ? palette : default);
            }
            catch (Exception exception)
            {
                Assert.Fail($"round {round}: {width}x{height}, {data.Length} bytes {Convert.ToHexString(data.AsSpan(0, Math.Min(64, data.Length)))}: {exception}");
                return;
            }

            if (ok)
            {
                accepted++;
            }

            // Whatever was drawn lies inside the rectangle and inside the bitmap that begins at its corner.
            var what = $"round {round}: {width}x{height} at ({left}, {top})-({right}, {bottom})";
            framebuffer.AssertUntouchedOutside(left, top, Math.Clamp(Math.Min(right - left + 1, width), 0, 1000), Math.Clamp(Math.Min(bottom - top + 1, height), 0, 1000), what);
            framebuffer.Clear();
        }

        output.WriteLine($"{bitsPerPixel} bits, {(compressed ? "compressed" : "uncompressed")}: {Rounds} random buffers, {accepted} decoded to some picture");
        if (bitsPerPixel is 4 or 1 or 0)
        {
            Assert.Equal(0, accepted);
        }
    }

    // The framebuffer the caller describes need not be the one it hands over: a size that does not fit the bytes
    // is refused, and one that fits is never written beyond.
    [Fact]
    public void FramebufferSizesThatDoNotFitTheirBytesAreRefused()
    {
        var random = new Random(4096);
        var vectors = Vectors.Bitmaps.Where(vector => vector.Width <= 64 && vector.Height <= 64).ToList();
        var memory = new byte[(80 * 80 * 4) + 512];
        int[] interesting = [0, 1, 2, 63, 64, 65, 79, 80, 81, 100, 1000, 65535, 65536, 1 << 20, 1 << 30, int.MaxValue, -1, -80, int.MinValue];
        var refused = 0;
        for (var round = 0; round < 40000; round++)
        {
            var vector = vectors[random.Next(vectors.Count)];
            var width = random.Next(3) == 0 ? interesting[random.Next(interesting.Length)] : random.Next(0, 90);
            var height = random.Next(3) == 0 ? interesting[random.Next(interesting.Length)] : random.Next(0, 90);
            var bytes = random.Next(4) == 0 ? random.Next(0, 80 * 80 * 4) : 80 * 80 * 4;
            var left = random.Next(8) == 0 ? interesting[random.Next(interesting.Length)] : random.Next(-70, 90);
            var top = random.Next(8) == 0 ? interesting[random.Next(interesting.Length)] : random.Next(-70, 90);
            memory.AsSpan().Fill(TestFramebuffer.Untouched);
            var framebuffer = memory.AsSpan(256, bytes);

            bool ok;
            try
            {
                ok = BitmapDecoder.Decode(vector.Data, vector.BitsPerPixel, vector.Compressed, false, vector.Width, vector.Height, framebuffer, width, height,
                    left, top, unchecked(left + vector.Width - 1), unchecked(top + vector.Height - 1), vector.Palette);
            }
            catch (Exception exception)
            {
                Assert.Fail($"round {round}: {vector.Name} into {width}x{height} ({bytes} bytes) at ({left}, {top}): {exception}");
                return;
            }

            var fits = width >= 0 && height >= 0 && (long)width * height <= bytes / 4;
            var rectangle = (long)left + vector.Width - 1 >= left && (long)left + vector.Width - 1 <= int.MaxValue && (long)top + vector.Height - 1 <= int.MaxValue;
            if (rectangle)
            {
                Assert.True(fits == ok, $"round {round}: {vector.Name} into {width}x{height} ({bytes} bytes) at ({left}, {top}): {ok}");
            }

            if (!ok)
            {
                refused++;
            }

            Assert.True(memory.AsSpan(0, 256).IndexOfAnyExcept(TestFramebuffer.Untouched) < 0, $"round {round}: written before the framebuffer");
            Assert.True(memory.AsSpan(256 + bytes).IndexOfAnyExcept(TestFramebuffer.Untouched) < 0, $"round {round}: written after the framebuffer");
            if (!fits)
            {
                Assert.True(framebuffer.IndexOfAnyExcept(TestFramebuffer.Untouched) < 0, $"round {round}: a refused framebuffer was written");
            }
        }

        output.WriteLine($"40000 framebuffers, {refused} refused");
        Assert.True(refused > 5000);
    }

    // A bitmap as large as the wire format can say costs two scan lines of memory, not a picture - and the time
    // of the pixels its data can produce.
    [Fact]
    public void ABitmapOfImpossibleSizeCostsNoMemory()
    {
        var framebuffer = new TestFramebuffer(64, 64);

        // Background runs of the greatest length: 65535 pixels for three bytes.
        var runs = new byte[65535];
        for (var i = 0; i + 2 < runs.Length; i += 3)
        {
            runs[i] = 0xF0;
            runs[i + 1] = 0xFF;
            runs[i + 2] = 0xFF;
        }

        // One planar plane of long runs.
        var planes = new byte[65535];
        planes[0] = 0x30;
        planes.AsSpan(1).Fill(0xF2);

        var small = new byte[100];

        // The scan lines come from the array pool, which hands the same ones out again - unless a garbage
        // collection of the tests running next to this one has just emptied it. So: the best of a few tries.
        var allocated = long.MaxValue;
        for (var attempt = 0; attempt < 5 && allocated >= 4096; attempt++)
        {
            BitmapDecoder.Decode(runs, 24, true, false, 65535, 100, framebuffer.Pixels, 64, 64, 0, 0, 63, 63);
            BitmapDecoder.Decode(planes, 32, true, false, 65535, 100, framebuffer.Pixels, 64, 64, 0, 0, 63, 63);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            Assert.False(BitmapDecoder.Decode(runs, 24, true, false, 65535, 65535, framebuffer.Pixels, 64, 64, 0, 0, 63, 63));
            var rle = watch.Elapsed;
            watch.Restart();
            Assert.False(BitmapDecoder.Decode(planes, 32, true, false, 65535, 65535, framebuffer.Pixels, 64, 64, 0, 0, 63, 63));
            var planar = watch.Elapsed;
            Assert.False(BitmapDecoder.Decode(small, 32, false, false, 65535, 65535, framebuffer.Pixels, 64, 64, 0, 0, 63, 63));
            Assert.False(BitmapDecoder.Decode(small, 32, true, false, 65535, 65535, framebuffer.Pixels, 64, 64, 0, 0, 63, 63));
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"65535 x 65535: RLE {rle.TotalMilliseconds:F0} ms for 64 KB of the longest runs, planar {planar.TotalMilliseconds:F0} ms, {allocated} bytes allocated");
        }

        Assert.True(allocated < 4096, $"{allocated} bytes allocated");
        framebuffer.AssertGuardsIntact("impossible sizes");
    }

    [Fact]
    public void DecodingAllocatesNothing()
    {
        var vectors = Vectors.Bitmaps.Where(vector => vector.Name.EndsWith("/text/64x64", StringComparison.Ordinal) || vector.Name.EndsWith("/text/1920x8", StringComparison.Ordinal)).ToList();
        Assert.True(vectors.Count > 20);
        var framebuffer = new TestFramebuffer(1920, 64);

        // The first round rents the scan lines from the pool; from then on they come back from it - unless a
        // garbage collection of the tests running next to this one has just emptied the pool, hence the tries.
        var allocated = long.MaxValue;
        for (var attempt = 0; attempt < 5 && allocated != 0; attempt++)
        {
            foreach (var vector in vectors)
            {
                Assert.True(framebuffer.Decode(vector, vector.Data, 0, 0, vector.Width, vector.Height), vector.Name);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var round = 0; round < 20; round++)
            {
                foreach (var vector in vectors)
                {
                    framebuffer.Decode(vector, vector.Data, 0, 0, vector.Width, vector.Height);
                }
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, allocated);
    }

    // Decodes into the middle of the framebuffer (two pixels of margin all around). The framebuffer is not
    // cleared in between: a write outside the rectangle stays visible until the caller looks.
    private static bool Decode(TestFramebuffer framebuffer, Vector vector, ReadOnlySpan<byte> data, string what)
    {
        try
        {
            return framebuffer.Decode(vector, data, 2, 2, vector.Width, vector.Height);
        }
        catch (Exception exception)
        {
            Assert.Fail($"{what}: {exception}");
            return false;
        }
    }

    private static byte[] Damage(byte[] data, Random random)
    {
        var copy = new List<byte>(data);
        var changes = random.Next(1, 4);
        for (var change = 0; change < changes && copy.Count > 0; change++)
        {
            // Changes near the beginning matter most: headers, the first scan line.
            var position = random.Next(3) == 0 ? random.Next(Math.Min(copy.Count, 16)) : random.Next(copy.Count);
            switch (random.Next(5))
            {
                case 0:
                    copy[position] = (byte)random.Next(256);
                    break;
                case 1:
                    copy[position] ^= (byte)(1 << random.Next(8));
                    break;
                case 2:
                    copy.RemoveAt(position);
                    break;
                case 3:
                    copy.Insert(position, (byte)random.Next(256));
                    break;
                default:
                    copy[position] = (byte)(random.Next(2) == 0 ? 0x00 : 0xFF);
                    break;
            }
        }

        return copy.ToArray();
    }
}
