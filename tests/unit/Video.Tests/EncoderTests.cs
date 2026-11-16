using Xunit;

namespace Matgate.Display.Video.Tests;

// The encoder on made-up screens, judged by what comes out and by ffmpeg decoding it again. The bit rate
// ceiling is the one display/src/video.c computed for "high": 8000 kbit/s for 1920 x 1080 at 60 pictures a
// second, scaled by area.
public sealed class EncoderTests
{
    private const int FramesPerSecond = 60;

    // A second of motion after the first half second, in which the key frame's debt is paid.
    private const int MotionPictures = 100;
    private const int Settling = FramesPerSecond / 2;

    public static TheoryData<int, int, string> Cases()
    {
        var data = new TheoryData<int, int, string>();
        foreach (var (width, height) in new[] { (640, 480), (1920, 1080), (3840, 2160) })
        {
            foreach (var scenario in new[] { "still", "scroll", "window", "noise" })
            {
                data.Add(width, height, scenario);
            }
        }

        return data;
    }

    // display/src/video.c, open_encoder().
    internal static int Ceiling(int width, int height, int framesPerSecond = FramesPerSecond, int perFullHd = 8000) =>
        Math.Max(500, (int)((long)perFullHd * width * height / (1920 * 1080) * (15 + framesPerSecond / 2) / 30));

    [Theory]
    [MemberData(nameof(Cases))]
    public void AScreenIsEncodedOnePictureForEachAndGetsSharpWhenItStandsStill(int width, int height, string scenario)
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        Assert.SkipUnless(Ffmpeg.IsAvailable, "ffmpeg is not installed.");
        var ceiling = Ceiling(width, height);
        using var recording = new Recording(width, height, FramesPerSecond, ceiling);
        switch (scenario)
        {
            case "still":
                Scenarios.Still(recording);
                break;
            case "scroll":
                Scenarios.Scroll(recording, MotionPictures);
                break;
            case "window":
                Scenarios.Window(recording, MotionPictures);
                break;
            default:
                Scenarios.Noise(recording, MotionPictures);
                break;
        }

        var pictures = recording.Pictures;
        var count = pictures.Count;

        // One access unit for every picture, none of them empty.
        Assert.Equal(scenario == "still" ? 3 : MotionPictures + 2, count);
        Assert.All(pictures, picture => Assert.True(picture.Bytes > 0, $"Picture {picture.Index} is empty."));

        // The first is a key frame with its parameter sets in front (and one IDR slice, four above the size
        // of 1920 x 1080); none after it is one.
        Assert.True(pictures[0].IsKey);
        Assert.Equal([AnnexB.Sps, AnnexB.Pps, .. Enumerable.Repeat(AnnexB.IdrSlice, width * height > 1920 * 1088 ? 4 : 1)], pictures[0].NalTypes);
        Assert.All(pictures.Skip(1), picture =>
        {
            Assert.False(picture.IsKey, $"Picture {picture.Index} is a key frame.");
            Assert.All(picture.NalTypes, type => Assert.Equal(AnnexB.Slice, type));
        });

        // While the screen moves - after the first half second, before the repeats - no second holds more
        // than the ceiling allows.
        var motion = pictures.Skip(1).Take(count - 3).ToList();
        var peak = 0.0;
        if (scenario != "still")
        {
            peak = PeakKilobitsPerSecond(motion.Skip(Settling).ToList(), 1000);
            Assert.True(peak <= ceiling, $"{peak:F0} kbit/s in one second of motion, the ceiling is {ceiling}.");
        }

        // ffmpeg sees as many pictures as went in, of the right size, in the stream's colours.
        var psnr = recording.DecodeAndCompare();
        Assert.Equal(count, psnr.Count);
        var header = recording.Probe();
        Assert.Equal(width.ToString(), header["width"]);
        Assert.Equal(height.ToString(), header["height"]);
        Assert.Equal("Constrained Baseline", header["profile"]);
        Assert.Equal("tv", header["color_range"]);
        Assert.Equal("smpte170m", header["color_space"]);

        // Standing still, the picture gets sharper with each repeat and ends up sharp: text as sharp as the
        // finest quantiser makes it, noise at least visibly better than while it moved.
        var last = psnr[count - 3];
        Assert.True(psnr[count - 2] >= last - 0.05 && psnr[count - 1] >= psnr[count - 2] - 0.05, $"The repeats made it worse: {last:F1}, {psnr[count - 2]:F1}, {psnr[count - 1]:F1} dB.");
        if (scenario == "noise")
        {
            Assert.True(psnr[count - 1] >= last + 5, $"Noise standing still: {last:F1} -> {psnr[count - 1]:F1} dB.");
        }
        else
        {
            Assert.True(psnr[count - 1] >= 42, $"The still picture ends at {psnr[count - 1]:F1} dB.");
            Assert.True(recording.Encoder.IsSharp);
        }

        if (scenario == "still")
        {
            Assert.True(psnr[1] >= psnr[0] + 5, $"The first repeat: {psnr[0]:F1} -> {psnr[1]:F1} dB.");
        }

