using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Matgate.Display.Video;

// OpenH264 (Cisco's H.264 codec, BSD licence) the way Ubuntu 24.04 ships it: libopenh264.so.7 from the package
// libopenh264-7, version 2.4.1, built by the distribution from source - nothing is fetched from Cisco.
//
// Its API is a C++ interface. Three plain functions are exported by name; everything else is a virtual method
// of the encoder object and is called here through the function pointers in the object's vtable. No compiler
// checks any of this, so every size, offset, enum value and vtable slot in this file is what
// tests/unit/Video.Tests/abi/openh264_abi.cpp prints when it is compiled against the distribution's headers -
// the test AbiTests compares the two. At run time a library of a version nobody has verified is refused.
internal static unsafe class OpenH264
{
    // The soname of the distribution's package. The unversioned name only exists where the headers are
    // installed too; the version check below decides either way.
#if AGENT_UNIX
    // The agent (agent/Matgate.Agent compiles this file too) runs on whatever Linux the machine has: every
    // soname the distributions ship, and the layout check below instead of a list of versions.
    private static readonly string[] LibraryNames = ["libopenh264.so.7", "libopenh264.so.8", "libopenh264.so.6", "libopenh264.so"];
#else
    private static readonly string[] LibraryNames = ["libopenh264.so.7", "libopenh264.so"];
#endif

    // The versions tests/unit/Video.Tests/abi/ holds a verified layout for.
    private static readonly (uint Major, uint Minor, uint Revision)[] VerifiedVersions = [(2, 4, 1)];

    // ISVCEncoder's virtual methods in the order of their declaration (the two destructors come after them).
    internal const int SlotInitialize = 0;
    internal const int SlotInitializeExt = 1;
    internal const int SlotGetDefaultParams = 2;
    internal const int SlotUninitialize = 3;
    internal const int SlotEncodeFrame = 4;
    internal const int SlotEncodeParameterSets = 5;
    internal const int SlotForceIntraFrame = 6;
    internal const int SlotSetOption = 7;
    internal const int SlotGetOption = 8;

    internal const int MaxSpatialLayers = 4;
    internal const int MaxLayersOfFrame = 128;
    internal const int MaxSlices = 35;

    // cmMallocMemeError: what EncodeFrame answers when the picture did not fit into its output buffer (it
    // drops its whole state then).
    internal const int ResultOutOfMemory = 3;

    // WELS_LOG_QUIET: the library writes its messages to stderr unless it is told not to.
    internal const int LogQuiet = 0;

    // videoFormatI420
    internal const int FormatI420 = 23;

    // Colour description for the stream's header: EVideoFormatSPS, EColorPrimaries, ETransferCharacteristics
    // and EColorMatrix.
    internal const byte VideoFormatUndefined = 5;
    internal const byte PrimariesBt709 = 1;
    internal const byte TransferSrgb = 13;
    internal const byte MatrixBt601 = 6;

    // LAYER_TYPE
    internal const byte LayerVideoCoding = 1;

    private static readonly delegate* unmanaged[Cdecl]<void**, int> _createEncoder;
    private static readonly delegate* unmanaged[Cdecl]<void*, void> _destroyEncoder;

    static OpenH264()
    {
        try
        {
            UnavailableReason = Load(out LibraryVersion, out _createEncoder, out _destroyEncoder);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            UnavailableReason = "libopenh264 could not be loaded: " + exception.Message;
        }

        IsAvailable = UnavailableReason is null;
    }

    public static bool IsAvailable { get; }

    public static string? UnavailableReason { get; }

    public static readonly string? LibraryVersion;

