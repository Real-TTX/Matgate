using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Matgate.Display.Video;

// What an encoder is set up with beyond its size. The defaults are what measuring found best for a remote
// desktop; the tests pass something else to look at the alternatives.
internal sealed record H264EncoderOptions
{
    public UsageType Usage { get; init; } = UsageType.CameraVideoRealTime;

    // Off: how coarsely a picture is coded is chosen by Matgate's own RateController. Anything else hands the
    // bit rate to the library, between MinQp and MaxQp.
    public RateControlMode LibraryRateControl { get; init; } = RateControlMode.Off;

    // The finest quantiser: what a picture that stands still ends up with.
    public int MinQp { get; init; } = 22;

    public int MaxQp { get; init; } = 51;

    // Matgate's rate controller: how many seconds of the bit rate a burst may take, and what a still picture
    // may borrow beyond that (see RateController).
    public double BucketSeconds { get; init; } = RateController.DefaultBucketSeconds;

    public double OverdraftSeconds { get; init; } = RateController.DefaultOverdraftSeconds;

    // More than one: the picture is cut into that many slices, coded on that many threads. 0: one thread up
    // to the size of 1920 x 1080, four above it.
    public int Threads { get; init; }

    public ComplexityMode Complexity { get; init; } = ComplexityMode.Low;

    public bool Denoise { get; init; }

    public bool BackgroundDetection { get; init; }

    public bool SceneChangeDetection { get; init; }

    public bool LongTermReference { get; init; }

    public bool LosslessLink { get; init; }

    public bool FrameSkip { get; init; }

    public bool Cabac { get; init; }

    // Milliseconds that only ever grow. Null: the machine's clock.
    public Func<long>? Clock { get; init; }

    // For measuring: the quantiser of every picture by its number, in place of the rate controller's choice.
    public Func<int, int>? Quantisers { get; init; }
}

// Turns the pictures of a remote desktop into an H.264 stream for the browser's VideoDecoder: Annex B, one
// access unit for every picture, in the order the pictures come - nothing is held back, nothing is left out,
// and a key frame is only made for the first picture and when the caller asks for one.
//
// A picture that stands still is what matters most on a remote desktop, so it is coded as finely as the
// encoder goes; while the screen moves, the pictures are as coarse as they have to be to stay below the bit
// rate the caller allows. The caller makes a still picture sharp by encoding the same frame again - a frame
// nothing was written to since the last call - until IsSharp says there is nothing left to gain.
//
// Where even the coarsest quantiser is not enough, the picture is coded as a mosaic (see RateController) - but
// only where it changed. What the library is given then is a picture of the encoder's own: the frame, with
// the rectangle that changed averaged over blocks and everything else left exactly as the library saw it
// before. The library codes nothing for what it has seen already, so a toolbar or a second window stays as
// sharp as it was while a page next to it scrolls faster than the connection carries; and when the frame
// itself is encoded again, only the part that was a mosaic has to be made sharp.
//
// Before the pictures get hard to read, the encoder asks for fewer of them: SuggestedInterval says how long
// the caller should wait before a picture in which that much is new.
//
// One encoder is used by one thread at a time; any number of encoders work side by side.
public sealed unsafe class H264Encoder : IDisposable
{
    // The smallest side OpenH264 takes.
    public const int MinSize = 16;

    // The longest side H.264's highest level (5.2) allows: 543 macroblocks.
    public const int MaxSize = 8688;

    // The largest picture OpenH264 takes, counted in macroblocks of 16 x 16 pixels (sides rounded up):
    // 9,437,184 pixels, for example 4096 x 2304 - 3840 x 2160 fits, 5120 x 2160 does not.
    public const int MaxMacroblocks = 36864;

    private readonly int _framesPerSecond;
    private readonly int _kilobitsPerSecond;
    private readonly H264EncoderOptions _options;
    private readonly RateController? _rate;
    private readonly Func<long> _clock;
    private void* _encoder;
    private FrameBitstream* _output;

