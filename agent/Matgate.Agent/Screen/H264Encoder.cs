using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Matgate.Agent.Screen;

// A picture in, H.264 (Annex B) out through the callback the encoder was made with.
internal interface IVideoEncoder : IDisposable
{
    int Width { get; }

    int Height { get; }

    // "hardware: <name>" or "software" - shown in the browser's statistics.
    string Name { get; }

    // False when the encoder cannot take a picture right now (a graphics card still busy): it is skipped.
    bool Encode(byte[] nv12, bool keyFrame);
}

internal static class VideoEncoders
{
    private static volatile bool _hardwareBroken;

    // The graphics card's encoder where there is one that works (much faster, and the processor stays free),
    // otherwise Windows' software encoder. MATGATE_AGENT_SOFTWARE_ENCODER=1 forces the software one.
    public static IVideoEncoder Create(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output)
    {
        if (!_hardwareBroken && Environment.GetEnvironmentVariable("MATGATE_AGENT_SOFTWARE_ENCODER") != "1")
        {
            try
            {
                return new HardwareH264Encoder(width, height, framesPerSecond, bitrate, output);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _hardwareBroken = true;
                AgentLog.Write("Screen: no hardware encoder, using the software one - " + exception.Message);
            }
        }

        return new SoftwareH264Encoder(width, height, framesPerSecond, bitrate, output);
    }

    // A hardware encoder that failed while running: the software one from now on.
    public static void HardwareFailed(Exception exception)
    {
        _hardwareBroken = true;
        AgentLog.Write("Screen: the hardware encoder failed, using the software one - " + exception.Message);
    }
}

