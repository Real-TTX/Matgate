namespace Matgate.Display.Video;

internal enum PictureKind
{
    // Coded without reference to anything before it.
    Key,

    // Something in it is new.
    Changed,

    // The same picture as the one before: the caller wants it sharper.
    Repeat,
}

// How a picture is coded: with which quantiser, and - only where even a coarse quantiser does not make it
// small enough - averaged over blocks of that many pixels first.
internal readonly record struct Coarseness(int Quantiser, int Block);

// Chooses how coarsely every picture is coded: as fine as the floor allows where the screen stands still or
// little moves, coarser where the pictures would otherwise not fit through the connection. It stands in for the
// library's own rate control, which is made for a camera: that one takes many pictures to make a still screen
// sharp and has no way of keeping a bit rate without leaving pictures out.
//
// The connection is a bucket that fills with the allowed bit rate and holds half a second of it; every picture
// takes its bits out. A full bucket pays for a sharp key frame or a change of the whole screen at once. An
// overdrawn one is no reason to act as long as the pictures are smaller than what flows in - then it fills by
// itself. Only when they stay larger does the picture get coarser, step by step, and finer again when there is
// room. Changing the coarseness is not free: a screen of text that was coded coarsely costs a third of a key
// frame to make sharp again, and a jump in either direction makes the next picture larger than staying put
// would have. So the controller would rather go into debt for a moment than move.
//
// Coarseness is one scale. Up to FinestMosaic it is H.264's quantiser. Some pictures do not get smaller that
// way - text scrolled faster than the encoder can follow costs as much at 46 as at 34, and more at 51 - so
// above it the picture is averaged over blocks of 4, 8 and 16 pixels before it is coded, which always helps.
// The encoder does that only where the picture changed; the scale still speaks of the whole picture, which is
// then at most as coarse as it says.
//
// There is a second way to make the pictures fit: fewer of them. Up to Readable the controller prefers many
// pictures - a little coarser is hard to see in motion. From there on text gets hard to read, so before it
// goes further it asks the caller to leave more time between the pictures (Interval): every picture then has
// more flowing in for it. Only when twenty pictures a second are still too many does the picture get coarser
// than that.
//
// Fewer pictures only help where the encoder does not follow the motion. It follows a page of text that
// scrolls by up to about 18 rows from one picture to the next, and such a picture is small; beyond that it
// codes the page almost from nothing, and then it hardly matters how far the page moved (measured at
// 1920 x 1080 and quantiser 34: 29 kbit for 6 rows, 128 for 18, 1667 for 30, 2299 for 54, 2658 for 90 and
// for 162; a key frame there takes about 2690). So a third of the pictures takes about half the bits where
// the encoder has lost the motion - and several times the bits where it still follows, because waiting is
// what makes it lose it. The caller is therefore only asked to wait while the pictures at its own pace cost
// at least half of what coding their changed part from nothing costs.
//
// Between those two there is motion the encoder loses at first and finds again: a steady scroll by 50 rows a
// picture costs 2429 kbit for the first pictures and 151 after ninety of them - if they keep coming at that
// distance. By 150 rows, which is what waiting makes of it, it never finds it. Waiting therefore does not go
// on for ever: when the bucket has filled up meanwhile, it is spent on the caller's own pace, and if the
// pictures get small there, nobody is asked to wait any more (see ScrollCostTests for the measurements).
internal sealed class RateController
{
    // Six steps of the scale halve the bits of a picture that follows the quantiser (every six quantisers
    // double the step size).
    internal const int Top = 70;
    private const int FinestMosaic = 46;

    // Half a second of the bit rate, as the engine this replaces had it.
    private const double BucketSeconds = 0.5;

    // The bucket fills a little more slowly than allowed, so that the rate stays below the ceiling rather than
    // around it.
    private const double Margin = 0.95;

    // A picture that stands still is worth a little more than the ceiling: from the second repeat on it may
    // borrow this many seconds of bit rate. The pictures after it pay it back.
    private const double OverdraftSeconds = 0.25;