    public static void* CreateEncoder()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(UnavailableReason);
        }

        void* encoder = null;
        var result = _createEncoder(&encoder);
        if (result != 0 || encoder == null)
        {
            throw new InvalidOperationException($"OpenH264 could not create an encoder (error {result}).");
        }

        return encoder;
    }

    public static void DestroyEncoder(void* encoder) => _destroyEncoder(encoder);

    public static int InitializeExt(void* encoder, EncoderParameters* parameters) =>
        ((delegate* unmanaged[Cdecl]<void*, EncoderParameters*, int>)Method(encoder, SlotInitializeExt))(encoder, parameters);

    public static int GetDefaultParams(void* encoder, EncoderParameters* parameters) =>
        ((delegate* unmanaged[Cdecl]<void*, EncoderParameters*, int>)Method(encoder, SlotGetDefaultParams))(encoder, parameters);

    public static int Uninitialize(void* encoder) =>
        ((delegate* unmanaged[Cdecl]<void*, int>)Method(encoder, SlotUninitialize))(encoder);

    public static int EncodeFrame(void* encoder, SourcePicture* picture, FrameBitstream* output) =>
        ((delegate* unmanaged[Cdecl]<void*, SourcePicture*, FrameBitstream*, int>)Method(encoder, SlotEncodeFrame))(encoder, picture, output);

    // The second argument is the layer; -1 means all of them (there is only one here). C++ fills it in as a
    // default argument, which a call through the vtable does not get for free.
    public static int ForceIntraFrame(void* encoder) =>
        ((delegate* unmanaged[Cdecl]<void*, byte, int, int>)Method(encoder, SlotForceIntraFrame))(encoder, 1, -1);

    public static int SetOption(void* encoder, EncoderOption option, void* value) =>
        ((delegate* unmanaged[Cdecl]<void*, int, void*, int>)Method(encoder, SlotSetOption))(encoder, (int)option, value);

    public static int GetOption(void* encoder, EncoderOption option, void* value) =>
        ((delegate* unmanaged[Cdecl]<void*, int, void*, int>)Method(encoder, SlotGetOption))(encoder, (int)option, value);

    // An object of a C++ class with virtual methods starts with the pointer to its vtable.
    private static void* Method(void* encoder, int slot) => (*(void***)encoder)[slot];

    private static string? Load(
        out string? version,
        out delegate* unmanaged[Cdecl]<void**, int> createEncoder,
        out delegate* unmanaged[Cdecl]<void*, void> destroyEncoder)
    {
        version = null;
        createEncoder = null;
        destroyEncoder = null;
        if (!OperatingSystem.IsLinux())
        {
            return "OpenH264 is only used on Linux, where the distribution's package libopenh264-7 provides it.";
        }

        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return $"OpenH264's layout has not been verified for {RuntimeInformation.ProcessArchitecture}.";
        }

        nint library = 0;
        foreach (var name in LibraryNames)
        {
            if (NativeLibrary.TryLoad(name, out library))
            {
                break;
            }
        }

        if (library == 0)
        {
            return $"{LibraryNames[0]} was not found: the package libopenh264-7 is not installed.";
        }

        if (!NativeLibrary.TryGetExport(library, "WelsGetCodecVersionEx", out var getVersion)
            || !NativeLibrary.TryGetExport(library, "WelsCreateSVCEncoder", out var create)
            || !NativeLibrary.TryGetExport(library, "WelsDestroySVCEncoder", out var destroy))
        {
            return $"{LibraryNames[0]} is not the OpenH264 library Matgate knows: it lacks the encoder's functions.";
        }

        CodecVersion found = default;
        ((delegate* unmanaged[Cdecl]<CodecVersion*, void>)getVersion)(&found);
        version = $"{found.Major}.{found.Minor}.{found.Revision}";
#if AGENT_UNIX
        if (found.Major != 2)
#else
        if (!VerifiedVersions.Contains((found.Major, found.Minor, found.Revision)))
