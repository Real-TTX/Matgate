using Xunit;

namespace Matgate.Display.Video.Tests;

// The rate controller on its own, with made-up pictures in place of an encoder: what it asks the caller to
// wait for, and what it does not. The pictures cost what the encoder was measured to take for a screen of
// text at 1920 x 1080 (see ScrollCostTests): a key frame 4400 kbit at quantiser 22, less at coarser ones.
public sealed class RateControllerTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Ceiling = 12000;
    private const double KeyBits = 4_400_000;

    // A key frame's size by the controller's scale, relative to 22 - what was measured.
    private static readonly (int Level, double Size)[] Sizes =
    [
        (22, 1.0), (28, 0.78), (34, 0.61), (40, 0.42), (46, 0.19), (52, 0.10), (58, 0.05), (64, 0.025), (70, 0.012),
    ];

    [Fact]
    public void BeforeAChangeTheEncoderCannotFollowGetsHardToReadTheCallerIsAskedToWait()
    {
        // A page that scrolls in a sixth of the screen, too fast to follow: every picture costs what coding
        // that part from nothing costs. At sixty pictures a second that only fits far beyond Readable; at a
        // few more than twenty it fits at Readable.
        var hurried = new Screen(60);
        hurried.Move(0.16, Screen.Unfollowed, 3000, wait: false);
        var patient = new Screen(60);
        patient.Move(0.16, Screen.Unfollowed, 3000, wait: true);

        Assert.True(hurried.Settled.Max(picture => picture.Level) >= 40, $"Without waiting the pictures stay at {hurried.Settled.Max(picture => picture.Level)}.");
        Assert.All(hurried.Moving, picture => Assert.Equal(1000.0 / 60, picture.Interval, 3));
        Assert.All(patient.Moving, picture => Assert.True(picture.Level <= RateController.Readable, $"A picture was coded at {picture.Level} although the caller waits."));
        Assert.Contains(patient.Moving, picture => picture.Interval > 30);
        Assert.All(patient.Moving, picture => Assert.InRange(picture.Interval, 1000.0 / 60 - 0.001, RateController.LongestInterval));

        // Fewer pictures, each of them finer - and no more bits than the connection carries in three seconds,
        // on top of the bucket it started with.
        Assert.True(patient.Moving.Count < hurried.Moving.Count * 2 / 3);
        Assert.True(patient.Moving.Sum(picture => picture.Bits) <= Ceiling * 1000.0 * 3.5, "More than the ceiling while waiting.");
    }

    [Fact]
    public void ASmallChangeIsNeverAskedToWait()
    {
        var screen = new Screen(60);
        screen.Move(0.6, Screen.Unfollowed, 1500, wait: true, after: () =>
        {
            // A cursor, a letter, a button - whatever the page next to them is asked to wait.
            Assert.Equal(0, screen.Controller.Interval(0.0));
            Assert.Equal(0, screen.Controller.Interval(0.0001));
            Assert.Equal(0, screen.Controller.Interval(0.01));
        });
        Assert.Contains(screen.Moving, picture => picture.Interval >= RateController.LongestInterval);
    }

    [Fact]
    public void TheWaitIsNeverLongerThanTwentyPicturesASecondTake()
    {
        // The whole screen, not followed: no waiting makes that fit at Readable. The caller waits as long as
        // it is ever asked to, and the pictures get coarser all the same.
        var screen = new Screen(60);
        screen.Move(1.0, Screen.Unfollowed, 3000, wait: true);
        Assert.All(screen.Moving, picture => Assert.InRange(picture.Interval, 1000.0 / 60 - 0.001, RateController.LongestInterval));
        Assert.Contains(screen.Settled, picture => picture.Interval == RateController.LongestInterval);
        Assert.True(screen.Settled.Max(picture => picture.Level) > RateController.Readable);

        // Where the caller's own pace is twenty pictures a second, there is nothing to ask for.
        var slow = new Screen(20);
        slow.Move(1.0, Screen.Unfollowed, 3000, wait: true);
        Assert.All(slow.Moving, picture => Assert.Equal(50.0, picture.Interval, 3));
    }

    [Fact]
    public void AChangeTheEncoderFollowsIsNotWaitedFor()
    {
        // The whole screen scrolls and the encoder follows it: a picture costs a tenth of coding it from
        // nothing, which is still too much for sixty a second at the floor. Waiting would move the page
        // further between two pictures, until the encoder loses it - so the pictures get coarser instead.
        var screen = new Screen(60);
        screen.Move(1.0, 0.1, 3000, wait: true);
        Assert.True(screen.Moving.Max(picture => picture.Level) >= RateController.Readable, "The pictures never got coarse: the scenario does not test anything.");
        Assert.All(screen.Moving, picture => Assert.Equal(1000.0 / 60, picture.Interval, 3));
    }

    [Fact]
    public void WhenTheScreenStandsStillNothingWaits()
    {
        var screen = new Screen(60);
        screen.Move(0.6, Screen.Unfollowed, 1500, wait: true);
        Assert.True(screen.Controller.Interval(0.6) > 0);

        // The repeat that makes it sharp, and whatever changes after it.
        screen.Repeat();
        Assert.Equal(0, screen.Controller.Interval(0.6));
        Assert.Equal(0, screen.Controller.Interval(1.0));
        while (!screen.Controller.IsSharp)
        {
            screen.Repeat();
        }

        // The next motion begins at the caller's pace: the bucket pays for a burst as it always did.
        screen.Move(0.6, Screen.Unfollowed, 60, wait: true);
        Assert.All(screen.Moving, picture => Assert.Equal(1000.0 / 60, picture.Interval, 3));
    }

    [Fact]
    public void AFullBucketIsSpentOnTryingTheCallersPaceAgain()
    {
        // Waiting, the bucket fills. Then the pictures come at the caller's pace until it is empty - which
        // is no use where the encoder cannot follow whatever the pace, and the waiting goes on.
        var lost = new Screen(60);
        lost.Move(0.16, Screen.Unfollowed, 8000, wait: true);
        var waits = lost.Moving.Select(picture => picture.Interval > 20).ToList();
        var tries = Enumerable.Range(1, waits.Count - 1).Count(i => waits[i - 1] && !waits[i]);
        Assert.True(tries >= 2, $"The caller's pace was tried {tries} times in eight seconds.");
        Assert.True(waits[^1] || waits.Skip(waits.Count - 40).Any(wait => wait), "The waiting did not go on.");
        Assert.All(lost.Moving, picture => Assert.True(picture.Level <= RateController.Readable));

        // Where the encoder finds the motion again once the pictures come quickly - ten of them, after the
        // waiting made it lose it - they get small, nobody waits any more, and they get finer than Readable.
        var found = new Screen(60);
        var waited = false;
        found.Move(
            0.16,
            quickly =>
            {
                waited |= quickly == 0;
                return waited && quickly >= 10 ? 0.1 : Screen.Unfollowed;
            },
            8000,
            wait: true);
        var end = found.Moving.Skip(found.Moving.Count - 60).ToList();
        Assert.Contains(found.Moving, picture => picture.Interval > 30);
        Assert.All(end, picture => Assert.Equal(1000.0 / 60, picture.Interval, 3));
        Assert.True(end.Max(picture => picture.Level) < RateController.Readable, $"The pictures stay at {end.Max(picture => picture.Level)}.");
    }

    internal sealed record Picture(double Time, double Interval, int Level, double Bits);

    // A screen of text behind a controller: a key frame, the repeats that make it sharp, and then whatever
    // the test moves on it.
    internal sealed class Screen
    {
        // The part of coding it from nothing that a change costs which the encoder does not follow at all.
        public const double Unfollowed = 1.0;

        private readonly double _pace;
        private double _now;

        public Screen(int framesPerSecond)
        {
            _pace = 1000.0 / framesPerSecond;
            Controller = new RateController(Width, Height, framesPerSecond, Ceiling, 22);
            Controller.Encoded((int)(KeyBits * Size(Level(Controller.Choose(PictureKind.Key, 1.0, 0)))));
            while (!Controller.IsSharp)
            {
                Repeat();
            }
        }

        public RateController Controller { get; }

        // The pictures of the last motion, and those of them after its first second.
        public List<Picture> Moving { get; private set; } = [];

        public List<Picture> Settled => Moving.Where(picture => picture.Time >= Moving[0].Time + 1000).ToList();

        public void Repeat()
        {
            _now += 300;
            var level = Level(Controller.Choose(PictureKind.Repeat, 0, (long)_now));

            // Making it sharp costs a part of a key frame the first time, next to nothing after that.
            Controller.Encoded(Controller.IsSharp || level > 22 ? 2000 : (int)(0.02 * KeyBits));
        }

        public void Move(double share, double cost, double milliseconds, bool wait, Action? after = null) =>
            Move(share, _ => cost, milliseconds, wait, after);

        // That part of the screen changes with every picture for so long. A picture costs the given part of
        // what coding the changed part from nothing costs; it may depend on how many pictures came at the
        // caller's own pace one after another.
        public void Move(double share, Func<int, double> cost, double milliseconds, bool wait, Action? after = null)
        {
            Moving = [];
            var start = _now;
            var quickly = 0;
            while (true)
            {
                var interval = wait ? Math.Max(_pace, Controller.Interval(share)) : _pace;
                if (_now + interval - start > milliseconds)
                {
                    break;
                }

                _now += interval;
                quickly = interval > _pace ? 0 : quickly + 1;
                var level = Level(Controller.Choose(PictureKind.Changed, share, (long)_now));
                var bits = cost(quickly) * share * KeyBits * Size(level);
                Controller.Encoded((int)bits);
                Moving.Add(new Picture(_now, interval, level, bits));
                after?.Invoke();
            }
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
                    return lowSize + (highSize - lowSize) * (level - lowLevel) / (highLevel - lowLevel);
                }
            }

            return Sizes[^1].Size;
        }
    }
}