    // How much of a bucket the debt may be before pictures that are larger than the inflow get coarser, and
    // from where on they get coarser quickly.
    private const double ToleratedDebt = 0.1;
    private const double DeepDebt = 0.5;

    // What a picture may overdraw the bucket by when it is expected to be large: a quarter of a bucket.
    private const double ExpectedDebt = 0.25;

    // The coarsest at which small text is still read without effort. Beyond it the caller is asked for fewer
    // pictures before the pictures get coarser.
    internal const int Readable = 34;

    // The longest the caller is asked to leave between two pictures: twenty a second still look like motion.
    internal const int LongestInterval = 50;

    // A picture that is waited for is expected to take this much less than what flows in meanwhile: what it
    // turns out larger by is no debt then, and a debt that is there gets paid.
    private const double IntervalMargin = 1.25;

    // From this part of what coding it from nothing costs, a change counts as one the encoder does not follow.
    private const double Unfollowed = 0.5;

    // How full the bucket has to be for the caller's own pace to be tried again while it is asked to wait.
    private const double PaceCredit = 0.8;

    // A key frame's size at each step of the scale, relative to the floor's 22: measured on screens of text
    // and of pictures. They shrink far less than the quantiser step grows.
    private static readonly (int Level, double Size)[] KeySizes =
    [
        (16, 1.25), (22, 1.0), (28, 0.78), (34, 0.61), (40, 0.42), (46, 0.19), (52, 0.10), (58, 0.05), (64, 0.025), (70, 0.012),
    ];

    private readonly double _bitsPerMillisecond;
    private readonly double _capacity;

    // The caller's own pace, and what flows in between two pictures at it.
    private readonly double _paceMilliseconds;
    private readonly double _paceInflow;
    private readonly int _floor;
    private double _bucket;
    private long _last = long.MinValue;

    // What a key frame of the picture would take at 22, in bits.
    private double _keyBits;

    // What a picture that changed everywhere takes at the floor, in bits - if it followed the quantiser.
    private double _changeBits;

    // What coding the whole picture from nothing takes at 22: what the last key frame said, or more where a
    // change has cost more since - the screen may hold more by now than it did then. Or less: every time the
    // screen comes to rest a part of it is forgotten.
    private double _scratchBits;

    // What the last change at the caller's own pace would have taken at 22 had it been the whole picture and
    // shrunk with the quantiser as a key frame does: close to _scratchBits where the encoder did not follow
    // the motion, a small part of it where it did.
    private double _paceCost;

    // Whether the caller was asked to wait for the picture that comes next, or for the one being coded.
    private bool _asked;
    private bool _waited;

    // Whether the bucket is being spent on the caller's own pace, where it would otherwise be asked to wait.
    private bool _atPace = true;

    // How coarse the picture the viewer has is, and how the last one was coded.
    private int _picture = Top;
    private int _level;
    private int _repeats;
    private int _floorRepeats;
    private PictureKind _kind = PictureKind.Key;
    private PictureKind _lastKind = PictureKind.Key;
    private double _share;
    private double _refine;
    private double _lastBits;

    public RateController(int width, int height, int framesPerSecond, int maxKilobitsPerSecond, int floor)
    {
        _bitsPerMillisecond = maxKilobitsPerSecond * Margin;
        _paceMilliseconds = 1000.0 / framesPerSecond;
        _paceInflow = _paceMilliseconds * _bitsPerMillisecond;
        _capacity = maxKilobitsPerSecond * 1000.0 * BucketSeconds;
        _bucket = _capacity;
        _floor = floor;
        _level = floor;

        // One bit a pixel: a screen with a fair amount of text on it. The first key frame says what it really is.
        _keyBits = (double)width * height;
        _changeBits = _keyBits / 8;
        _scratchBits = _keyBits;
    }

    // Whether the viewer's picture is as sharp as it gets: encoding the same picture again changes nothing.
    public bool IsSharp => _picture <= _floor;