#endif
        {
            var verified = string.Join(", ", VerifiedVersions.Select(each => $"{each.Major}.{each.Minor}.{each.Revision}"));
            return $"OpenH264 {version} is installed, but Matgate has only verified the binary layout of {verified}.";
        }

        createEncoder = (delegate* unmanaged[Cdecl]<void**, int>)create;
        destroyEncoder = (delegate* unmanaged[Cdecl]<void*, void>)destroy;
        return HasExpectedLayout(createEncoder, destroyEncoder)
            ? null
            : $"OpenH264 {version} does not have the parameter layout Matgate verified for that version.";
    }

    // The version number alone is a promise. Here the library fills in its defaults, and a handful of them -
    // from the first field to the last, with one deep inside the array of layers - must be where this file
    // says they are.
    private static bool HasExpectedLayout(delegate* unmanaged[Cdecl]<void**, int> createEncoder, delegate* unmanaged[Cdecl]<void*, void> destroyEncoder)
    {
        void* encoder = null;
        if (createEncoder(&encoder) != 0 || encoder == null)
        {
            return false;
        }

        try
        {
            var quiet = LogQuiet;
            SetOption(encoder, EncoderOption.TraceLevel, &quiet);
            EncoderParameters parameters;
            new Span<byte>(&parameters, sizeof(EncoderParameters)).Fill(0xA5);
            return GetDefaultParams(encoder, &parameters) == 0
                && parameters.UsageType == UsageType.CameraVideoRealTime
                && parameters.MaxFrameRate == 60f
                && parameters.SpatialLayerNum == 1
                && parameters.SpatialLayers[0].DLayerQp == 26
                && parameters.SpatialLayers[MaxSpatialLayers - 1].SliceArgument.SliceSizeConstraint == 1500
                && parameters.NumRefFrame == -1
                && parameters.EnableFrameSkip == 1
                && parameters.MaxQp == 51
                && parameters.LtrMarkPeriod == 30
                && parameters.MultipleThreadIdc == 1
                && parameters.EnableFrameCroppingFlag == 1
                && parameters.IdrBitrateRatio == 400;
        }
        finally
        {
            destroyEncoder(encoder);
        }
    }
}

// EUsageType
internal enum UsageType
{
    CameraVideoRealTime = 0,
    ScreenContentRealTime = 1,
}

// RC_MODES
internal enum RateControlMode
{
    Off = -1,
    Quality = 0,
    Bitrate = 1,
    BufferBased = 2,
    Timestamp = 3,
}

// ECOMPLEXITY_MODE
internal enum ComplexityMode
{
    Low = 0,
    Medium = 1,
    High = 2,
}

// EParameterSetStrategy
internal enum ParameterSetStrategy
{
    ConstantId = 0,
    IncreasingId = 1,
}

// SliceModeEnum
internal enum SliceMode
{
    Single = 0,
    FixedCount = 1,
}

// EVideoFrameType
internal enum FrameType
{
    Invalid = 0,
    Idr = 1,
    I = 2,
    P = 3,
    Skip = 4,
    IPMixed = 5,
}

// ENCODER_OPTION
internal enum EncoderOption
{
    DataFormat = 0,
    ParametersExt = 3,
    Bitrate = 5,
    MaxBitrate = 6,
    RateControlMode = 8,
    FrameSkip = 9,
    Complexity = 19,
    TraceLevel = 25,
    GetStatistics = 28,
    BitsVaryPercentage = 31,
}

// OpenH264Version
[StructLayout(LayoutKind.Sequential)]
internal struct CodecVersion
{
    public uint Major;
    public uint Minor;
    public uint Revision;
    public uint Reserved;
}

// SSliceArgument
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SliceArgument
{
    public SliceMode SliceMode;
    public uint SliceNum;
    public fixed uint SliceMbNum[OpenH264.MaxSlices];
    public uint SliceSizeConstraint;
}

// SSpatialLayerConfig. The library can encode up to four sizes of a picture at once; Matgate uses one.
[StructLayout(LayoutKind.Sequential)]
internal struct SpatialLayer
{
    public int VideoWidth;
    public int VideoHeight;
    public float FrameRate;
    public int SpatialBitrate;
    public int MaxSpatialBitrate;
    public int ProfileIdc;
    public int LevelIdc;
    public int DLayerQp;
    public SliceArgument SliceArgument;
    public byte VideoSignalTypePresent;
    public byte VideoFormat;
    public byte FullRange;
    public byte ColorDescriptionPresent;
    public byte ColorPrimaries;
    public byte TransferCharacteristics;
    public byte ColorMatrix;
    public byte AspectRatioPresent;
    public int AspectRatio;
    public ushort AspectRatioExtWidth;
    public ushort AspectRatioExtHeight;
}