// What both Media Foundation encoders share: set up for a remote screen rather than for a film - baseline
// profile (no B-frames, nothing waits for a later picture), low-latency mode (one picture in, one out), constant
// bit rate, key frames only when the viewer asks for one. The output is an Annex B stream - what the browser's
// VideoDecoder takes as it is.
internal abstract unsafe class MediaFoundationEncoder : IVideoEncoder
{
    protected static readonly Guid IidTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
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
    protected static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    protected static readonly Guid FormatH264 = new("34363248-0000-0010-8000-00AA00389B71");
    protected static readonly Guid FormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    // ICodecAPI properties.
    protected static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid QualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    protected const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);
    private const int ProvidesSamples = 0x100;

    private static int _started;

    private readonly Action<byte[], bool> _output;
    private readonly long _duration;
    private nint _codecApi;
    private bool _providesSamples;
    private int _outputSize;
    private byte[]? _sequenceHeader;
    private long _time;

    protected MediaFoundationEncoder(int width, int height, int framesPerSecond, Action<byte[], bool> output)
    {
        Width = width;
        Height = height;
        _output = output;
        _duration = 10_000_000 / framesPerSecond;
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            MediaFactory.MFStartup(true).CheckError();
        }

        _ = CoInitializeEx(0, 0);
    }

    public int Width { get; }

    public int Height { get; }

    public abstract string Name { get; }

    protected IMFTransform Transform { get; set; } = null!;

    public abstract bool Encode(byte[] nv12, bool keyFrame);

    // Media types and settings; the transform is there, nothing streams yet.
    protected void Configure(int framesPerSecond, int bitrate, bool hardware)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(Transform.NativePointer, IidCodecApi, out _codecApi));

        // Set before the media types: the encoder fixes some of it when the output type is set.
        SetValue(RateControlMode, 0u);
        SetValue(MeanBitRate, (uint)bitrate);
        SetValue(BPictureCount, 0u);
        SetValue(GopSize, (uint)(framesPerSecond * 600));
        if (!hardware)
        {
            SetValue(QualityVsSpeed, 30u);
        }

        SetBool(LowLatencyMode, true);

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MajorType, MediaTypeVideo).CheckError();
            output.Set(Subtype, FormatH264).CheckError();
            output.Set(AverageBitrate, (uint)bitrate).CheckError();
            output.Set(FrameSize, Pack(Width, Height)).CheckError();
            output.Set(FrameRate, Pack(framesPerSecond, 1)).CheckError();
            output.Set(PixelAspectRatio, Pack(1, 1)).CheckError();
            output.Set(InterlaceMode, 2u).CheckError();
            output.Set(Profile, 66u).CheckError();
            Transform.SetOutputType(0, output, 0);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MajorType, MediaTypeVideo).CheckError();
            input.Set(Subtype, FormatNv12).CheckError();
            input.Set(FrameSize, Pack(Width, Height)).CheckError();
            input.Set(FrameRate, Pack(framesPerSecond, 1)).CheckError();
            input.Set(PixelAspectRatio, Pack(1, 1)).CheckError();
            input.Set(InterlaceMode, 2u).CheckError();
            input.Set(YuvMatrix, 1u).CheckError();
            input.Set(NominalRange, 2u).CheckError();
            input.Set(Primaries, 2u).CheckError();
            input.Set(TransferFunction, 5u).CheckError();
            Transform.SetInputType(0, input, 0);
        }

        SetBool(LowLatencyMode, true);
        ReadSequenceHeader();
        var info = Transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & ProvidesSamples) != 0;
        _outputSize = Math.Max(info.Size, Width * Height * 2);
    }

    protected void StartStreaming()
    {
        Transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        Transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    protected void Submit(byte[] nv12, bool keyFrame)
    {
        if (keyFrame)
        {
            SetValue(ForceKeyFrame, 1u);
        }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        using var sample = MediaFactory.MFCreateSample();
        buffer.Lock(out var pointer, out _, out _);
        Marshal.Copy(nv12, 0, pointer, nv12.Length);
        buffer.Unlock();
        buffer.CurrentLength = nv12.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = _time;
        sample.SampleDuration = _duration;
        _time += _duration;
        Transform.ProcessInput(0, sample, 0);
    }

    // One output: true when a picture came out (or the format settled), false when the encoder needs more input.
    protected bool TakeOutput()
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
            var result = Transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _);
            data.Events?.Dispose();
            if (result.Code == NeedMoreInput)
            {
                return false;
            }

            if (result.Code == StreamChange)
            {
                // The encoder settled its output format: take what it offers and carry on.
                using var type = Transform.GetOutputAvailableType(0, 0);
                Transform.SetOutputType(0, type, 0);
                ReadSequenceHeader();
                return true;
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

                _output(bytes, key);
            }
            finally
            {
                if (_providesSamples)
                {
                    produced.Dispose();
                }
            }

            return true;
        }
        finally
        {
            ownBuffer?.Dispose();
            own?.Dispose();
        }
    }

    private void ReadSequenceHeader()
    {
        try
        {
            using var type = Transform.GetOutputCurrentType(0);
            _sequenceHeader = type.GetBlob(SequenceHeader);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            _sequenceHeader = null;
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    // ICodecAPI::SetValue (vtable slot 9) with a hand-made VARIANT: VT_UI4 or VT_BOOL.
    protected void SetValue(Guid property, uint value)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 19;
        *(uint*)(variant + 8) = value;
        Call(property, variant);
    }

    protected void SetBool(Guid property, bool value)
    {
        var variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = 11;
        *(short*)(variant + 8) = value ? (short)-1 : (short)0;
        Call(property, variant);
    }

    private void Call(Guid property, byte* variant)
    {
        if (_codecApi == 0)
        {
            return;
        }

        var table = *(nint**)_codecApi;
        var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, byte*, int>)table[9];
        // Not every encoder knows every property; one it does not know is simply left at its default.
        _ = setValue(_codecApi, &property, variant);
    }

    public virtual void Dispose()
    {
        try
        {
            Transform?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
        }

        if (_codecApi != 0)
        {
            Marshal.Release(_codecApi);
            _codecApi = 0;
        }

        Transform?.Dispose();
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint model);

    [DllImport("ole32.dll")]
    protected static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}

// Windows' own software encoder: synchronous - a picture in, its output right away.
internal sealed class SoftwareH264Encoder : MediaFoundationEncoder
{
    private static readonly Guid ClsidMsH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");

    public SoftwareH264Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output)
        : base(width, height, framesPerSecond, output)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ClsidMsH264Encoder, 0, 1, IidTransform, out var pointer));
        Transform = new IMFTransform(pointer);
        Configure(framesPerSecond, bitrate, hardware: false);
        StartStreaming();
    }

    public override string Name => "software";

    public override bool Encode(byte[] nv12, bool keyFrame)
    {
        Submit(nv12, keyFrame);
        while (TakeOutput())
        {
        }

        return true;
    }
}

