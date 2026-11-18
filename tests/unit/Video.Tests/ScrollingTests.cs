using Xunit;

namespace Matgate.Display.Video.Tests;

// A page that scrolls faster than the connection carries, in one part of the screen, next to fine text that
// stands still - what a browser in a remote desktop does when the wheel is turned. Two things are asked of
// the encoder then: whatever it has to make coarse, it makes coarse only where the screen changed; and before
// it makes the page hard to read it asks for fewer pictures.
public sealed class ScrollingTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int FramesPerSecond = 60;

    // The part that scrolls, not on the grid of the macroblocks on purpose, and the macroblocks it touches.
    private const int Left = 0;
    private const int Top = 40;
    private const int PartWidth = 1270;
    private const int PartHeight = 960;
    private const int GridRight = 1280;
    private const int GridTop = 32;
    private const int GridBottom = 1008;

    // Fifty rows from one picture to the next at sixty pictures a second: more than the encoder follows.
    private const double Fast = 3000;

    [Fact]
    public void OnlyWhatChangedIsCodedAsAMosaic()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        using var recording = Screen();
        using var mosaic = new I420Frame(Width, Height);
        var encoder = recording.Encoder;
        var first = recording.Pictures.Count;
        var page = Pictures.TextPage(PartWidth, Height * 4, seed: 5);
        var coarse = 0;

        // At the session's pace whatever happens, so that the pictures have to get as coarse as they can.
        Scenarios.ScrollPart(recording, page, Left, Top, PartWidth, PartHeight, Fast, 600, paced: false, picture =>
        {
            Assert.Equal(encoder.MosaicMacroblocks > 0, picture.Block > 1);
            if (picture.Block == 1)
            {
                Assert.Null(encoder.LastShown);
                return;
            }

            coarse++;
            Assert.False(picture.Sharp, $"Picture {picture.Index} is a mosaic and counts as sharp.");

            // What changed, widened to whole macroblocks - and nothing else.
            Assert.Equal((Left, GridTop, GridRight, GridBottom), encoder.LastMosaic);
            Assert.Equal(GridRight / 16 * ((GridBottom - GridTop) / 16), encoder.MosaicMacroblocks);

            // The library was given the real picture outside of it and the averaged one inside.
            var shown = encoder.LastShown;
            Assert.NotNull(shown);
            Mosaic.Render(recording.Frame, mosaic, picture.Block);
            Assert.True(
                Same(shown, recording.Frame, mosaic, Left, GridTop, GridRight, GridBottom),
                $"Picture {picture.Index}: what the library was given is not the frame with a mosaic of {picture.Block} where it changed.");
        });

        var moving = recording.Pictures.Count - first;
        Assert.True(coarse > 0, "No picture was coded as a mosaic: the scenario does not test anything.");

        // Standing still, it gets sharp everywhere, and only then says so.
        var repeats = 0;
        while (!encoder.IsSharp && repeats < 40)
        {
            var picture = recording.Encode(after: Scenarios.RefinePause);
            repeats++;
            Assert.False(picture.Sharp && (picture.Block > 1 || encoder.MosaicMacroblocks > 0), $"Repeat {repeats} is a mosaic and counts as sharp.");
        }

        Assert.True(encoder.IsSharp, "Still not sharp after 40 repeats.");
        Assert.Equal(0, encoder.MosaicMacroblocks);
        Assert.Null(encoder.LastShown);
        Assert.Equal(1, recording.Pictures[^1].Block);

        var report = $"mosaic only where it changed: {moving} pictures of a page scrolling in {PartWidth}x{PartHeight} of {Width}x{Height} at {recording.KilobitsPerSecond} kbit/s, " +
            $"{coarse} of them a mosaic (blocks of up to {recording.Pictures.Max(picture => picture.Block)}), sharp after {repeats} repeats " +
            $"of {string.Join(", ", recording.Pictures.Skip(first + moving).Select(picture => picture.Bytes * 8 / 1000))} kbit";
        if (Ffmpeg.IsAvailable)
        {
            // What the viewer sees of the text that stood still, picture by picture: as sharp as it was.
            // The library's loop filter smooths the edge between two macroblocks, so the column of macroblocks
            // next to the mosaic loses a few pixels along that edge; beyond it nothing changes at all.
            var still = recording.DecodeAndCompare(GridRight + 16, 0, Width - GridRight - 16, Height);
            var before = still[first - 1];
            var worst = still.Skip(first).Take(moving).Min();
            Assert.True(worst >= before - 0.2, $"The text that stood still went from {before:F1} dB to {worst:F1} dB while the page scrolled.");
            var edge = recording.DecodeAndCompare(GridRight, 0, 16, Height);
            var edgeWorst = edge.Skip(first).Take(moving).Min();
            Assert.True(edgeWorst >= 30, $"The text next to the mosaic went from {edge[first - 1]:F1} dB to {edgeWorst:F1} dB.");

            var part = recording.DecodeAndCompare(Left, GridTop, GridRight, GridBottom - GridTop);
            var whole = recording.DecodeAndCompare();
            Assert.True(whole[^1] >= 42, $"The still picture ends at {whole[^1]:F1} dB.");
            Assert.True(part[^1] >= 42, $"The part that scrolled ends at {part[^1]:F1} dB.");
            report += $"; the text that stood still {before:F1} dB before, at least {worst:F1} dB while the page scrolled ({edge[first - 1]:F1} -> {edgeWorst:F1} dB in the 16 pixels next to the mosaic, " +
                $"the page itself down to {part.Skip(first).Take(moving).Min():F1} dB), the whole picture {whole[^1]:F1} dB at the end";
        }

        Report.Line(report);
    }

    [Fact]
    public void ASmallChangeElsewhereLeavesTheMosaicWhereItIsAndBecomesOneItself()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        using var recording = Screen();
        using var before = new I420Frame(Width, Height);
        using var mosaic = new I420Frame(Width, Height);
        var encoder = recording.Encoder;
        var page = Pictures.TextPage(PartWidth, Height * 4, seed: 5);
        Scenarios.ScrollPart(recording, page, Left, Top, PartWidth, PartHeight, Fast, 600, paced: false);
        Assert.True(recording.Pictures[^1].Block > 1, "The page was not coded as a mosaic: the scenario does not test anything.");
        before.CopyFrom(encoder.LastShown!);
        var macroblocks = encoder.MosaicMacroblocks;

        // A cursor in the text that stands still, right after the last picture of the page.
        const int X = 1600;
        const int Y = 512;
        Pictures.Cursor(recording.Source, Width, X, Y, visible: true);
        recording.Show(X, Y, 2, 16);
        var picture = recording.Encode();
        Assert.True(picture.Block > 1, "The cursor came when the pictures were no longer coarse.");
        Assert.Equal((X, Y, X + 16, Y + 16), encoder.LastMosaic);
        Assert.Equal(macroblocks + 1, encoder.MosaicMacroblocks);
        Assert.False(picture.Sharp);

        // Everything but the cursor's macroblock is exactly what the library was given before.
        Mosaic.Render(recording.Frame, mosaic, picture.Block);
        Assert.True(Same(encoder.LastShown!, before, mosaic, X, Y, X + 16, Y + 16), "More than the cursor's macroblock changed in what the library was given.");

        while (!encoder.IsSharp && recording.Pictures.Count < 200)
        {
            recording.Encode(after: Scenarios.RefinePause);
        }

        Assert.True(encoder.IsSharp);
        Assert.Equal(0, encoder.MosaicMacroblocks);
    }

    [Fact]
    public void NothingStaysCoarserThanTheLastPictureWasCoded()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        using var recording = Screen();
        var encoder = recording.Encoder;
        var page = Pictures.TextPage(PartWidth, Height * 4, seed: 5);
        var mosaics = new Dictionary<int, I420Frame>();
        try
        {
            foreach (var block in new[] { 4, 8, 16 })
            {
                mosaics[block] = new I420Frame(Width, Height);
            }

            // The large part scrolls until it is a coarse mosaic, then only its upper third goes on: the
            // pictures get finer, and the two thirds that came to rest must not be left behind as they are -
            // the rate controller takes all of the picture to be as fine as the last one was coded.
            Scenarios.ScrollPart(recording, page, Left, Top, PartWidth, PartHeight, Fast, 500, paced: false);
            var coarsest = recording.Pictures[^1].Block;
            Assert.True(coarsest > 4, "The page was not coded as a coarse mosaic: the scenario does not test anything.");
            var finer = 0;
            Scenarios.ScrollPart(recording, page, Left, Top, PartWidth, 320, Fast, 1500, paced: false, picture =>
            {
                if (picture.Block == 1)
                {
                    Assert.Equal(0, encoder.MosaicMacroblocks);
                    return;
                }

                finer += picture.Block < coarsest ? 1 : 0;
                foreach (var (block, mosaic) in mosaics)
                {
                    Mosaic.Render(recording.Frame, mosaic, block);
                }

                // Every macroblock is the frame's, or the frame's averaged over blocks no larger than this
                // picture's - the part that came to rest still holds what it held when it was averaged.
                var shown = encoder.LastShown!;
                var averaged = 0;
                for (var y = 0; y < Height; y += 16)
                {
                    for (var x = 0; x < Width; x += 16)
                    {
                        var bottom = Math.Min(Height, y + 16);
                        if (Same(shown, recording.Frame, recording.Frame, x, y, x + 16, bottom, only: true))
                        {
                            continue;
                        }

                        averaged++;
                        Assert.True(
                            mosaics.Any(pair => pair.Key <= picture.Block && Same(shown, pair.Value, pair.Value, x, y, x + 16, bottom, only: true)),
                            $"Picture {picture.Index}, coded with blocks of {picture.Block}: the macroblock at {x},{y} is coarser than that.");
                    }
                }

                Assert.True(averaged <= encoder.MosaicMacroblocks);
            });

            Assert.True(finer > 0, "The pictures did not get finer while the mosaic was there: the scenario does not test anything.");
        }
        finally
        {
            foreach (var mosaic in mosaics.Values)
            {
                mosaic.Dispose();
            }
        }
    }

    [Fact]
    public void AKeyFrameThatHasToBeCoarseIsAMosaicOfTheWholePicture()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);

        // Noise empties the bucket; a key frame asked for then cannot be anything but coarse, and it is
        // coded from nothing - so all of it, not only what changed last.
        using var recording = new Recording(640, 480, FramesPerSecond, EncoderTests.Ceiling(640, 480));
        using var mosaic = new I420Frame(640, 480);
        var encoder = recording.Encoder;
        Scenarios.Noise(recording, 20, refine: false);
        var key = recording.Encode(forceKey: true);
        Assert.True(key.IsKey);
        Assert.True(key.Block > 1, "The key frame was not coded as a mosaic: the scenario does not test anything.");
        Assert.Equal((0, 0, 640, 480), encoder.LastMosaic);
        Assert.Equal(640 / 16 * (480 / 16), encoder.MosaicMacroblocks);
        Mosaic.Render(recording.Frame, mosaic, key.Block);
        Assert.True(Same(encoder.LastShown!, mosaic, mosaic, 0, 0, 640, 480));
        Assert.False(key.Sharp);

        var repeats = 0;
        while (!encoder.IsSharp && repeats < 40)
        {
            recording.Encode(after: Scenarios.RefinePause);
            repeats++;
        }

        Assert.True(encoder.IsSharp, "Still not sharp after 40 repeats.");
        Assert.Equal(0, encoder.MosaicMacroblocks);
        Assert.Equal(1, recording.Pictures[^1].Block);
    }

    // A turn of the wheel: the page scrolls for a second, too fast for the encoder to follow at once. rows
    // is the height of the part that scrolls, speed is in rows a second - fifty or a hundred from one picture
    // to the next at the session's pace.
    [Theory]
    [InlineData(320, 3000)]
    [InlineData(640, 3000)]
    [InlineData(PartHeight, 3000)]
    [InlineData(PartHeight, 6000)]
    public void BeforeThePageGetsHardToReadTheEncoderAsksForFewerPictures(int rows, int speed)
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);
        var page = Pictures.TextPage(PartWidth, Height * 4, seed: 5);
        var nominal = 1000.0 / FramesPerSecond;
        var runs = new Dictionary<bool, (int Pictures, int Mosaics, int Coarsest, double Average, double Worst)>();
        foreach (var paced in new[] { false, true })
        {
            using var recording = Screen();
            var encoder = recording.Encoder;
            var first = recording.Pictures.Count;
            var intervals = new List<double>();
            var last = recording.Now;
            Scenarios.ScrollPart(recording, page, Left, Top, PartWidth, rows, speed, 1000, paced, picture =>
            {
                intervals.Add(picture.Time - last);
                last = picture.Time;

                // However long the page is asked to wait: a cursor, a letter, a button is not.
                Assert.Equal(0, encoder.SuggestedInterval(2 * 16));
                Assert.Equal(0, encoder.SuggestedInterval(200 * 100));
            });

            var moving = recording.Pictures.Skip(first).ToList();
            Assert.All(intervals, interval => Assert.InRange(interval, nominal - 0.001, RateController.LongestInterval + 0.001));
            if (paced)
            {
                Assert.Contains(intervals, interval => interval > nominal + 1);
            }

            var average = 0.0;
            var worst = 0.0;
            if (Ffmpeg.IsAvailable)
            {
                var part = recording.DecodeAndCompare(Left, Top, PartWidth, rows).Skip(first).ToList();
                average = part.Average();
                worst = part.Min();
            }

            runs[paced] = (moving.Count, moving.Count(picture => picture.Block > 1), moving.Max(Level), average, worst);
            Report.Line(
                $"a second of scrolling, {PartWidth}x{rows} of {Width}x{Height} at {speed} rows/s and {recording.KilobitsPerSecond} kbit/s, {(paced ? "waiting when asked" : "never waiting   ")}: " +
                $"{moving.Count} pictures, {moving.Count(picture => picture.Block > 1)} of them a mosaic, none coarser than quantiser {moving.Max(picture => picture.Quantiser)} / blocks of {moving.Max(picture => picture.Block)}, " +
                $"the page {average:F1} dB on average and at least {worst:F1} dB, {moving.Sum(picture => (long)picture.Bytes) * 8 / 1000} kbit");

            // The page stops: the repeats come at the session's own timing, and what changes next does not wait.
            recording.Encode(after: Scenarios.RefinePause);
            Assert.Equal(0, encoder.SuggestedInterval((long)Width * Height));
        }

        // Fewer pictures, none of them a mosaic, and each of them easier to read.
        var hurried = runs[false];
        var patient = runs[true];
        Assert.True(patient.Pictures < hurried.Pictures);
        Assert.Equal(0, patient.Mosaics);
        Assert.True(patient.Coarsest <= hurried.Coarsest, $"Waiting made the pictures coarser: {patient.Coarsest} against {hurried.Coarsest}.");
        if (Ffmpeg.IsAvailable)
        {
            Assert.True(patient.Average >= hurried.Average + 2, $"Waiting did not make the page sharper: {patient.Average:F1} dB on average against {hurried.Average:F1} dB.");
            Assert.True(patient.Worst >= hurried.Worst + 2, $"Waiting did not make the worst picture of the page sharper: {patient.Worst:F1} dB against {hurried.Worst:F1} dB.");
        }
    }

    [Fact]
    public void AScrollTheEncoderFindsAgainGetsItsPaceBack()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);

        // Fifty rows from one picture to the next is more than the encoder follows at once, but it finds a
        // steady motion like that after a while - as long as the pictures come quickly: three times as far
        // it never finds. So the waiting must not go on for ever: when the bucket is full again the session's
        // own pace is tried, and it stays.
        using var recording = Screen();
        var first = recording.Pictures.Count;
        var page = Pictures.TextPage(PartWidth, Height * 4, seed: 5);
        var intervals = new List<double>();
        var last = recording.Now;
        Scenarios.ScrollPart(recording, page, Left, Top, PartWidth, 320, Fast, 8000, paced: true, picture =>
        {
            intervals.Add(picture.Time - last);
            last = picture.Time;
        });

        var moving = recording.Pictures.Skip(first).ToList();
        var nominal = 1000.0 / FramesPerSecond;
        var end = moving.Count - FramesPerSecond;
        Assert.Contains(intervals, interval => interval > 2 * nominal);
        Assert.All(intervals.Skip(end), interval => Assert.Equal(nominal, interval, 3));
        Assert.All(moving.Skip(end), picture => Assert.True(picture.Block == 1 && picture.Quantiser < RateController.Readable, $"Picture {picture.Index} is coded at {picture.Quantiser}."));

        var psnr = Ffmpeg.IsAvailable ? recording.DecodeAndCompare(Left, Top, PartWidth, 320).Skip(first).ToList() : null;
        var seconds = new List<string>();
        for (var second = 0; second < 8; second++)
        {
            var indexes = Enumerable.Range(0, moving.Count).Where(i => (int)((moving[i].Time - moving[0].Time) / 1000) == second).ToList();
            seconds.Add($"{indexes.Count} pictures" + (psnr is null ? "" : $" at {indexes.Average(i => psnr[i]):F1} dB"));
        }

        if (psnr is not null)
        {
            Assert.True(psnr.Skip(end).Min() >= 40, $"The page ends at {psnr.Skip(end).Min():F1} dB.");
        }

        Report.Line($"eight seconds of steady scrolling, {PartWidth}x320 of {Width}x{Height} at {Fast:F0} rows/s, waiting when asked - second by second: {string.Join(", ", seconds)}");
    }

    [Fact]
    public void AScrollTheEncoderFollowsIsNotSlowedDown()
    {
        Assert.SkipUnless(H264Encoder.IsAvailable, "libopenh264 is not available: " + H264Encoder.UnavailableReason);

        // An eighth of the bit rate: even the small pictures of a page the encoder follows are too large for
        // it, and they get coarse. Fewer of them would move the page further from one to the next, until the
        // encoder loses it - so here it does not ask for fewer.
        using var recording = new Recording(Width, Height, FramesPerSecond, EncoderTests.Ceiling(Width, Height) / 8);
        var page = Pictures.TextPage(Width, Height * 4);
        var coarsest = 0;
        for (var i = 0; i < 150; i++)
        {
            Pictures.Scroll(page, recording.Source, Width, Height, i * 6);
            recording.Show();
            var picture = recording.Encode();
            coarsest = Math.Max(coarsest, picture.Quantiser);
            Assert.Equal(0, recording.Encoder.SuggestedInterval((long)Width * Height));
        }

        Assert.True(coarsest >= RateController.Readable, $"The pictures never got coarser than {coarsest}: the scenario does not test anything.");
        Report.Line($"a scroll the encoder follows at {recording.KilobitsPerSecond} kbit/s: up to quantiser {coarsest}, never asked to wait");
    }

    // The screen before anything moves: fine text everywhere, encoded until it is sharp.
    private static Recording Screen()
    {
        var recording = new Recording(Width, Height, FramesPerSecond, EncoderTests.Ceiling(Width, Height));
        Pictures.TextPage(Width, Height).CopyTo(recording.Source, 0);
        recording.Show();
        recording.Encode();
        while (!recording.Encoder.IsSharp && recording.Pictures.Count < 10)
        {
            recording.Encode(after: Scenarios.RefinePause);
        }

        Assert.True(recording.Encoder.IsSharp);
        return recording;
    }

    // Where a picture is on the rate controller's one scale of coarseness.
    private static int Level(EncodedPicture picture) => picture.Block switch
    {
        1 => picture.Quantiser,
        4 => 52,
        8 => picture.Quantiser <= 46 ? 58 : 64,
        _ => 70,
    };

    // Whether the picture is the one frame inside the rectangle and the other outside of it, in all three
    // planes. The rectangle has even edges. only: what is outside of it is not looked at.
    private static bool Same(I420Frame picture, I420Frame outside, I420Frame inside, int left, int top, int right, int bottom, bool only = false) =>
        Same(picture.Y, outside.Y, inside.Y, picture.StrideY, picture.Width, picture.Height, left, top, right, bottom, only)
        && Same(picture.U, outside.U, inside.U, picture.StrideU, picture.Width / 2, picture.Height / 2, left / 2, top / 2, right / 2, bottom / 2, only)
        && Same(picture.V, outside.V, inside.V, picture.StrideV, picture.Width / 2, picture.Height / 2, left / 2, top / 2, right / 2, bottom / 2, only);

    private static bool Same(Span<byte> picture, Span<byte> outside, Span<byte> inside, int stride, int width, int height, int left, int top, int right, int bottom, bool only)
    {
        for (var y = only ? top : 0; y < (only ? bottom : height); y++)
        {
            var row = picture.Slice(y * stride, width);
            var around = outside.Slice(y * stride, width);
            if (y < top || y >= bottom)
            {
                if (!row.SequenceEqual(around))
                {
                    return false;
                }

                continue;
            }

            if (!row[left..right].SequenceEqual(inside.Slice(y * stride + left, right - left)))
            {
                return false;
            }

            if (!only && (!row[..left].SequenceEqual(around[..left]) || !row[right..].SequenceEqual(around[right..])))
            {
                return false;
            }
        }

        return true;
    }
}