    // How the next picture is to be coded. share is the part of the picture that changed (0..1), now the time
    // in milliseconds.
    public Coarseness Choose(PictureKind kind, double share, long now)
    {
        var inflow = 0.0;
        var flowed = 0.0;
        if (_last != long.MinValue)
        {
            // What flowed since the last picture, and what of it the bucket still held.
            flowed = Math.Max(0, now - _last) * _bitsPerMillisecond;
            inflow = Math.Min(flowed, _capacity - _bucket);
            _bucket += inflow;
        }

        _last = now;
        _kind = kind;
        _waited = _asked;
        _asked = false;
        if (kind != PictureKind.Changed)
        {
            // Whatever moves next starts at the caller's pace, on what the bucket holds.
            _atPace = true;
        }

        _share = Math.Clamp(share, 0.001, 1.0);
        switch (kind)
        {
            case PictureKind.Key:
                _repeats = 0;
                _level = Fit(_floor, Top, Math.Max(_bucket, inflow), key: true);
                break;

            case PictureKind.Repeat:
                // Nothing moves, so nothing has to be kept back for the next picture - and nothing is gained
                // by coding the picture more coarsely than the viewer already has it.
                _repeats++;
                var borrowed = _repeats >= 2 ? OverdraftSeconds * 1000 * _bitsPerMillisecond : 0;
                _share = 0;
                _level = Fit(_floor, Math.Max(_floor, _picture), Math.Max(0, _bucket) + borrowed, key: false);

                // Whatever moved before has stopped; what moves next need not be like it.
                _changeBits *= 0.5;
                if (_repeats == 1)
                {
                    // Nor need the screen hold as much as it did: what it was seen to cost at the most is
                    // forgotten slowly, and learnt again from the first picture that costs more.
                    _scratchBits *= 0.8;
                }

                break;

            default:
                _repeats = 0;
                _level = Changed(inflow, flowed);
                break;
        }

        _refine = RefineBits(_level);
        return Scale(_level);
    }

    // How many milliseconds the caller should let pass between the last picture and the next, if that part of
    // the picture (0..1) is new in it; 0 when the caller's own pace will do.
    //
    // Only while things move and the pictures are already as coarse as text takes: until then the bucket pays
    // for a burst at full pace, and the controller makes the pictures coarser as it always did. From then on
    // the wait is what lets enough flow in for a picture like the last ones at Readable. A small change is
    // expected to be small and never waits.
    //
    // The caller asks right before it decides about a picture: the answer given last is what the controller
    // takes the picture to have waited for.
    public int Interval(double share)
    {
        _asked = false;
        if (_lastKind != PictureKind.Changed || _level < Readable || !WorthWaitingFor(share))
        {
            return 0;
        }

        // Waiting is what keeps the encoder from finding the motion again, should it be one it can follow
        // after all. So whenever the bucket has filled up while waiting, it is spent on the caller's own
        // pace: either the pictures get small - then there is nothing to wait for any more - or the bucket is
        // empty after a moment and the waiting goes on.
        if (_bucket < 0)
        {
            _atPace = false;
        }
        else if (_bucket >= PaceCredit * _capacity)
        {
            _atPace = true;
        }

        if (_atPace)
        {
            return 0;
        }

        // The last picture was no finer than Readable, so one like it costs at least as much there - which
        // is nearer the truth than the estimate while that is still learning what this motion costs.
        var bits = ReadableBits(share);
        if (_share >= 0.02)
        {
            bits = Math.Max(bits, _lastBits * Math.Clamp(share, 0.001, 1.0) / _share);
        }

        var interval = Math.Min(LongestInterval, IntervalMargin * bits / _bitsPerMillisecond);
        _asked = interval > _paceMilliseconds;
        return _asked ? (int)Math.Ceiling(interval) : 0;
    }

    // The picture just chosen for did not fit into the library's output buffer and is coded once more from
    // nothing: two halvings coarser, as a key frame.
    public Coarseness Coarser()
    {
        _kind = PictureKind.Key;
        _repeats = 0;
        _refine = 0;
        _level = Step(Math.Min(Top, _level + 12));
        return Scale(_level);
    }

