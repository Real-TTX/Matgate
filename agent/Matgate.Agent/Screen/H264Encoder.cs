using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Matgate.Agent.Screen;

// Windows' own H.264 encoder (Media Foundation), set up for a remote screen rather than for a film:
// baseline profile (no B-frames, nothing waits for a later picture), low-latency mode (one picture in, one
// out), constant bit rate, and key frames only when the viewer asks for one. The output is an Annex B
// stream - what the browser's VideoDecoder takes as it is.
internal sealed unsafe class H264Encoder : IDisposable
{
    private static readonly Guid ClsidMsH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid IidTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IidCodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid Primaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    // ICodecAPI properties.
    private static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int ProvidesSamples = 0x100;

    private static int _started;

    private readonly IMFTransform _transform;
    private readonly nint _codecApi;
    private readonly long _duration;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private byte[]? _sequenceHeader;
    private long _time;

    public H264Encoder(int width, int height, int framesPerSecond, int bitrate)
    {
        Width = width;
        Height = height;
        _duration = 10_000_000 / framesPerSecond;
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            MediaFactory.MFStartup(true).CheckError();
        }

        _ = CoInitializeEx(0, 0);
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidMsH264Encoder, 0, 1, IidTransform, out var pointer));
        _transform = new IMFTransform(pointer);
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(pointer, IidCodecApi, out _codecApi));

        // Set before the media types: the encoder fixes some of it when the output type is set.
        SetValue(RateControlMode, 0u);
        SetValue(MeanBitRate, (uint)bitrate);
        SetValue(BPictureCount, 0u);
        SetValue(GopSize, (uint)(framesPerSecond * 600));
        SetValue(QualityVsSpeed, 30u);
        SetBool(LowLatencyMode, true);

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MajorType, MediaTypeVideo).CheckError();
            output.Set(Subtype, FormatH264).CheckError();
            output.Set(AverageBitrate, (uint)bitrate).CheckError();
            output.Set(FrameSize, Pack(width, height)).CheckError();
            output.Set(FrameRate, Pack(framesPerSecond, 1)).CheckError();
            output.Set(PixelAspectRatio, Pack(1, 1)).CheckError();
            output.Set(InterlaceMode, 2u).CheckError();
            output.Set(Profile, 66u).CheckError();
            _transform.SetOutputType(0, output, 0);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MajorType, MediaTypeVideo).CheckError();
            input.Set(Subtype, FormatNv12).CheckError();
            input.Set(FrameSize, Pack(width, height)).CheckError();
            input.Set(FrameRate, Pack(framesPerSecond, 1)).CheckError();
            input.Set(PixelAspectRatio, Pack(1, 1)).CheckError();
            input.Set(InterlaceMode, 2u).CheckError();
            input.Set(YuvMatrix, 1u).CheckError();
            input.Set(NominalRange, 2u).CheckError();
            input.Set(Primaries, 2u).CheckError();
            input.Set(TransferFunction, 5u).CheckError();
            _transform.SetInputType(0, input, 0);
        }

        SetBool(LowLatencyMode, true);
        ReadSequenceHeader();
        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & ProvidesSamples) != 0;
        _outputSize = Math.Max(info.Size, width * height * 2);
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    public int Width { get; }

    public int Height { get; }

    public void SetBitrate(int bitrate) => SetValue(MeanBitRate, (uint)bitrate);

    // One picture in; whatever the encoder has ready comes out through `output` (Annex B, key frame or not).
    public void Encode(byte[] nv12, bool keyFrame, Action<byte[], bool> output)
    {
        if (keyFrame)
        {
            SetValue(ForceKeyFrame, 1u);
        }

        using (var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length))
        using (var sample = MediaFactory.MFCreateSample())
        {
            buffer.Lock(out var pointer, out _, out _);
            Marshal.Copy(nv12, 0, pointer, nv12.Length);
            buffer.Unlock();
            buffer.CurrentLength = nv12.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = _time;
            sample.SampleDuration = _duration;
            _time += _duration;
            _transform.ProcessInput(0, sample, 0);
        }

        Drain(output);
    }

    private void Drain(Action<byte[], bool> output)
    {
        while (true)
        {
            IMFSample? own = null;
            IMFMediaBuffer? ownBuffer = null;
            if (!_providesSamples)
            {
                own = MediaFactory.MFCreateSample();
                ownBuffer = MediaFactory.MFCreateMemoryBuffer(_outputSize);
                own.AddBuffer(ownBuffer);
            }

            try
            {
                var data = new OutputDataBuffer { StreamID = 0, Sample = own! };
                var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
                data.Events?.Dispose();
                if (result.Code == NeedMoreInput)
                {
                    return;
                }

                if (result.Code == StreamChange)
                {
                    // The encoder settled its output format: take what it offers and carry on.
                    using var type = _transform.GetOutputAvailableType(0, 0);
                    _transform.SetOutputType(0, type, 0);
                    ReadSequenceHeader();
                    continue;
                }

                result.CheckError();
                var produced = data.Sample;
                try
                {
                    using var contiguous = produced.ConvertToContiguousBuffer();
                    contiguous.Lock(out var pointer, out _, out var length);
                    var bytes = new byte[length];
                    Marshal.Copy(pointer, bytes, 0, length);
                    contiguous.Unlock();
                    var key = AnnexB.HasNal(bytes, 5);
                    if (key && !AnnexB.HasNal(bytes, 7) && _sequenceHeader is { Length: > 0 } header)
                    {
                        // Some encoders keep SPS/PPS in the media type only; a decoder needs them in front of the key frame.
                        bytes = [.. header, .. bytes];
                    }

                    output(bytes, key);
                }
                finally
                {
                    if (_providesSamples)
                    {
                        produced.Dispose();
                    }
                }
            }
            finally
            {
                ownBuffer?.Dispose();
                own?.Dispose();
            }
        }
    }

    private void ReadSequenceHeader()
    {
        try
        {
            using var type = _transform.GetOutputCurrentType(0);
            _sequenceHeader = type.GetBlob(SequenceHeader);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            _sequenceHeader = null;
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    // ICodecAPI::SetValue (vtable slot 9) with a hand-made VARIANT: VT_UI4 or VT_BOOL.
    private void SetValue(Guid property, uint value)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 19;
        *(uint*)(variant + 8) = value;
        Call(property, variant);
    }

    private void SetBool(Guid property, bool value)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 11;
        *(short*)(variant + 8) = value ? (short)-1 : (short)0;
        Call(property, variant);
    }

    private void Call(Guid property, byte* variant)
    {
        var table = *(nint**)_codecApi;
        var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, byte*, int>)table[9];
        // Not every encoder knows every property; one it does not know is simply left at its default.
        _ = setValue(_codecApi, &property, variant);
    }

    public void Dispose()
    {
        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
        }

        if (_codecApi != 0)
        {
            Marshal.Release(_codecApi);
        }

        _transform.Dispose();
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}

internal static class AnnexB
{
    // Whether the stream holds a NAL unit of this type (5 = IDR picture, 7 = SPS).
    public static bool HasNal(ReadOnlySpan<byte> stream, int type)
    {
        for (var i = 0; i + 3 < stream.Length; i++)
        {
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1 && (stream[i + 3] & 0x1F) == type)
            {
                return true;
            }
        }

        return false;
    }
}