// The graphics card's H.264 encoder (NVIDIA, AMD, Intel), as Media Foundation offers it: asynchronous - it says
// when it wants a picture (NeedInput) and when one is done (HaveOutput). Its events are handled on a thread of
// their own, so a finished picture goes out the moment it is ready, not with the next one.
internal sealed class HardwareH264Encoder : MediaFoundationEncoder
{
    private static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private const uint EnumHardware = 0x04;
    private const uint EnumSortAndFilter = 0x40;
    private const int NeedInput = 601;
    private const int HaveOutput = 602;
    private const int EventError = 1;

    private readonly SemaphoreSlim _wanted = new(0, int.MaxValue);
    private readonly IMFMediaEventGenerator _events;
    private readonly Thread _pump;
    private readonly string _name;
    private volatile bool _closing;
    private volatile Exception? _failure;

    public HardwareH264Encoder(int width, int height, int framesPerSecond, int bitrate, Action<byte[], bool> output)
        : base(width, height, framesPerSecond, output)
    {
        MediaFactory.MFTEnumEx(CategoryVideoEncoder, EnumHardware | EnumSortAndFilter,
            new RegisterTypeInfo { GuidMajorType = MediaTypeVideo, GuidSubtype = FormatNv12 },
            new RegisterTypeInfo { GuidMajorType = MediaTypeVideo, GuidSubtype = FormatH264 },
            out var activates, out var count);
        if (count == 0)
        {
            Marshal.FreeCoTaskMem(activates);
            throw new InvalidOperationException("this machine has no hardware H.264 encoder");
        }

        IMFActivate? chosen = null;
        for (var i = 0; i < count; i++)
        {
            var activate = new IMFActivate(Marshal.ReadIntPtr(activates, i * IntPtr.Size));
            if (i == 0)
            {
                chosen = activate;
            }
            else
            {
                activate.Dispose();
            }
        }

        Marshal.FreeCoTaskMem(activates);
        try
        {
            try
            {
                _name = chosen!.GetString(FriendlyName);
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                _name = "GPU";
            }

            chosen!.ActivateObject<IMFTransform>(IidTransform, out var transform).CheckError();
            Transform = transform ?? throw new InvalidOperationException("the hardware encoder did not start");
        }
        finally
        {
            chosen?.Dispose();
        }

        // Asynchronous transforms have to be unlocked before anything else is set.
        Transform.Attributes.Set(AsyncUnlock, 1u);
        Transform.Attributes.Set(LowLatencyMode, 1u);
        Configure(framesPerSecond, bitrate, hardware: true);
        _events = Transform.QueryInterface<IMFMediaEventGenerator>();
        _pump = new Thread(Pump) { IsBackground = true, Name = "Matgate encoder events" };
        _pump.Start();
        StartStreaming();
    }

    public override string Name => "hardware: " + _name;

    public override bool Encode(byte[] nv12, bool keyFrame)
    {
        if (_failure is { } failure)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }

        // The card says when it can take the next picture; a key frame may wait a little longer.
        if (!_wanted.Wait(keyFrame ? 500 : 60))
        {
            return false;
        }

        Submit(nv12, keyFrame);
        return true;
    }

    private void Pump()
    {
        while (!_closing)
        {
            IMFMediaEvent mediaEvent;
            try
            {
                mediaEvent = _events.GetEvent(0);
            }
            catch (SharpGen.Runtime.SharpGenException exception)
            {
                if (!_closing)
                {
                    _failure = exception;
                    _wanted.Release();
                }

                return;
            }

            using (mediaEvent)
            {
                try
                {
                    switch ((int)mediaEvent.EventType)
                    {
                        case NeedInput:
                            _wanted.Release();
                            break;
                        case HaveOutput:
                            TakeOutput();
                            break;
                        case EventError:
                            _failure = new InvalidOperationException("the encoder reported an error");
                            _wanted.Release();
                            break;
                    }
                }
                catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or COMException or InvalidOperationException)
                {
                    if (!_closing)
                    {
                        _failure = exception;
                        _wanted.Release();
                    }
                }
            }
        }
    }

    public override void Dispose()
    {
        _closing = true;
        try
        {
            Transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
        }

        try
        {
            using var shutdown = Transform.QueryInterface<IMFShutdown>();
            shutdown.Shutdown();
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidCastException)
        {
        }

        _pump.Join(TimeSpan.FromSeconds(1));
        _events.Dispose();
        base.Dispose();
    }
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
