using System.Diagnostics;

namespace Matgate.Display.Video.Tests;

// Block is the size of the blocks the picture was averaged over before it was coded (1: not at all).
internal sealed record EncodedPicture(int Index, int Bytes, bool IsKey, int Quantiser, int Block, bool Sharp, List<int> NalTypes, double Milliseconds, double Time);

// An encoder at work on a made-up screen, with everything kept that a test wants to look at afterwards: the
// stream, the pictures that went in, and what came out for each of them. Time is the recording's own - a
// picture every 1/fps seconds unless the test says otherwise - so a run takes as long as the machine needs
// and still means the same.
internal sealed unsafe class Recording : IDisposable
{
    private readonly string _folder;
    private readonly FileStream _stream;
    private readonly FileStream _reference;
    private double _now;

    public Recording(int width, int height, int framesPerSecond, int kilobitsPerSecond, H264EncoderOptions? options = null)
    {
        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;
        KilobitsPerSecond = kilobitsPerSecond;
        _folder = Path.Combine(Path.GetTempPath(), "matgate-video-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        StreamPath = Path.Combine(_folder, "stream.h264");
        ReferencePath = Path.Combine(_folder, "reference.yuv");
        // Read by ffmpeg and by the comparison while the recording still holds them.
        _stream = new FileStream(StreamPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _reference = new FileStream(ReferencePath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        Frame = new I420Frame(width, height);
        Source = new byte[width * height * 4];
        Encoder = new H264Encoder(width, height, framesPerSecond, kilobitsPerSecond, (options ?? new H264EncoderOptions()) with { Clock = () => (long)_now });
    }

    public int Width { get; }

    public int Height { get; }

    public int FramesPerSecond { get; }

    public int KilobitsPerSecond { get; }

    public H264Encoder Encoder { get; }

    public I420Frame Frame { get; }

    // The screen as the remote desktop has it: BGRX, a row is Width * 4 bytes. A test draws into it and then
    // calls Show for the part it drew.
    public byte[] Source { get; }

    public string StreamPath { get; }

    public string ReferencePath { get; }

    public List<EncodedPicture> Pictures { get; } = [];

    // Leave the reference pictures out (a long run at a large size would fill the disk); DecodeAndCompare
    // cannot be used then.
    public bool KeepReference { get; init; } = true;

    public double ConvertMilliseconds { get; private set; }

    // The recording's clock, in milliseconds: when the picture encoded last was encoded.
    public double Now => _now;

    // How long the session would wait before it encodes a picture in which this many pixels are new: its own
    // pace, or longer where the encoder asks for it.
    public double Interval(long changedPixels) =>
        Math.Max(1000.0 / FramesPerSecond, Encoder.SuggestedInterval(changedPixels));

    // Converts the given part of Source into the frame - the whole screen without arguments.
    public void Show(int x = 0, int y = 0, int width = int.MaxValue, int height = int.MaxValue)
    {
        var started = Stopwatch.GetTimestamp();
        ColorConverter.BgrxToI420(Source, Width * 4, Frame, x, y, Math.Min(width, Width - x), Math.Min(height, Height - y));
        ConvertMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // Encodes the frame as it is now. after: milliseconds since the picture before (default: 1000 / fps).
    public EncodedPicture Encode(bool forceKey = false, double? after = null)
    {
        if (Pictures.Count > 0)
        {
            _now += after ?? 1000.0 / FramesPerSecond;
        }

        var started = Stopwatch.GetTimestamp();
        var unit = Encoder.Encode(Frame, forceKey, out var isKey);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _stream.Write(unit.Span);
        if (KeepReference)
        {
            WritePlane(Frame.Y, Frame.StrideY, Width, Height);
            WritePlane(Frame.U, Frame.StrideU, Width / 2, Height / 2);
            WritePlane(Frame.V, Frame.StrideV, Width / 2, Height / 2);
        }

        var picture = new EncodedPicture(Pictures.Count, unit.Length, isKey, Encoder.LastQuantiser, Encoder.LastBlock, Encoder.IsSharp, AnnexB.NalTypes(unit.Span), elapsed, _now);
        Pictures.Add(picture);
        return picture;
    }

    // Decodes the stream with ffmpeg and compares one rectangle of every picture with what went in: the luma
    // PSNR in dB of that part alone (99 where it came back exactly).
    public List<double> DecodeAndCompare(int left, int top, int width, int height)
    {
        _stream.Flush();
        _reference.Flush();
        var decodedPath = Path.Combine(_folder, "decoded.yuv");
        Ffmpeg.Decode(StreamPath, decodedPath);
        return Compare(ReferencePath, decodedPath, Width, Height, left, top, width, height);
    }

    // The highest bit rate (kbit/s) of any stretch of the given length, among the pictures from first on.
    public double PeakKilobitsPerSecond(int first = 0, double windowMilliseconds = 1000)
    {
        var peak = 0.0;
        for (var start = first; start < Pictures.Count; start++)
        {
            long bytes = 0;
            for (var i = start; i < Pictures.Count && Pictures[i].Time - Pictures[start].Time < windowMilliseconds; i++)
            {
                bytes += Pictures[i].Bytes;
            }

            peak = Math.Max(peak, bytes * 8 / windowMilliseconds);
        }

        return peak;
    }

    public double AverageKilobitsPerSecond(int first, int count)
    {
        long bytes = 0;
        for (var i = first; i < first + count; i++)
        {
            bytes += Pictures[i].Bytes;
        }

        return bytes * 8.0 * FramesPerSecond / count / 1000;
    }

    // Decodes the stream with ffmpeg and compares every picture with what went in: the luma PSNR in dB
    // (99 for a picture that came back exactly).
    public List<double> DecodeAndCompare()
    {
        _stream.Flush();
        _reference.Flush();
        var decodedPath = Path.Combine(_folder, "decoded.yuv");
        Ffmpeg.Decode(StreamPath, decodedPath);
        return Compare(ReferencePath, decodedPath, Width, Height);
    }

    public Dictionary<string, string> Probe()
    {
        _stream.Flush();
        return Ffmpeg.Probe(StreamPath);
    }

    public static List<double> Compare(string referencePath, string decodedPath, int width, int height) =>
        Compare(referencePath, decodedPath, width, height, 0, 0, width, height);

    public static List<double> Compare(string referencePath, string decodedPath, int width, int height, int left, int top, int partWidth, int partHeight)
    {
        var luma = width * height;
        var size = luma * 3 / 2;
        var result = new List<double>();
        using var reference = File.OpenRead(referencePath);
        using var decoded = File.OpenRead(decodedPath);
        if (decoded.Length % size != 0)
        {
            throw new InvalidOperationException($"The decoded pictures are not {width} x {height}: {decoded.Length} bytes is not a multiple of {size}.");
        }

        var expected = new byte[size];
        var actual = new byte[size];
        while (decoded.Position < decoded.Length)
        {
            reference.ReadExactly(expected);
            decoded.ReadExactly(actual);
            long squares = 0;
            for (var y = top; y < top + partHeight; y++)
            {
                for (var i = y * width + left; i < y * width + left + partWidth; i++)
                {
                    var difference = expected[i] - actual[i];
                    squares += difference * difference;
                }
            }

            result.Add(squares == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 * partWidth * partHeight / squares));
        }

        return result;
    }

    public void Dispose()
    {
        Encoder.Dispose();
        Frame.Dispose();
        _stream.Dispose();
        _reference.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WritePlane(Span<byte> plane, int stride, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            _reference.Write(plane.Slice(y * stride, width));
        }
    }
}