    // What the library is given in place of the frame while pictures are coded as a mosaic, and whether the
    // picture coded last was this one. If it was the frame itself, this one is out of date.
    private I420Frame? _shown;
    private bool _shownIsLast;

    // For every macroblock of _shown, the size of the blocks it is averaged over (0: it is the frame's), and
    // how many of them are averaged at all.
    private byte[]? _blocks;
    private int _mosaicMacroblocks;
    private byte[] _buffer = new byte[64 * 1024];
    private long _frames;
    private int _quantiser;
    private I420Frame? _lastFrame;
    private long _lastVersion;
    private long _lastChangedPixels;

    public H264Encoder(int width, int height, int framesPerSecond, int maxKilobitsPerSecond)
        : this(width, height, framesPerSecond, maxKilobitsPerSecond, new H264EncoderOptions())
    {
    }

    internal H264Encoder(int width, int height, int framesPerSecond, int maxKilobitsPerSecond, H264EncoderOptions options)
    {
        if (!Supports(width, height))
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"{width} x {height} cannot be encoded: both sides are even, between {MinSize} and {MaxSize}, and the picture has at most {MaxMacroblocks} macroblocks of 16 x 16 pixels.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKilobitsPerSecond, 1);
        Width = width;
        Height = height;
        _framesPerSecond = framesPerSecond;
        _kilobitsPerSecond = maxKilobitsPerSecond;
        _options = options;
        if (options.Clock is { } clock)
        {
            _clock = clock;
        }
        else
        {
            var watch = Stopwatch.StartNew();
            _clock = () => watch.ElapsedMilliseconds;
        }

        _quantiser = options.MinQp;
        if (options.LibraryRateControl == RateControlMode.Off)
        {
            _rate = new RateController(width, height, framesPerSecond, maxKilobitsPerSecond, options.MinQp, options.BucketSeconds, options.OverdraftSeconds);
        }

        _encoder = OpenH264.CreateEncoder();
        try
        {
            _output = (FrameBitstream*)NativeMemory.AllocZeroed((nuint)sizeof(FrameBitstream));
            Initialize();
        }
        catch
        {
            Release();
            throw;
        }
    }

    ~H264Encoder()
    {
        Release();
    }

    // False when libopenh264 cannot be used on this machine; UnavailableReason says why, for the log.
    public static bool IsAvailable => OpenH264.IsAvailable;

    public static string? UnavailableReason => OpenH264.UnavailableReason;

    // "2.4.1"; null when no library was found at all.
    public static string? LibraryVersion => OpenH264.LibraryVersion;

    public int Width { get; }

    public int Height { get; }

    // True when the picture the viewer has is as sharp as this encoder makes it: encoding the same frame
    // once more would change nothing. After a change or a key frame it is false until the picture has been
    // encoded again often enough - once or twice where the bit rate is generous, more often where it is not.
    // Never while any part of what the viewer has is a mosaic.
    public bool IsSharp => (_rate?.IsSharp ?? true) && _mosaicMacroblocks == 0;

    // The quantiser and the kind of the picture encoded last - for tests and statistics.
    internal int LastQuantiser { get; private set; }

    internal int LastBlock { get; private set; }

    internal FrameType LastFrameType { get; private set; }

    // For tests: what the library was given for the picture encoded last where that was not the frame itself,
    // the rectangle of it that was averaged anew for that picture (empty: none), and how many macroblocks of
    // what the viewer has are a mosaic.
    internal I420Frame? LastShown => _shownIsLast ? _shown : null;

    internal (int Left, int Top, int Right, int Bottom) LastMosaic { get; private set; }

    internal int MosaicMacroblocks => _mosaicMacroblocks;

    // Whether a picture of this size can be encoded at all. A caller with a larger desktop has to scale it.
    public static bool Supports(int width, int height) =>
        width >= MinSize && height >= MinSize && width <= MaxSize && height <= MaxSize
        && (width & 1) == 0 && (height & 1) == 0
        && Macroblocks(width, height) <= MaxMacroblocks;

