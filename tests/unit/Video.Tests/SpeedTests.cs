using Xunit;

namespace Matgate.Display.Video.Tests;

[CollectionDefinition("Speed", DisableParallelization = true)]
public sealed class SpeedCollection;

// Milliseconds per picture for the conversion and for the encoding, at each size, for what a remote desktop
// does most: nothing (the same picture once more), a blinking cursor, scrolling text, and the worst case of
// every pixel new. Every measurement runs several times; the report gives the fastest, the middle and the
// slowest run (each run's median picture), so the spread on a busy machine shows. For comparison: the C engine
// this replaces used x264 "superfast"/"zerolatency" and kept up with 60 pictures a second at 1920 x 1080.
[Collection("Speed")]
public sealed class SpeedTests
{
    private const int Runs = 3;
    private const int Pictures = 60;

    public static TheoryData<int, int, int> Sizes() => new()
    {
        { 640, 480, 1 },
        { 1920, 1080, 1 },
        { 3840, 2160, 1 },
        { 3840, 2160, 4 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void HowLongAPictureTakes(int width, int height, int threads)
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        foreach (var scenario in new[] { "still", "cursor", "scroll", "noise" })
        {
            var convert = new List<double>();
            var encode = new List<double>();
            var slowest = new List<double>();
            for (var run = 0; run < Runs; run++)
            {
                var (convertTimes, encodeTimes) = Measure(width, height, threads, scenario);
                convert.Add(Median(convertTimes));
                encode.Add(Median(encodeTimes));
                slowest.Add(encodeTimes.Order().ElementAt((int)(encodeTimes.Count * 0.95)));
            }

            Report.Line(
                $"speed {width}x{height} threads {threads} {scenario,-6}: convert {Spread(convert)} ms, encode {Spread(encode)} ms " +
                $"(95th percentile {Spread(slowest)} ms) - min / median / max of {Runs} runs");
            if (width == 1920 && threads == 1 && scenario is "cursor" or "scroll")
            {
                // A generous bound on a busy machine: half of what 60 pictures a second allow at the most.
                Assert.True(encode.Min() + convert.Min() < 33, $"{scenario} at 1920x1080 takes {encode.Min() + convert.Min():F1} ms a picture.");
            }
        }
    }

    // Converts and encodes the pictures of a scenario, after a key frame and the repeats that make it sharp.
    private static (List<double> Convert, List<double> Encode) Measure(int width, int height, int threads, string scenario)
    {
        var ceiling = EncoderTests.Ceiling(width, height);
        using var recording = new Recording(width, height, 60, ceiling, new H264EncoderOptions { Threads = threads }) { KeepReference = false };
        var page = Video.Tests.Pictures.TextPage(width, height * 2);
        Video.Tests.Pictures.Scroll(page, recording.Source, width, height, 0);
        recording.Show();
        recording.Encode();
        Scenarios.Refine(recording);

        var random = new Random(1);
        var convert = new List<double>();
        var encode = new List<double>();
        var x = width / 2 & ~7;
        var y = height / 2 & ~15;
        for (var i = 1; i <= Pictures; i++)
        {
            switch (scenario)
            {
                case "still":
                    // The whole screen converted once more, unchanged - what a desktop that repaints without
                    // changing anything causes - and the same picture encoded again.
                    recording.Show();
                    break;
                case "cursor":
                    Video.Tests.Pictures.Cursor(recording.Source, width, x, y, i % 2 == 0);
                    recording.Show(x, y, 2, 16);
                    break;
                case "scroll":
                    Video.Tests.Pictures.Scroll(page, recording.Source, width, height, i * 6);
                    recording.Show();
                    break;
                default:
                    Video.Tests.Pictures.Noise(recording.Source, random);
                    recording.Show();
                    break;
            }

            convert.Add(recording.ConvertMilliseconds);
            encode.Add(recording.Encode(after: scenario == "still" ? 300 : null).Milliseconds);
        }

        // The first pictures of a run pay for the runtime warming up.
        return (convert.Skip(5).ToList(), encode.Skip(5).ToList());
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private static string Spread(List<double> values)
    {
        var sorted = values.Order().ToList();
        return $"{sorted[0]:F2} / {sorted[sorted.Count / 2]:F2} / {sorted[^1]:F2}";
    }
}
