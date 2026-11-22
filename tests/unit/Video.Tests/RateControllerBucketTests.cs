using Xunit;

namespace Matgate.Display.Video.Tests;

// The rate controller's two sizes that the caller may choose since "fast" came: the bucket (how long one picture
// may hold the connection) and the overdraft (what a picture that stands still may borrow). Without them the
// controller is the one it was; with less of either, the first picture after a large change is coarser instead
// of later.
public sealed class RateControllerBucketTests
{
    // A key frame of a screen of text at 1920 x 1080 at quantiser 22, and what it shrinks to further up the
    // scale - as measured (see RateControllerTests).
    private const double KeyBits = 4_400_000;

    private static readonly (int Level, double Size)[] Sizes =
    [
        (22, 1.0), (28, 0.78), (34, 0.61), (40, 0.42), (46, 0.19), (52, 0.10), (58, 0.05), (64, 0.025), (70, 0.012),
    ];

    [Fact]
    public void WithoutABucketOrAnOverdraftTheControllerIsTheOneItWas()
    {
        Assert.Equal(0.5, RateController.DefaultBucketSeconds);
        Assert.Equal(0.25, RateController.DefaultOverdraftSeconds);

        // The same pictures to two controllers: one made as before, one told the old sizes - they choose alike.
        var before = new RateController(1920, 1080, 60, 12000, 22);
        var told = new RateController(1920, 1080, 60, 12000, 22, 0.5, 0.25);
        var choices = new List<(Coarseness Before, Coarseness Told, int WaitBefore, int WaitTold)>();
        var now = 0L;
        void Picture(PictureKind kind, double share, double cost)
        {
            var first = before.Choose(kind, share, now);
            var second = told.Choose(kind, share, now);
            var bits = (int)(cost * KeyBits * Size(Level(first)));
            before.Encoded(bits);
            told.Encoded(bits);
            choices.Add((first, second, before.Interval(share), told.Interval(share)));
        }

        Picture(PictureKind.Key, 1.0, 1.0);
        for (var i = 0; i < 6; i++)
        {
            now += 200;
            Picture(PictureKind.Repeat, 0, 0.02);
        }

        // A page scrolled in a sixth of the screen, too fast to follow, then the whole screen, then rest.
        foreach (var (share, cost, count) in new[] { (0.16, 0.16, 120), (1.0, 1.0, 60), (0.01, 0.01, 30) })
        {
            for (var i = 0; i < count; i++)
            {
                now += 17;
                Picture(PictureKind.Changed, share, cost);
            }
        }

        for (var i = 0; i < 6; i++)
        {
            now += 200;
            Picture(PictureKind.Repeat, 0, 0.02);
        }

        Assert.All(choices, choice =>
        {
            Assert.Equal(choice.Before, choice.Told);
            Assert.Equal(choice.WaitBefore, choice.WaitTold);
        });
        Assert.Contains(choices, choice => Level(choice.Before) > 34);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.15)]
    public void AKeyFrameIsExpectedToFitIntoTheBucket(double bucketSeconds)
    {
        // At the fastest response's ceiling for 1920 x 1080 and its floor. The first key frame tells the
        // controller what the screen costs; the screen stands still until the bucket is full again, and the next
        // key frame is made to fit into it: it holds the connection for no longer than the bucket says.
        Assert.True(KeyLevel(bucketSeconds, out var next) >= 26);
        Assert.True(KeyBits * Size(next) <= Ceiling * 1000.0 * bucketSeconds, $"A key frame at {next} takes {KeyBits * Size(next):N0} bits.");
    }

    [Fact]
    public void ASmallerBucketMakesAKeyFrameCoarserInsteadOfLater()
    {
        // The very first one, of a screen the controller knows nothing of yet, and the next one.
        var small = KeyLevel(0.15, out var smallNext);
        var usual = KeyLevel(0.5, out var usualNext);

        Assert.True(small > usual, $"The first: {small} with a bucket of 0.15 s, {usual} with one of half a second.");
        Assert.True(smallNext > usualNext, $"The next: {smallNext} with a bucket of 0.15 s, {usualNext} with one of half a second.");
    }

    [Fact]
    public void WhatAStillPictureMayBorrowIsTheOverdraft()
    {
        // A key frame that emptied the bucket and more, a first repeat that may not borrow, and a second one
        // that may: how much finer it gets is what it may borrow.
        int SecondRepeat(double overdraftSeconds)
        {
            var controller = new RateController(1920, 1080, 60, 12000, 22, 0.5, overdraftSeconds);
            controller.Choose(PictureKind.Key, 1.0, 0);
            controller.Encoded(12_000_000);
            controller.Choose(PictureKind.Repeat, 0, 100);
            controller.Encoded(2000);
            return Level(controller.Choose(PictureKind.Repeat, 0, 200));
        }

        var none = SecondRepeat(0);
        var little = SecondRepeat(0.05);
        var usual = SecondRepeat(0.25);
        Assert.True(usual < little && little < none, $"{usual} with 0.25 s, {little} with 0.05 s, {none} without.");
    }

    [Fact]
    public void ABucketOfNothingIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateController(1920, 1080, 60, 4500, 26, 0, 0.05));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateController(1920, 1080, 60, 4500, 26, 0.15, -0.05));
    }

    // The fastest response's ceiling for 1920 x 1080.
    private const int Ceiling = 4500;

    // The level of a first key frame at the fastest response's ceiling and floor, and that of the next one once
    // the screen stood still for ten seconds - with nothing owed, the bucket is all the next one has.
    private static int KeyLevel(double bucketSeconds, out int next)
    {
        var controller = new RateController(1920, 1080, 60, Ceiling, 26, bucketSeconds, 0.05);
        var first = Level(controller.Choose(PictureKind.Key, 1.0, 0));
        controller.Encoded((int)(KeyBits * Size(first)));
        controller.Choose(PictureKind.Repeat, 0, 10_000);
        controller.Encoded(2000);
        next = Level(controller.Choose(PictureKind.Key, 1.0, 10_017));
        return first;
    }

    private static int Level(Coarseness coarseness) => coarseness.Block switch
    {
        1 => coarseness.Quantiser,
        4 => 52,
        8 => coarseness.Quantiser <= 46 ? 58 : 64,
        _ => 70,
    };

    private static double Size(int level)
    {
        for (var i = 1; i < Sizes.Length; i++)
        {
            if (level <= Sizes[i].Level)
            {
                var (lowLevel, lowSize) = Sizes[i - 1];
                var (highLevel, highSize) = Sizes[i];
                return lowSize + (highSize - lowSize) * Math.Max(0, level - lowLevel) / (highLevel - lowLevel);
            }
        }

        return Sizes[^1].Size;
    }
}