[InlineArray(OpenH264.MaxSpatialLayers)]
internal struct SpatialLayers
{
    private SpatialLayer _first;
}

// SEncParamExt. A C++ bool is one byte.
[StructLayout(LayoutKind.Sequential)]
internal struct EncoderParameters
{
    public UsageType UsageType;
    public int PicWidth;
    public int PicHeight;
    public int TargetBitrate;
    public RateControlMode RCMode;
    public float MaxFrameRate;
    public int TemporalLayerNum;
    public int SpatialLayerNum;
    public SpatialLayers SpatialLayers;
    public ComplexityMode ComplexityMode;
    public uint IntraPeriod;
    public int NumRefFrame;
    public ParameterSetStrategy SpsPpsIdStrategy;
    public byte PrefixNalAddingCtrl;
    public byte EnableSSEI;
    public byte SimulcastAVC;
    public int PaddingFlag;
    public int EntropyCodingModeFlag;
    public byte EnableFrameSkip;
    public int MaxBitrate;
    public int MaxQp;
    public int MinQp;
    public uint MaxNalSize;
    public byte EnableLongTermReference;
    public int LTRRefNum;
    public uint LtrMarkPeriod;
    public ushort MultipleThreadIdc;
    public byte UseLoadBalancing;
    public int LoopFilterDisableIdc;
    public int LoopFilterAlphaC0Offset;
    public int LoopFilterBetaOffset;
    public byte EnableDenoise;
    public byte EnableBackgroundDetection;
    public byte EnableAdaptiveQuant;
    public byte EnableFrameCroppingFlag;
    public byte EnableSceneChangeDetect;
    public byte IsLosslessLink;
    public byte FixRCOverShoot;
    public int IdrBitrateRatio;
}

// SLayerBSInfo: the NAL units of one layer of an encoded picture, one after another in BsBuf.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct LayerBitstream
{
    public byte TemporalId;
    public byte SpatialId;
    public byte QualityId;
    public FrameType FrameType;
    public byte LayerType;
    public int SubSeqId;
    public int NalCount;
    public int* NalLengthInByte;
    public byte* BsBuf;
}

[InlineArray(OpenH264.MaxLayersOfFrame)]
internal struct LayerBitstreams
{
    private LayerBitstream _first;
}

// SFrameBSInfo
[StructLayout(LayoutKind.Sequential)]
internal struct FrameBitstream
{
    public int LayerNum;
    public LayerBitstreams LayerInfo;
    public FrameType FrameType;
    public int FrameSizeInBytes;
    public long TimeStamp;
}

// SSourcePicture
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SourcePicture
{
    public int ColorFormat;
    public fixed int Stride[4];
    public byte* Data0;
    public byte* Data1;
    public byte* Data2;
    public byte* Data3;
    public int PicWidth;
    public int PicHeight;
    public long TimeStamp;
}

// SEncoderStatistics; "unsigned long" has the size of a pointer on Linux.
[StructLayout(LayoutKind.Sequential)]
internal struct EncoderStatistics
{
    public uint Width;
    public uint Height;
    public float AverageFrameSpeedInMs;
    public float AverageFrameRate;
    public float LatestFrameRate;
    public uint BitRate;
    public uint AverageFrameQP;
    public uint InputFrameCount;
    public uint SkippedFrameCount;
    public uint ResolutionChangeTimes;
    public uint IDRReqNum;
    public uint IDRSentNum;
    public uint LTRSentNum;
    public long StatisticsTs;
    public nuint TotalEncodedBytes;
    public nuint LastStatisticsBytes;
    public nuint LastStatisticsFrameCount;
}
