namespace Matgate.Display.Video;

// What a level of "Picture quality" in a session's Display dialog means for Matgate's own engine (RDP and VNC):
// how many pictures a second and how many bits for them, how many pictures may be on their way to the browser,
// how large a burst the connection may take at once, how fine a still picture gets - and what the remote
// desktop is asked for when the session connects. The three levels the dialog always had keep their numbers
// exactly; "fast" gives up colours and sharpness for time.
//
// It lives with the video part because that is what most of it steers, and so that the tests of that part
// (tests/unit/Video.Tests) see it.
public sealed record ScreenQuality
{
    // What the page sends: "auto", "medium", "low" or "fast".
    public required string Name { get; init; }

    public int FramesPerSecond { get; init; } = 60;

    // The ceiling in kbit/s for a picture of 1920 x 1080 (see KilobitsPerSecond).
    public int BitratePerFullHd { get; init; } = 8000;

    // How many pictures may be on their way before the next one waits for the browser to show one.
    public int MaxInFlight { get; init; } = 3;

    // The rate controller's bucket, and what a still picture may borrow beyond it (see RateController).
    public double BucketSeconds { get; init; } = RateController.DefaultBucketSeconds;

    public double OverdraftSeconds { get; init; } = RateController.DefaultOverdraftSeconds;

    // The finest quantiser: where a picture that stands still ends up.
    public int MinQp { get; init; } = 22;

    // Whether the encoder may ask for fewer pictures where more changes than the connection carries
    // (H264Encoder.SuggestedInterval). Without it every picture goes out at the frame rate, as coarse as it
    // has to be.
    public bool Pace { get; init; } = true;

    // RDP, asked for when the session connects: bits a pixel, and what the desktop may spend on looks (the
    // performance flags of the Client Info: 0x180 is font smoothing and desktop composition on).
    public int RdpColorDepth { get; init; } = 32;

    public uint RdpPerformanceFlags { get; init; } = 0x180;

    // "Smooth (60 fps)": what a session has unless the viewer chose something else.
    public static readonly ScreenQuality Smooth = new() { Name = "auto" };

    public static readonly ScreenQuality Medium = new() { Name = "medium", FramesPerSecond = 30, BitratePerFullHd = 4000 };

    public static readonly ScreenQuality LowData = new() { Name = "low", FramesPerSecond = 20, BitratePerFullHd = 1500 };

    // "Fastest response (fewer colours)": every picture as soon as it can be had. Sixty a second, but less to
    // send of each - a lower ceiling, a still picture a little less fine, sound off unless asked for (the
    // page) - and no picture waits for the network: two at most are on their way, a burst may take the
    // connection for a sixth of a second instead of half of one, and the encoder never asks for fewer of
    // them. The remote desktop sends 16 bits a pixel and leaves out what only costs pictures: wallpaper,
    // window contents while dragging, menu animations, visual styles, the cursor's shadow and its blinking,
    // smoothed fonts.
    public static readonly ScreenQuality Fastest = new()
    {
        Name = "fast",
        BitratePerFullHd = 3000,
        MaxInFlight = 2,
        BucketSeconds = 0.15,
        OverdraftSeconds = 0.05,
        MinQp = 26,
        Pace = false,
        RdpColorDepth = 16,
        RdpPerformanceFlags = 0x6F,
    };

    // The level of a value the page sent; anything unknown is "auto", as it always was.
    public static ScreenQuality From(string? name) => name switch
    {
        "medium" => Medium,
        "low" => LowData,
        "fast" => Fastest,
        _ => Smooth,
    };

    // The encoder's ceiling for a picture of this size: what flows for 1920 x 1080, by the picture's share of
    // that and by the frame rate (one and a half times it at 60 pictures a second, once at 30, five sixths at
    // 20) - never below half a megabit.
    public int KilobitsPerSecond(int width, int height) =>
        Math.Max(500, (int)((long)BitratePerFullHd * width * height / (1920 * 1080) * (15 + FramesPerSecond / 2) / 30));

    // Whether an encoder made for this level does for that one as well: the frame rate, the ceiling and the
    // rate controller are the encoder's; how many pictures are on their way and whether they wait are not.
    public bool SameEncoder(ScreenQuality other) =>
        FramesPerSecond == other.FramesPerSecond && BitratePerFullHd == other.BitratePerFullHd && MinQp == other.MinQp
        && BucketSeconds.Equals(other.BucketSeconds) && OverdraftSeconds.Equals(other.OverdraftSeconds);

    // What the encoder is set up with beyond its size and rate.
    internal H264EncoderOptions EncoderOptions() => new()
    {
        MinQp = MinQp,
        BucketSeconds = BucketSeconds,
        OverdraftSeconds = OverdraftSeconds,
    };
}
