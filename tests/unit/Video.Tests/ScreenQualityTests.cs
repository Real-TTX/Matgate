using Xunit;

namespace Matgate.Display.Video.Tests;

// What each level of "Picture quality" in a session's Display dialog means for the engine. The three levels the
// dialog always had keep every number they had before "fast" came (ScreenSession had them as constants and a
// switch); "fast" is checked against the plan it was built from.
public sealed class ScreenQualityTests
{
    // The sizes the dialog's ceilings were worked out for, and one below the half-megabit floor.
    private static readonly (int Width, int Height)[] Sizes = [(1280, 720), (1920, 1080), (2560, 1440), (3840, 2160), (640, 360)];

    // How ScreenSession computed the encoder's ceiling before ScreenQuality existed (and EncoderTests' Ceiling()).
    private static int Before(int bitratePerFullHd, int framesPerSecond, int width, int height) =>
        Math.Max(500, (int)((long)bitratePerFullHd * width * height / (1920 * 1080) * (15 + framesPerSecond / 2) / 30));

    [Theory]
    [InlineData("auto", 60, 8000, new[] { 5332, 12000, 21333, 48000, 1332 })]
    [InlineData("medium", 30, 4000, new[] { 1777, 4000, 7111, 16000, 500 })]
    [InlineData("low", 20, 1500, new[] { 555, 1250, 2221, 5000, 500 })]
    public void TheThreeLevelsTheDialogAlwaysHadKeepEveryNumber(string name, int framesPerSecond, int bitratePerFullHd, int[] ceilings)
    {
        var quality = ScreenQuality.From(name);

        Assert.Equal(name, quality.Name);
        Assert.Equal(framesPerSecond, quality.FramesPerSecond);
        Assert.Equal(bitratePerFullHd, quality.BitratePerFullHd);
        // What was the same for all three before.
        Assert.Equal(3, quality.MaxInFlight);
        Assert.True(quality.Pace);
        Assert.Equal(32, quality.RdpColorDepth);
        Assert.Equal(0x180u, quality.RdpPerformanceFlags);
        // The encoder is made exactly as the four-argument constructor makes it.
        Assert.Equal(new H264EncoderOptions(), quality.EncoderOptions());
        Assert.Equal(22, quality.MinQp);
        Assert.Equal(0.5, quality.BucketSeconds);
        Assert.Equal(0.25, quality.OverdraftSeconds);

        for (var i = 0; i < Sizes.Length; i++)
        {
            var (width, height) = Sizes[i];
            Assert.Equal(Before(bitratePerFullHd, framesPerSecond, width, height), quality.KilobitsPerSecond(width, height));
            Assert.Equal(ceilings[i], quality.KilobitsPerSecond(width, height));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("high")]
    [InlineData("Fast")]
    [InlineData(" fast")]
    [InlineData("1234567890abcdef")]
    public void AnythingUnknownIsSmoothAsItWasBefore(string? name)
    {
        var quality = ScreenQuality.From(name);

        Assert.Same(ScreenQuality.Smooth, quality);
        Assert.Equal("auto", quality.Name);
    }

    [Fact]
    public void TheFastestResponseGivesUpColoursAndSharpnessForTime()
    {
        var fast = ScreenQuality.From("fast");

        Assert.Same(ScreenQuality.Fastest, fast);
        Assert.Equal("fast", fast.Name);
        // Every picture there is, at sixty a second - none of them waits for the network.
        Assert.Equal(60, fast.FramesPerSecond);
        Assert.False(fast.Pace);
        Assert.Equal(2, fast.MaxInFlight);
        // Less to send of each, and no burst that holds the connection for long.
        Assert.Equal(3000, fast.BitratePerFullHd);
        Assert.Equal(0.15, fast.BucketSeconds);
        Assert.Equal(0.05, fast.OverdraftSeconds);
        Assert.Equal(26, fast.MinQp);
        Assert.Equal(new H264EncoderOptions { MinQp = 26, BucketSeconds = 0.15, OverdraftSeconds = 0.05 }, fast.EncoderOptions());
        Assert.Equal(new[] { 1999, 4500, 7999, 18000, 500 }, Sizes.Select(size => fast.KilobitsPerSecond(size.Width, size.Height)).ToArray());
        // The remote desktop: 16 bits a pixel, and nothing that only costs pictures - no wallpaper, no window
        // contents while dragging, no menu animations, no visual styles, no cursor shadow or blinking, no
        // smoothed fonts, no desktop composition.
        Assert.Equal(16, fast.RdpColorDepth);
        Assert.Equal(0x01u | 0x02u | 0x04u | 0x08u | 0x20u | 0x40u, fast.RdpPerformanceFlags);
    }

    [Fact]
    public void OnlyWhatTheEncoderIsMadeWithMakesANewEncoder()
    {
        ScreenQuality[] levels = [ScreenQuality.Smooth, ScreenQuality.Medium, ScreenQuality.LowData, ScreenQuality.Fastest];
        foreach (var level in levels)
        {
            foreach (var other in levels)
            {
                Assert.Equal(ReferenceEquals(level, other), level.SameEncoder(other));
            }
        }

        // How many pictures are on their way and whether they wait are the session's, not the encoder's; the
        // colours are the connection's.
        Assert.True(ScreenQuality.Smooth.SameEncoder(ScreenQuality.Smooth with { MaxInFlight = 2, Pace = false, RdpColorDepth = 16 }));
        Assert.False(ScreenQuality.Smooth.SameEncoder(ScreenQuality.Smooth with { MinQp = 26 }));
        Assert.False(ScreenQuality.Smooth.SameEncoder(ScreenQuality.Smooth with { BucketSeconds = 0.15 }));
        Assert.False(ScreenQuality.Smooth.SameEncoder(ScreenQuality.Smooth with { OverdraftSeconds = 0.05 }));
    }
}