        var average = motion.Count > 0 ? motion.Sum(picture => (long)picture.Bytes) * 8.0 * FramesPerSecond / motion.Count / 1000 : 0;
        Report.Line(
            $"quality {width}x{height} {scenario,-6}: ceiling {ceiling} kbit/s, motion average {average:F0} / peak {peak:F0} kbit/s, " +
            $"key {pictures[0].Bytes / 1024.0:F0} KB at {psnr[0]:F1} dB, motion min {(motion.Count > 0 ? psnr.Skip(1).Take(count - 3).Min() : 0):F1} dB, " +
            $"last moving {last:F1} dB -> repeat 1: {psnr[count - 2]:F1} dB ({pictures[count - 2].Bytes / 1024.0:F1} KB) -> repeat 2: {psnr[count - 1]:F1} dB ({pictures[count - 1].Bytes / 1024.0:F1} KB), " +
            $"mosaic pictures {pictures.Count(picture => picture.Block > 1)}");
    }

    [Fact]
    public void AKeyFrameComesWhenAskedAndOnlyThen()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        using var recording = new Recording(640, 480, FramesPerSecond, Ceiling(640, 480));
        var page = Pictures.TextPage(640, 960);
        for (var i = 0; i < 40; i++)
        {
            Pictures.Scroll(page, recording.Source, 640, 480, i * 4);
            recording.Show();
            var picture = recording.Encode(forceKey: i is 10 or 25);
            var key = i is 0 or 10 or 25;
            Assert.Equal(key, picture.IsKey);
            Assert.Equal(key ? [AnnexB.Sps, AnnexB.Pps, AnnexB.IdrSlice] : [AnnexB.Slice], picture.NalTypes);
        }

        // A key frame of a picture that did not change is a key frame too.
        Assert.True(recording.Encode(forceKey: true).IsKey);
        Assert.False(recording.Encode().IsKey);

        // The parameter sets never change, so the browser keeps its decoder.
        var stream = File.ReadAllBytes(recording.StreamPath);
        if (Ffmpeg.IsAvailable)
        {
            Assert.Equal(42, recording.DecodeAndCompare().Count);
            var packets = Ffmpeg.Packets(recording.StreamPath);
            Assert.Equal([0, 10, 25, 40], packets.Select((packet, index) => (packet, index)).Where(pair => pair.packet.IsKey).Select(pair => pair.index));
        }

        Assert.NotEmpty(stream);
    }

    [Theory]
    [InlineData(640, 480, 60, 0x1F)]
    [InlineData(1280, 720, 30, 0x1F)]
    [InlineData(1920, 1080, 30, 0x28)]
    [InlineData(1920, 1080, 60, 0x2A)]
    [InlineData(2560, 1440, 60, 0x33)]
    [InlineData(3840, 2160, 30, 0x33)]
    [InlineData(3840, 2160, 60, 0x34)]
    public void TheHeaderIsConstrainedBaselineWithTheLevelOfTheSize(int width, int height, int framesPerSecond, int level)
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        using var frame = new I420Frame(width, height);
        using var encoder = new H264Encoder(width, height, framesPerSecond, Ceiling(width, height, framesPerSecond));
        var unit = encoder.Encode(frame, false, out var isKey).ToArray();
        Assert.True(isKey);

        // The three bytes the browser makes its codec string of: avc1.42C0xx.
        Assert.Equal((0x42, 0xC0, level), AnnexB.SpsHeader(unit));
        Report.Line($"header {width}x{height}@{framesPerSecond}: avc1.42c0{level:x2}");
    }

    [Fact]
    public void AStillPictureIsSharpAfterTheRepeatsAndCostsNothingAfterThat()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        using var recording = new Recording(1920, 1080, FramesPerSecond, Ceiling(1920, 1080));
        Scenarios.Still(recording);
        Assert.True(recording.Encoder.IsSharp);
        var again = recording.Encode(after: 300);
        Assert.True(again.Bytes < 1000, $"Encoding a sharp picture again took {again.Bytes} bytes.");

        // With room to spare, a new page is coded as finely as a repeat would make it.
        Pictures.Scroll(Pictures.TextPage(1920, 2160, seed: 3), recording.Source, 1920, 1080, 0);
        recording.Show();
        recording.Encode();
        Assert.True(recording.Encoder.IsSharp);
    }

    [Fact]
    public void WhereTheBitRateIsTightAStillPictureTakesMoreRepeatsToGetSharp()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        Assert.SkipUnless(Ffmpeg.IsAvailable, "ffmpeg is not installed.");

        // 1500 kbit/s for 1920 x 1080: "low" in display/src/video.c, at 20 pictures a second.
        var ceiling = Ceiling(1920, 1080, 20, 1500);
        using var recording = new Recording(1920, 1080, 20, ceiling);
        Scenarios.Scroll(recording, 40, refine: false);
        Assert.False(recording.Encoder.IsSharp);
        var repeats = 0;
        while (!recording.Encoder.IsSharp && repeats < 30)
        {
            recording.Encode(after: Scenarios.RefinePause);
            repeats++;
        }

        Assert.True(recording.Encoder.IsSharp, "Still not sharp after 30 repeats.");
        var psnr = recording.DecodeAndCompare();
        var moving = psnr[40];
        Assert.True(psnr[^1] >= 40, $"The still picture ends at {psnr[^1]:F1} dB.");
        Report.Line($"tight: 1920x1080 at {ceiling} kbit/s - the last moving picture {moving:F1} dB, sharp ({psnr[^1]:F1} dB) after {repeats} repeats: {string.Join(", ", psnr.Skip(41).Select(value => value.ToString("F1")))} dB");
    }

    [Fact]
    public void APictureTooLargeForTheLibrarysBufferIsCodedAgainCoarserAsAKeyFrame()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);

        // Noise at the finest quantiser comes out larger than the raw picture, which the library cannot hold.
        using var recording = new Recording(640, 480, FramesPerSecond, 100000, new H264EncoderOptions { Quantisers = _ => 0 });
        Scenarios.Noise(recording, 3, refine: false);
        Assert.All(recording.Pictures, picture => Assert.True(picture.Bytes > 0));
        Assert.True(recording.Pictures.Skip(1).Any(picture => picture.IsKey), "The library held every picture.");
        Assert.True(recording.Pictures.All(picture => picture.Quantiser >= 12), "No picture was coded again.");
        if (Ffmpeg.IsAvailable)
        {
            Assert.Equal(3, recording.DecodeAndCompare().Count);
        }
    }

    [Fact]
    public void EncodersInParallelDoNotDisturbEachOther()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);

        // The same pictures through one encoder alone and through eight at once on eight threads: the
        // library is deterministic, so anything they shared would show as a difference.
        var alone = Play(0);
        var together = new byte[8][];
        Parallel.For(0, together.Length, new ParallelOptions { MaxDegreeOfParallelism = together.Length }, i => together[i] = Play(i));
        Assert.All(together, stream => Assert.Equal(alone, stream));

        static byte[] Play(int _)
        {
            using var recording = new Recording(640, 480, FramesPerSecond, Ceiling(640, 480)) { KeepReference = false };
            Scenarios.Window(recording, 30);
            Scenarios.Scroll(recording, 30);
            recording.Encode(forceKey: true);
            return File.ReadAllBytes(recording.StreamPath);
        }
    }

    [Fact]
    public void EncodersWithSlicesInParallelDoNotDisturbEachOther()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);

        // Above 1920 x 1080 the encoder codes four slices on the library's worker threads, which all such
        // encoders of the process share.
        var alone = Play();
        var together = new byte[4][];
        Parallel.For(0, together.Length, new ParallelOptions { MaxDegreeOfParallelism = together.Length }, i => together[i] = Play());
        Assert.All(together, stream => Assert.Equal(alone, stream));

        static byte[] Play()
        {
            using var recording = new Recording(2560, 1440, FramesPerSecond, Ceiling(2560, 1440)) { KeepReference = false };
            Scenarios.Window(recording, 20);
            Scenarios.Noise(recording, 10);
            return File.ReadAllBytes(recording.StreamPath);
        }
    }

    [Fact]
    public void WhatCannotBeEncodedIsRefused()
    {
        foreach (var (width, height) in new[] { (14, 16), (16, 14), (641, 480), (640, 481), (4098, 2304), (5120, 2160), (8704, 1072), (16384, 512) })
        {
            Assert.False(H264Encoder.Supports(width, height), $"{width}x{height}");
            Assert.Throws<ArgumentOutOfRangeException>(() => new H264Encoder(width, height, 60, 8000));
        }

        foreach (var (width, height) in new[] { (16, 16), (640, 480), (1366, 768), (3840, 2160), (4096, 2304), (5120, 1440), (8688, 1072) })
        {
            Assert.True(H264Encoder.Supports(width, height), $"{width}x{height}");
        }

        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Encoder(640, 480, 0, 8000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264Encoder(640, 480, 60, 0));
        var encoder = new H264Encoder(640, 480, 60, 4000);
        using var wrong = new I420Frame(320, 240);
        Assert.Throws<ArgumentException>(() => encoder.Encode(wrong, false, out _));
        using var frame = new I420Frame(640, 480);
        Assert.False(encoder.Encode(frame, false, out _).IsEmpty);
        encoder.Dispose();
        encoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.Encode(frame, false, out _));
    }

    // The highest bit rate (kbit/s) of any stretch of the given length among these pictures.
    internal static double PeakKilobitsPerSecond(List<EncodedPicture> pictures, double windowMilliseconds)
    {
        var peak = 0.0;
        for (var start = 0; start < pictures.Count; start++)
        {
            if (pictures[^1].Time - pictures[start].Time < windowMilliseconds - 1000.0 / FramesPerSecond)
            {
                break;
            }

            long bytes = 0;
            for (var i = start; i < pictures.Count && pictures[i].Time - pictures[start].Time < windowMilliseconds; i++)
            {
                bytes += pictures[i].Bytes;
            }

            peak = Math.Max(peak, bytes * 8 / windowMilliseconds);
        }

        return peak;
    }
}