    // How many milliseconds the caller should let pass between the picture encoded last and the next one, if
    // in that one this many pixels are new; 0 when the caller's own pace will do. While the screen changes
    // faster than the connection carries, fewer pictures that can be read are better than many that cannot:
    // the wait is never longer than twenty pictures a second take, and a small change - a cursor, a letter
    // typed - is never asked to wait. It is advice for pictures in which something changed; a key frame and
    // the repeats that make a still picture sharp keep the caller's timing.
    public int SuggestedInterval(long changedPixels)
    {
        ObjectDisposedException.ThrowIf(_encoder == null, this);
        return _rate?.Interval(Math.Clamp((double)changedPixels / ((long)Width * Height), 0.0, 1.0)) ?? 0;
    }

    // One picture in, one access unit out: Annex B with start codes, and SPS and PPS in front of every key
    // frame. The memory is valid until the next call. The encoder has copied the picture when this returns,
    // so the frame can be written to again right away.
    public ReadOnlyMemory<byte> Encode(I420Frame frame, bool forceKey, out bool isKey)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_encoder == null, this);
        ObjectDisposedException.ThrowIf(frame.IsDisposed, frame);
        if (frame.Width != Width || frame.Height != Height)
        {
            throw new ArgumentException($"The picture is {frame.Width} x {frame.Height}, the encoder was made for {Width} x {Height}.", nameof(frame));
        }

        var known = ReferenceEquals(frame, _lastFrame);
        var unchanged = known && frame.Version == _lastVersion;
        var share = known ? Math.Min(1.0, (double)(frame.ChangedPixels - _lastChangedPixels) / ((long)Width * Height)) : 1.0;

        // Where the frame differs from what this encoder saw of it last; all of a frame it has not seen.
        var changed = new Area(0, 0, 0, 0);
        if (frame.TakeChanged(known ? _lastVersion : -1, out var left, out var top, out var right, out var bottom))
        {
            changed = new Area(left, top, right, bottom);
        }

        _lastFrame = frame;
        _lastVersion = frame.Version;
        _lastChangedPixels = frame.ChangedPixels;

        // The first picture is a key frame without being asked.
        var kind = forceKey || _frames == 0 ? PictureKind.Key : unchanged ? PictureKind.Repeat : PictureKind.Changed;
        if (forceKey && _frames > 0)
        {
            Check(OpenH264.ForceIntraFrame(_encoder), "force a key frame");
        }

        // A key frame codes every part of the picture anew, and its size is reckoned for a picture that is
        // coarse everywhere.
        var whole = new Area(0, 0, Width, Height);
        if (kind == PictureKind.Key)
        {
            changed = whole;
        }

        var coarseness = new Coarseness(_quantiser, 1);
        if (_options.Quantisers is { } quantisers)
        {
            coarseness = new Coarseness(quantisers((int)_frames), 1);
        }
        else if (_rate is not null)
        {
            coarseness = _rate.Choose(kind, share, _clock());
        }

        if (_rate is not null)
        {
            SetQuantiser(coarseness.Quantiser);
        }

        var result = EncodeFrame(frame, coarseness.Block, changed);
        if (result == OpenH264.ResultOutOfMemory)
        {
            // A picture that would come out larger than it is raw does not fit into the library's output
            // buffer; the library then drops everything it has. It is set up again and the picture encoded
            // once more, much more coarsely - as a key frame, since nothing of the stream is left to build on.
            coarseness = _rate?.Coarser() ?? new Coarseness(Math.Min(51, _quantiser + 12), 1);
            _quantiser = coarseness.Quantiser;
            Initialize();
            Check(EncodeFrame(frame, coarseness.Block, whole), "encode the picture");
        }
        else
        {
            Check(result, "encode the picture");
        }

        _frames++;
        var length = Collect();
        LastFrameType = _output->FrameType;
        LastQuantiser = _rate is not null ? _quantiser : (int)Statistics().AverageFrameQP;
        LastBlock = coarseness.Block;
        isKey = _output->FrameType == FrameType.Idr;
        if (length == 0 && !_options.FrameSkip)
        {
            throw new InvalidOperationException($"OpenH264 left a picture out ({_output->FrameType}).");
        }

        _rate?.Encoded(length * 8);
        return new ReadOnlyMemory<byte>(_buffer, 0, length);
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private static int Macroblocks(int width, int height) => ((width + 15) / 16) * ((height + 15) / 16);

    private void Initialize()
    {
        // Its messages would go to the process's stderr; the setting belongs to this encoder alone.
        var quiet = OpenH264.LogQuiet;
        Check(OpenH264.SetOption(_encoder, EncoderOption.TraceLevel, &quiet), "turn the library's log off");

        EncoderParameters parameters;
        Check(OpenH264.GetDefaultParams(_encoder, &parameters), "read the default parameters");
        var bitrate = (int)Math.Min(_kilobitsPerSecond * 1000L, 240_000_000);
        parameters.UsageType = _options.Usage;
        parameters.PicWidth = Width;
        parameters.PicHeight = Height;
        parameters.TargetBitrate = bitrate;
        parameters.RCMode = _options.LibraryRateControl;

        // Only the level in the stream's header depends on it (and the library's own rate control, where
        // that is used). The library knows nothing above 60, and for the largest pictures the highest level
        // it can write (5.2: 2,073,600 macroblocks a second) is only reached with a smaller number.
        var rate = Math.Clamp(_framesPerSecond, 1, 60);
        rate = Math.Min(rate, Math.Max(1, 2_073_600 / Macroblocks(Width, Height)));
        parameters.MaxFrameRate = rate;
        parameters.TemporalLayerNum = 1;
        parameters.SpatialLayerNum = 1;
        parameters.ComplexityMode = _options.Complexity;

        // No key frame by the calendar: the browser asks for one when it needs one.
        parameters.IntraPeriod = 0;
        parameters.NumRefFrame = 1;

        // The same SPS and PPS in front of every key frame, so the decoder's configuration never changes.
        parameters.SpsPpsIdStrategy = ParameterSetStrategy.ConstantId;
        parameters.EntropyCodingModeFlag = _options.Cabac ? 1 : 0;
        parameters.EnableFrameSkip = (byte)(_options.FrameSkip ? 1 : 0);
        parameters.MaxBitrate = 0;
        parameters.MaxQp = _options.MaxQp;
        parameters.MinQp = _options.MinQp;
        parameters.EnableLongTermReference = (byte)(_options.LongTermReference ? 1 : 0);
        parameters.IsLosslessLink = (byte)(_options.LosslessLink ? 1 : 0);
        parameters.EnableDenoise = (byte)(_options.Denoise ? 1 : 0);
        parameters.EnableBackgroundDetection = (byte)(_options.BackgroundDetection ? 1 : 0);
        parameters.EnableAdaptiveQuant = 0;
        parameters.EnableSceneChangeDetect = (byte)(_options.SceneChangeDetection ? 1 : 0);

        // A large picture is cut into four slices coded side by side: at 3840 x 2160 one thread needs 14 ms
        // for scrolling text and 40 ms for noise, four need half of that. Slices cost a few per cent of bits
        // and leave the slice edges unfiltered, and the library's worker threads are one pool for the whole
        // process (four threads, shared by every encoder that uses slices) - so pictures up to the size of
        // 1920 x 1080, which one thread codes in a few milliseconds, stay in one slice on the caller's thread.
        var threads = _options.Threads > 0 ? Math.Min(_options.Threads, 4) : Macroblocks(Width, Height) > 8160 ? 4 : 1;
        parameters.MultipleThreadIdc = (ushort)threads;

        // Slices of fixed size, so that the same pictures always give the same stream.
        parameters.UseLoadBalancing = 0;

        ref var layer = ref parameters.SpatialLayers[0];
        layer.VideoWidth = Width;
        layer.VideoHeight = Height;
        layer.FrameRate = rate;
        layer.SpatialBitrate = bitrate;
        layer.MaxSpatialBitrate = 0;
        layer.DLayerQp = _quantiser;
        layer.SliceArgument.SliceMode = threads > 1 ? SliceMode.FixedCount : SliceMode.Single;
        layer.SliceArgument.SliceNum = (uint)threads;

        // What ColorConverter does, written into the header: BT.601 matrix, limited range, the primaries and
        // the curve of an sRGB screen. Without it a browser guesses by the size of the picture.
        layer.VideoSignalTypePresent = 1;
        layer.VideoFormat = OpenH264.VideoFormatUndefined;
        layer.FullRange = 0;
        layer.ColorDescriptionPresent = 1;
        layer.ColorPrimaries = OpenH264.PrimariesBt709;
        layer.TransferCharacteristics = OpenH264.TransferSrgb;
        layer.ColorMatrix = OpenH264.MatrixBt601;

        Check(OpenH264.InitializeExt(_encoder, &parameters), "set up the encoder");
        var format = OpenH264.FormatI420;
        Check(OpenH264.SetOption(_encoder, EncoderOption.DataFormat, &format), "set the picture format");
    }

    private int EncodeFrame(I420Frame frame, int block, Area changed)
    {
        var input = frame;
        if (block > 1)
        {
            input = Coarse(frame, block, changed);
        }
        else
        {
            // The frame itself: whatever was a mosaic is coded from the real picture now.
            _shownIsLast = false;
            _mosaicMacroblocks = 0;
            LastMosaic = default;
        }

        SourcePicture picture = default;
        picture.ColorFormat = OpenH264.FormatI420;
        picture.Stride[0] = input.StrideY;
        picture.Stride[1] = input.StrideU;
        picture.Stride[2] = input.StrideV;
        picture.Data0 = input.PointerY;
        picture.Data1 = input.PointerU;
        picture.Data2 = input.PointerV;
        picture.PicWidth = Width;
        picture.PicHeight = Height;
        picture.TimeStamp = _frames * 1000 / _framesPerSecond;
        return OpenH264.EncodeFrame(_encoder, &picture, _output);
    }

    // The picture the library is given when the frame is to be coded as a mosaic of blocks of this size: the
    // picture it was given last, with the rectangle that changed since averaged over such blocks. Nothing
    // else is touched - with one exception: what is still averaged over larger blocks from an earlier picture
    // is averaged over these now, so that nothing the viewer has is coarser than the rate controller believes
    // (it reckons the whole picture as coarse as the last one was coded).
    private I420Frame Coarse(I420Frame frame, int block, Area changed)
    {
        var columns = (Width + Mosaic.Grid - 1) / Mosaic.Grid;
        var rows = (Height + Mosaic.Grid - 1) / Mosaic.Grid;
        _shown ??= new I420Frame(Width, Height);
        _blocks ??= new byte[columns * rows];
        if (!_shownIsLast)
        {
            // The library saw the frame itself last. Outside of what changed since, the frame still is that
            // picture, and inside it does not matter.
            _shown.CopyFrom(frame);
            _blocks.AsSpan().Clear();
            _mosaicMacroblocks = 0;
            _shownIsLast = true;
        }

        // What changed, widened to whole macroblocks: the library codes by them, and they are a grid of the
        // blocks of every size, so a block is cut the same way in every picture.
        var firstColumn = Math.Clamp(changed.Left / Mosaic.Grid, 0, columns);
        var firstRow = Math.Clamp(changed.Top / Mosaic.Grid, 0, rows);
        var lastColumn = Math.Clamp((changed.Right + Mosaic.Grid - 1) / Mosaic.Grid, firstColumn, columns);
        var lastRow = Math.Clamp((changed.Bottom + Mosaic.Grid - 1) / Mosaic.Grid, firstRow, rows);
        LastMosaic = default;
        if (firstColumn < lastColumn && firstRow < lastRow)
        {
            Render(frame, block, firstColumn, firstRow, lastColumn, lastRow);
            LastMosaic = (firstColumn * Mosaic.Grid, firstRow * Mosaic.Grid, Math.Min(Width, lastColumn * Mosaic.Grid), Math.Min(Height, lastRow * Mosaic.Grid));
        }

        // The frame has not changed there since those macroblocks were averaged, so it is still what they
        // show.
        for (var row = 0; row < rows && _mosaicMacroblocks > 0; row++)
        {
            var line = _blocks.AsSpan(row * columns, columns);
            for (var column = 0; column < columns; column++)
            {
                if (line[column] <= block)
                {
                    continue;
                }

                var end = column + 1;
                while (end < columns && line[end] > block)
                {
                    end++;
                }

                Render(frame, block, column, row, end, row + 1);
                column = end;
            }
        }

        return _shown;
    }

    // Averages these macroblocks of the frame into _shown and notes it.
    private void Render(I420Frame frame, int block, int firstColumn, int firstRow, int lastColumn, int lastRow)
    {
        Mosaic.Render(
            frame, _shown!, block,
            firstColumn * Mosaic.Grid, firstRow * Mosaic.Grid, Math.Min(Width, lastColumn * Mosaic.Grid), Math.Min(Height, lastRow * Mosaic.Grid));
        var columns = (Width + Mosaic.Grid - 1) / Mosaic.Grid;
        for (var row = firstRow; row < lastRow; row++)
        {
            var line = _blocks.AsSpan(row * columns + firstColumn, lastColumn - firstColumn);
            _mosaicMacroblocks += line.Count((byte)0);
            line.Fill((byte)block);
        }
    }

    // With the library's rate control off, every picture is coded with the layer's quantiser. Changing it
    // goes through the full set of parameters - read back from the encoder, so that nothing else differs and
    // the library has no reason to start over with a key frame.
    private void SetQuantiser(int quantiser)
    {
        if (quantiser == _quantiser)
        {
            return;
        }

        EncoderParameters parameters;
        Check(OpenH264.GetOption(_encoder, EncoderOption.ParametersExt, &parameters), "read the parameters");
        parameters.SpatialLayers[0].DLayerQp = quantiser;
        Check(OpenH264.SetOption(_encoder, EncoderOption.ParametersExt, &parameters), "change the quantiser");
        _quantiser = quantiser;
    }

    private EncoderStatistics Statistics()
    {
        EncoderStatistics statistics = default;
        OpenH264.GetOption(_encoder, EncoderOption.GetStatistics, &statistics);
        return statistics;
    }

    // The NAL units of all layers, one after another: for a key frame the parameter sets are a layer of
    // their own in front of the picture.
    private int Collect()
    {
        var total = 0;
        for (var i = 0; i < _output->LayerNum; i++)
        {
            ref var layer = ref _output->LayerInfo[i];
            for (var nal = 0; nal < layer.NalCount; nal++)
            {
                total += layer.NalLengthInByte[nal];
            }
        }

        if (_buffer.Length < total)
        {
            _buffer = GC.AllocateUninitializedArray<byte>(Math.Max(total, _buffer.Length * 2));
        }

        var written = 0;
        for (var i = 0; i < _output->LayerNum; i++)
        {
            ref var layer = ref _output->LayerInfo[i];
            var size = 0;
            for (var nal = 0; nal < layer.NalCount; nal++)
            {
                size += layer.NalLengthInByte[nal];
            }

            new ReadOnlySpan<byte>(layer.BsBuf, size).CopyTo(_buffer.AsSpan(written));
            written += size;
        }

        return total;
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"OpenH264 could not {what} (error {result}).");
        }
    }

    private void Release()
    {
        if (_encoder != null)
        {
            OpenH264.Uninitialize(_encoder);
            OpenH264.DestroyEncoder(_encoder);
            _encoder = null;
        }

        if (_output != null)
        {
            NativeMemory.Free(_output);
            _output = null;
        }

        _shown?.Dispose();
        _shown = null;
        _shownIsLast = false;
        _mosaicMacroblocks = 0;
    }

    // A rectangle of the picture; nothing when Right is not beyond Left.
    private readonly record struct Area(int Left, int Top, int Right, int Bottom);
}