    // What the picture came to, in bits.
    public void Encoded(int bits)
    {
        // A debt of more than a bucket is forgiven: the bits are sent, and keeping the picture coarse for
        // seconds does not bring them back.
        _bucket = Math.Max(_bucket - bits, -_capacity);
        if (_kind != PictureKind.Repeat)
        {
            _floorRepeats = 0;
        }

        switch (_kind)
        {
            case PictureKind.Key:
                _keyBits = bits / KeySize(_level);
                _scratchBits = _keyBits;

                // Until a change says otherwise, a change of the whole picture is taken to cost a quarter of a
                // key frame: more than text takes, less than noise - which then overdraws the bucket once and
                // is made coarser with the pictures that follow.
                _changeBits = Math.Max(_changeBits, 0.25 * _keyBits);

                // A key frame is less sharp than the pictures that follow it at the same quantiser.
                _picture = Math.Min(Top, _level + 6);
                break;

            case PictureKind.Repeat:
                if (_level > _floor)
                {
                    _picture = Math.Min(_picture, _level);
                    break;
                }

                // Coded at the floor is not yet as sharp as the floor makes it: a picture refined from very
                // coarse, or in many small steps, still gains several dB from the same once more (measured:
                // 37.6 -> 41.4 dB after a jump from 46; 39.0 -> 39.7 -> 40.9 dB after steps from 44). While a
                // repeat at the floor still costs a noticeable part of a key frame there is something left to
                // fix; three of them are enough for anything (noise would otherwise never end).
                _floorRepeats++;
                _picture = bits < 0.03 * _keyBits * KeySize(_floor) || _floorRepeats >= 3 ? _floor : _floor + 1;
                break;

            default:
                if (_share >= 0.02)
                {
                    // A picture that also got sharper says little about what the change alone costs.
                    if (_refine < 0.3 * bits)
                    {
                        // Up halfway at once (it may have been a single unusual picture), down slowly.
                        var change = (bits - _refine) / (_share * Gain(_level));
                        _changeBits = change > _changeBits ? 0.5 * (_changeBits + change) : Math.Max(change, 0.7 * _changeBits);

                        // Whether the encoder follows the motion shows in the pictures the caller did not
                        // wait for: waiting itself makes them cost what coding from nothing does. A mosaic
                        // says nothing about it - there is so little left to code that following saves
                        // next to nothing.
                        if (_level <= FinestMosaic)
                        {
                            var scratch = (bits - _refine) / (_share * KeySize(_level));
                            _scratchBits = Math.Max(_scratchBits, scratch);
                            if (!_waited)
                            {
                                _paceCost = scratch;
                            }
                        }
                    }

                    _picture = _level;
                }
                else if (_level < _picture || _level > FinestMosaic)
                {
                    // A small change coded more coarsely than the rest leaves the picture as a whole what it
                    // was - unless it is a mosaic: that has to be made sharp again however small it is, so
                    // the picture counts as coarse until it has been.
                    _picture = _level;
                }

                break;
        }

        _lastKind = _kind;
        _lastBits = bits;
    }

