using Matgate.Agent.Screen;
using Matgate.Display.Video;

namespace Matgate.Agent.Unix.Screen;

// H.264 through OpenH264 (Cisco's codec, BSD licence), as the distribution installs it - the agent brings
// no codec of its own. Set up for a remote screen: no B-frames, one picture in and one out, key frames only
// when the viewer asks for one.
internal sealed unsafe class OpenH264Encoder : IVideoEncoder
{
    private readonly Action<byte[], bool> _output;
    private readonly byte[] _u;
    private readonly byte[] _v;
    private FrameBitstream* _bitstream;
    private void* _encoder;
    private long _frames;
    private readonly int _framesPerSecond;

    public OpenH264Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output)
    {
        if (!OpenH264.IsAvailable)
        {
            throw new InvalidOperationException(OpenH264.UnavailableReason);
        }

        Width = width;
        Height = height;
        _output = output;
        _framesPerSecond = framesPerSecond;
        _u = new byte[(width / 2) * (height / 2)];
        _v = new byte[(width / 2) * (height / 2)];
        _encoder = OpenH264.CreateEncoder();
        _bitstream = (FrameBitstream*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(FrameBitstream));
        try
        {
            var quiet = OpenH264.LogQuiet;
            OpenH264.SetOption(_encoder, EncoderOption.TraceLevel, &quiet);
            EncoderParameters parameters;
            Check(OpenH264.GetDefaultParams(_encoder, &parameters), "read the defaults");
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            parameters.UsageType = UsageType.CameraVideoRealTime;
            parameters.PicWidth = width;
            parameters.PicHeight = height;
            parameters.TargetBitrate = bitrate;
            parameters.RCMode = RateControlMode.Bitrate;
            parameters.MaxFrameRate = framesPerSecond;
            parameters.TemporalLayerNum = 1;
            parameters.SpatialLayerNum = 1;
            parameters.ComplexityMode = ComplexityMode.Low;
            parameters.IntraPeriod = 0;
            parameters.NumRefFrame = 1;
            parameters.SpsPpsIdStrategy = ParameterSetStrategy.ConstantId;
            parameters.EntropyCodingModeFlag = 0;
            // Never a picture left out: the last one before the screen stands still must arrive.
            parameters.EnableFrameSkip = 0;
            parameters.MaxBitrate = 0;
            parameters.MaxQp = 42;
            parameters.MinQp = 14;
            parameters.EnableLongTermReference = 0;
            parameters.EnableDenoise = 0;
            parameters.EnableBackgroundDetection = 1;
            parameters.EnableAdaptiveQuant = 0;
            parameters.EnableSceneChangeDetect = 0;
            parameters.MultipleThreadIdc = (ushort)threads;
            parameters.UseLoadBalancing = 0;
            ref var layer = ref parameters.SpatialLayers[0];
            layer.VideoWidth = width;
            layer.VideoHeight = height;
            layer.FrameRate = framesPerSecond;
            layer.SpatialBitrate = bitrate;
            layer.MaxSpatialBitrate = 0;
            layer.SliceArgument.SliceMode = threads > 1 ? SliceMode.FixedCount : SliceMode.Single;
            layer.SliceArgument.SliceNum = (uint)threads;
            Check(OpenH264.InitializeExt(_encoder, &parameters), "start the encoder");
            var format = OpenH264.FormatI420;
            Check(OpenH264.SetOption(_encoder, EncoderOption.DataFormat, &format), "set the picture format");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width { get; }

    public int Height { get; }

    public string Name => "software: OpenH264 " + OpenH264.LibraryVersion;

    public bool Encode(byte[] nv12, bool keyFrame)
    {
        // NV12 keeps the two colour planes interleaved; OpenH264 wants each on its own.
        var luma = Width * Height;
        var chroma = _u.Length;
        fixed (byte* source = nv12, u = _u, v = _v)
        {
            var mixed = source + luma;
            for (var i = 0; i < chroma; i++)
            {
                u[i] = mixed[i * 2];
                v[i] = mixed[i * 2 + 1];
            }

            if (keyFrame)
            {
                Check(OpenH264.ForceIntraFrame(_encoder), "force a key frame");
            }

            SourcePicture picture = default;
            picture.ColorFormat = OpenH264.FormatI420;
            picture.Stride[0] = Width;
            picture.Stride[1] = Width / 2;
            picture.Stride[2] = Width / 2;
            picture.Data0 = source;
            picture.Data1 = u;
            picture.Data2 = v;
            picture.PicWidth = Width;
            picture.PicHeight = Height;
            picture.TimeStamp = _frames++ * 1000 / _framesPerSecond;
            Check(OpenH264.EncodeFrame(_encoder, &picture, _bitstream), "encode the picture");
        }

        if (_bitstream->FrameType is FrameType.Skip or FrameType.Invalid)
        {
            return true;
        }

        var total = 0;
        for (var i = 0; i < _bitstream->LayerNum; i++)
        {
            ref var layer = ref _bitstream->LayerInfo[i];
            for (var nal = 0; nal < layer.NalCount; nal++)
            {
                total += layer.NalLengthInByte[nal];
            }
        }

        if (total == 0)
        {
            return true;
        }

        var bytes = new byte[total];
        var written = 0;
        for (var i = 0; i < _bitstream->LayerNum; i++)
        {
            ref var layer = ref _bitstream->LayerInfo[i];
            var size = 0;
            for (var nal = 0; nal < layer.NalCount; nal++)
            {
                size += layer.NalLengthInByte[nal];
            }

            new ReadOnlySpan<byte>(layer.BsBuf, size).CopyTo(bytes.AsSpan(written));
            written += size;
        }

        _output(bytes, _bitstream->FrameType == FrameType.Idr);
        return true;
    }

    private static void Check(int result, string what)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"OpenH264 could not {what} ({result}).");
        }
    }

    public void Dispose()
    {
        if (_encoder is not null)
        {
            OpenH264.Uninitialize(_encoder);
            OpenH264.DestroyEncoder(_encoder);
            _encoder = null;
        }

        System.Runtime.InteropServices.NativeMemory.Free(_bitstream);
        _bitstream = null;
    }
}
