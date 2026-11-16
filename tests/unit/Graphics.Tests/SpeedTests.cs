using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace Matgate.Display.Rdp.Graphics.Tests;

// Measures how fast the decoders are, in megapixels per second. Does nothing in a normal run; to measure:
//
//   dotnet test tests/unit/Graphics.Tests -c Release -e MATGATE_GRAPHICS_BENCHMARK=1 --filter Speed --logger "console;verbosity=detailed"
//
// The streams are the ones FreeRDP's encoders made of the test pictures (TestData), in the two sizes a server
// typically sends: a 64 x 64 tile and a strip of 1920 x 8.
public class SpeedTests(ITestOutputHelper output)
{
    private static readonly string[] Pictures = ["flat", "gradient", "text", "lines", "speckle", "dither", "mix", "noise"];

    [Theory]
    [InlineData("rle16/encoder", "Interleaved RLE, 16 bits")]
    [InlineData("rle24/encoder", "Interleaved RLE, 24 bits")]
    [InlineData("rle15/encoder", "Interleaved RLE, 15 bits")]
    [InlineData("planar/encoder/rle", "RDP 6.0 planar, RLE, no alpha (what Windows sends at 32 bits)")]
    [InlineData("planar/encoder/rle+alpha", "RDP 6.0 planar, RLE, with alpha plane")]
    [InlineData("planar/encoder/raw", "RDP 6.0 planar, raw planes")]
    [InlineData("planar/generated/cll1+cs+rle", "RDP 6.0 planar, AYCoCg with subsampled chroma, RLE")]
    [InlineData("planar/generated/cll3+rle+alpha", "RDP 6.0 planar, AYCoCg, RLE, with alpha plane")]
    [InlineData("raw32/padded", "uncompressed, 32 bits")]
    [InlineData("raw16/padded", "uncompressed, 16 bits")]
    public void Speed(string prefix, string title)
    {
        if (Environment.GetEnvironmentVariable("MATGATE_GRAPHICS_BENCHMARK") != "1")
        {
            return;
        }

        output.WriteLine(title);
        double pixels = 0;
        double seconds = 0;
        foreach (var picture in Pictures)
        {
            var line = $"  {picture,-9}";
            foreach (var size in new[] { "64x64", "1920x8" })
            {
                // A strip the RLE encoder split in two is two vectors. What FreeRDP's planar encoder wrote is the
                // picture only sometimes (see harness/vectors.c); where it is not, the stream of the harness's
                // own encoder is the one to measure.
                var vectors = Vectors.Bitmaps.Where(vector => vector.Name.StartsWith(prefix + "/" + picture + "/" + size, StringComparison.Ordinal)).ToList();
                if (prefix.StartsWith("planar/encoder/", StringComparison.Ordinal))
                {
                    var format = "cll0" + (prefix.Contains("rle", StringComparison.Ordinal) ? "+rle" : "") + (prefix.Contains("alpha", StringComparison.Ordinal) ? "+alpha" : "");
                    var generated = Vectors.Bitmaps.Where(vector => vector.Name == $"planar/generated/{format}/{picture}/{size}").ToList();
                    if (generated.Count > 0)
                    {
                        vectors = generated;
                    }
                }

                if (vectors.Count == 0)
                {
                    line += $"  {size,7}: -";
                    continue;
                }

                var (megapixels, elapsed) = Measure(vectors);
                pixels += megapixels;
                seconds += elapsed;
                line += $"  {size,7}: {megapixels / elapsed,6:F0} MP/s";
            }

            output.WriteLine(line);
        }

        output.WriteLine($"  all pictures: {pixels / seconds:F0} MP/s");
    }

    // Megapixels decoded and the seconds that took.
    private static (double Megapixels, double Seconds) Measure(List<Vector> vectors)
    {
        var framebuffer = new TestFramebuffer(vectors.Max(vector => vector.Width), vectors.Sum(vector => vector.Height));
        var megapixels = vectors.Sum(vector => (double)vector.Width * vector.Height) / 1e6;

        // Warm up: the JIT's second tier needs a few thousand calls.
        for (var i = 0; i < 3000; i++)
        {
            Decode(framebuffer, vectors);
        }

        // The best of several short runs: the machine has other work.
        var best = double.MaxValue;
        var rounds = Math.Max(20, (int)(8.0 / megapixels));
        for (var attempt = 0; attempt < 7; attempt++)
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < rounds; i++)
            {
                Decode(framebuffer, vectors);
            }

            best = Math.Min(best, watch.Elapsed.TotalSeconds);
        }

        return (megapixels * rounds, best);
    }

    private static void Decode(TestFramebuffer framebuffer, List<Vector> vectors)
    {
        var top = 0;
        foreach (var vector in vectors)
        {
            if (!framebuffer.Decode(vector, vector.Data, 0, top, vector.Width, vector.Height))
            {
                Assert.Fail(vector.Name);
            }

            top += vector.Height;
        }
    }
}