    // A picture in which something moved. Where the last one was coded is where this one starts. inflow is
    // what the bucket took in since then, flowed what it would have taken had it not been full.
    private int Changed(double inflow, double flowed)
    {
        var level = Step(Math.Max(_floor, _level));
        var moving = _lastKind == PictureKind.Changed;
        if (moving && _lastBits > inflow && _bucket < -ToleratedDebt * _capacity)
        {
            // Larger than what flows in, and the debt is no longer small: coarser by as much as the picture
            // was too large - if it follows the quantiser. If it does not, the next picture says so.
            var deep = _bucket < -DeepDebt * _capacity;
            var steps = Math.Clamp((int)Math.Ceiling(6 * Math.Log2(_lastBits / Math.Max(inflow, 1))), 1, deep ? 12 : 6);
            var coarser = Step(Math.Min(Top, level + steps));

            // Not past Readable in one go: from there on the caller is asked to wait, and whether that is
            // enough shows with the next picture.
            level = level < Readable ? Math.Min(coarser, Readable) : coarser;
        }
        else if (level > _floor && _bucket >= 0 && (!moving || _lastBits <= 0.7 * flowed))
        {
            // Room to spare: one step finer, if the bucket pays for sharpening the whole picture by it. Room
            // is measured by what flowed, not by what the bucket took of it: a full bucket takes in no more
            // than the last picture took out, and small pictures would look as large as the inflow for
            // ever. Not finer than Readable while pictures like this one only fit there because the caller
            // waits for them: more pictures come before finer ones.
            var finer = level <= FinestMosaic ? level - 1 : level - 6;
            var budget = Math.Max(0.5 * _bucket, Math.Min(inflow, _bucket));
            var waiting = finer < Readable && WorthWaitingFor(_share);
            if (ChangeBits(finer) + RefineBits(finer) <= budget && !waiting)
            {
                level = finer;
            }
        }

        // A picture that is expected to be far larger than the bucket holds - the whole screen new on a slow
        // connection - is not sent at its finest first.
        return Fit(level, Top, Math.Max(_bucket, inflow) + ExpectedDebt * _capacity, key: false);
    }

    // The finest step of the scale from the first to the last at which the picture is expected to fit; the
    // last if it fits nowhere.
    private int Fit(int first, int last, double budget, bool key)
    {
        last = Step(last);
        for (var level = Step(first); level < last; level = Step(level + 1))
        {
            var bits = key ? _keyBits * KeySize(level) : ChangeBits(level) + RefineBits(level);
            if (bits <= budget)
            {
                return level;
            }
        }

        return last;
    }

    // Above the finest mosaic the scale only has every sixth step.
    private static int Step(int level) => level <= FinestMosaic ? level : Math.Min(Top, FinestMosaic + (level - FinestMosaic + 5) / 6 * 6);

    private static Coarseness Scale(int level) => level switch
    {
        <= FinestMosaic => new Coarseness(level, 1),
        <= 52 => new Coarseness(FinestMosaic, 4),
        <= 58 => new Coarseness(FinestMosaic, 8),
        <= 64 => new Coarseness(51, 8),
        _ => new Coarseness(51, 16),
    };

    // What the changed part of the picture is expected to cost at this step.
    private double ChangeBits(int level) => _changeBits * _share * Gain(level);

    // What a picture is expected to cost at Readable (or at the floor, where that is coarser) if this part
    // of it is new.
    private double ReadableBits(double share) => _changeBits * Math.Clamp(share, 0.001, 1.0) * Gain(Math.Max(_floor, Readable));

    // Whether a picture in which this part is new is one to wait for: at the caller's own pace it does not
    // fit at Readable, and the encoder has lost the motion, so that waiting costs no more than it brings.
    private bool WorthWaitingFor(double share) =>
        _paceCost >= Unfollowed * _scratchBits && IntervalMargin * ReadableBits(share) > _paceInflow;

    // What sharpening the viewer's picture to this step costs: about the difference between a key frame
    // there and a key frame as coarse as the picture is now, and a quarter more.
    private double RefineBits(int level) => 1.25 * _keyBits * Math.Max(0, KeySize(level) - KeySize(_picture));

    // What is left of the bits of a change at this step, for a picture that follows the quantiser.
    private double Gain(int level) => Math.Pow(2, (_floor - level) / 6.0);

    private static double KeySize(int level)
    {
        for (var i = 1; i < KeySizes.Length; i++)
        {
            if (level <= KeySizes[i].Level)
            {
                var (lowLevel, lowSize) = KeySizes[i - 1];
                var (highLevel, highSize) = KeySizes[i];
                return lowSize + (highSize - lowSize) * Math.Max(0, level - lowLevel) / (highLevel - lowLevel);
            }
        }

        return KeySizes[^1].Size;
    }
}
