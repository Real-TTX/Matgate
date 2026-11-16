using System.Runtime.InteropServices;
using Xunit;

namespace Matgate.Display.Video.Tests;

// OpenH264 is called through a vtable and with structs that only exist as C declarations: nothing checks that
// OpenH264.cs still says what the library's headers say. So the headers are asked - abi/openh264_abi.cpp prints
// sizes, offsets, enum values and vtable slots - and every line is compared with the C# side.
//
// The output is also kept in abi/ for every verified version and architecture, so the comparison runs where
// there is no compiler (a developer's Windows machine) and shows in a review what a new version changed.
public sealed unsafe class AbiTests
{
    // The values the library fills into its default parameters, which OpenH264.HasExpectedLayout looks at
    // before it trusts a library.
    private static readonly Dictionary<string, long> Defaults = new()
    {
        ["default.SEncParamExt.iUsageType"] = (long)UsageType.CameraVideoRealTime,
        ["default.SEncParamExt.fMaxFrameRate"] = 60,
        ["default.SEncParamExt.iSpatialLayerNum"] = 1,
        ["default.SEncParamExt.sSpatialLayers0.iDLayerQp"] = 26,
        ["default.SEncParamExt.sSpatialLayers3.uiSliceSizeConstraint"] = 1500,
        ["default.SEncParamExt.iNumRefFrame"] = -1,
        ["default.SEncParamExt.bEnableFrameSkip"] = 1,
        ["default.SEncParamExt.iMaxQp"] = 51,
        ["default.SEncParamExt.iLtrMarkPeriod"] = 30,
        ["default.SEncParamExt.iMultipleThreadIdc"] = 1,
        ["default.SEncParamExt.bEnableFrameCroppingFlag"] = 1,
        ["default.SEncParamExt.iIdrBitrateRatio"] = 400,
    };

    public static TheoryData<string> RecordedFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(AbiFolder, "openh264-*.txt").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    private static string AbiFolder => Path.Combine(AppContext.BaseDirectory, "abi");

    [Fact]
    public void ALayoutIsRecordedForTheVersionAndArchitecturesMatgateRunsOn()
    {
        Assert.True(File.Exists(Path.Combine(AbiFolder, "openh264-2.4.1-linux-x64.txt")));
        Assert.True(File.Exists(Path.Combine(AbiFolder, "openh264-2.4.1-linux-arm64.txt")));
    }

    [Theory]
    [MemberData(nameof(RecordedFiles))]
    public void TheRecordedLayoutIsWhatTheCodeAssumes(string file)
    {
        var facts = Parse(File.ReadAllText(Path.Combine(AbiFolder, file)));
        Compare(facts);
        Assert.StartsWith($"openh264-{facts["library.version"]}-linux-", file);
    }

    // In the container the suite is meant to run in: compiles the program against the installed headers,
    // runs it against the installed library and expects exactly what is recorded.
    [Fact]
    public void TheInstalledLibraryHasTheRecordedLayout()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The layout is taken from the distribution's headers on Linux.");
        Assert.SkipUnless(File.Exists("/usr/include/wels/codec_api.h"), "libopenh264-dev is not installed.");
        Assert.SkipUnless(Tool.Works("g++", "--version"), "g++ is not installed.");

        var program = Path.Combine(Path.GetTempPath(), "openh264_abi_" + Guid.NewGuid().ToString("N"));
        try
        {
            Tool.Run("g++", "-std=c++17", "-O0", "-o", program, Path.Combine(AbiFolder, "openh264_abi.cpp"), "-lopenh264");
            var output = Tool.Run(program);
            var facts = Parse(output);
            Compare(facts);

            // The headers and the library belong to the same version, and it is the one the code loaded.
            Assert.Equal(facts["header.version"], facts["library.version"]);
            Assert.True(H264Encoder.IsAvailable, H264Encoder.UnavailableReason);
            Assert.Equal(facts["library.version"], H264Encoder.LibraryVersion);

            var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var recorded = Path.Combine(AbiFolder, $"openh264-{facts["library.version"]}-linux-{architecture}.txt");
            Assert.True(File.Exists(recorded), $"Nothing is recorded for this library: save the program's output as abi/{Path.GetFileName(recorded)}.");
            Assert.Equal(Lines(File.ReadAllText(recorded)), Lines(output));
        }
        finally
        {
            File.Delete(program);
        }
    }

    [Fact]
    public void TheLibraryIsEitherUsableOrSaysWhyNot()
    {
        if (H264Encoder.IsAvailable)
        {
            Assert.Null(H264Encoder.UnavailableReason);
            Assert.Equal("2.4.1", H264Encoder.LibraryVersion);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(H264Encoder.UnavailableReason));
            Assert.Throws<InvalidOperationException>(() => new H264Encoder(640, 480, 60, 4000));
        }
    }

    private static void Compare(Dictionary<string, string> facts)
    {
        var expected = Expected();
        var problems = new List<string>();
        foreach (var (name, value) in facts)
        {
            if (name is "header.version" or "library.version")
            {
                continue;
            }

            if (name.StartsWith("library.vtable.", StringComparison.Ordinal))
            {
                // The slot as found in an object the library made, not only in the headers.
                Check(name, value, expected["vtable." + name["library.vtable.".Length..]], problems);
            }
            else if (name.StartsWith("default.", StringComparison.Ordinal))
            {
                if (Defaults.TryGetValue(name, out var wanted))
                {
                    Check(name, value, wanted, problems);
                }
            }
            else if (expected.TryGetValue(name, out var wanted))
            {
                Check(name, value, wanted, problems);
            }
            else
            {
                problems.Add($"{name}: printed by the program, unknown to the test");
            }
        }

        foreach (var name in expected.Keys.Concat(Defaults.Keys))
        {
            if (!facts.ContainsKey(name))
            {
                problems.Add($"{name}: assumed by the code, not printed by the program");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void Check(string name, string printed, long wanted, List<string> problems)
    {
        if (!long.TryParse(printed, out var value) || value != wanted)
        {
            problems.Add($"{name}: the library says {printed}, the code assumes {wanted}");
        }
    }

    // Everything OpenH264.cs relies on, under the names the program prints.
    private static Dictionary<string, long> Expected()
    {
        var facts = new Dictionary<string, long>
        {
            ["sizeof.void*"] = sizeof(nint),
            ["sizeof.bool"] = sizeof(byte),
            ["sizeof.int"] = sizeof(int),
            ["sizeof.long"] = sizeof(nuint),
            ["sizeof.EUsageType"] = sizeof(UsageType),

            ["enum.MAX_SPATIAL_LAYER_NUM"] = OpenH264.MaxSpatialLayers,
            ["enum.MAX_LAYER_NUM_OF_FRAME"] = OpenH264.MaxLayersOfFrame,
            ["enum.MAX_SLICES_NUM_TMP"] = OpenH264.MaxSlices,
            ["enum.AUTO_REF_PIC_COUNT"] = -1,
            ["enum.UNSPECIFIED_BIT_RATE"] = 0,

            ["enum.cmResultSuccess"] = 0,
            ["enum.cmInitParaError"] = 1,
            ["enum.cmUnknownReason"] = 2,
            ["enum.cmMallocMemeError"] = OpenH264.ResultOutOfMemory,
            ["enum.cmInitExpected"] = 4,
            ["enum.cmUnsupportedData"] = 5,

            ["enum.CAMERA_VIDEO_REAL_TIME"] = (long)UsageType.CameraVideoRealTime,
            ["enum.SCREEN_CONTENT_REAL_TIME"] = (long)UsageType.ScreenContentRealTime,

            ["enum.RC_QUALITY_MODE"] = (long)RateControlMode.Quality,
            ["enum.RC_BITRATE_MODE"] = (long)RateControlMode.Bitrate,
            ["enum.RC_BUFFERBASED_MODE"] = (long)RateControlMode.BufferBased,
            ["enum.RC_TIMESTAMP_MODE"] = (long)RateControlMode.Timestamp,
            ["enum.RC_OFF_MODE"] = (long)RateControlMode.Off,

            ["enum.LOW_COMPLEXITY"] = (long)ComplexityMode.Low,
            ["enum.MEDIUM_COMPLEXITY"] = (long)ComplexityMode.Medium,
            ["enum.HIGH_COMPLEXITY"] = (long)ComplexityMode.High,

            ["enum.CONSTANT_ID"] = (long)ParameterSetStrategy.ConstantId,
            ["enum.INCREASING_ID"] = (long)ParameterSetStrategy.IncreasingId,

            ["enum.SM_SINGLE_SLICE"] = (long)SliceMode.Single,
            ["enum.SM_FIXEDSLCNUM_SLICE"] = (long)SliceMode.FixedCount,

            ["enum.PRO_UNKNOWN"] = 0,
            ["enum.PRO_BASELINE"] = 66,
            ["enum.PRO_MAIN"] = 77,
            ["enum.PRO_HIGH"] = 100,
            ["enum.LEVEL_UNKNOWN"] = 0,

            ["enum.videoFormatI420"] = OpenH264.FormatI420,

            ["enum.videoFrameTypeInvalid"] = (long)FrameType.Invalid,
            ["enum.videoFrameTypeIDR"] = (long)FrameType.Idr,
            ["enum.videoFrameTypeI"] = (long)FrameType.I,
            ["enum.videoFrameTypeP"] = (long)FrameType.P,
            ["enum.videoFrameTypeSkip"] = (long)FrameType.Skip,
            ["enum.videoFrameTypeIPMixed"] = (long)FrameType.IPMixed,

            ["enum.NON_VIDEO_CODING_LAYER"] = 0,
            ["enum.VIDEO_CODING_LAYER"] = OpenH264.LayerVideoCoding,

            ["enum.VF_UNDEF"] = OpenH264.VideoFormatUndefined,
            ["enum.CP_BT709"] = OpenH264.PrimariesBt709,
            ["enum.CP_UNDEF"] = 2,
            ["enum.TRC_BT709"] = 1,
            ["enum.TRC_UNDEF"] = 2,
            ["enum.TRC_IEC61966_2_1"] = OpenH264.TransferSrgb,
            ["enum.CM_UNDEF"] = 2,
            ["enum.CM_SMPTE170M"] = OpenH264.MatrixBt601,

            ["enum.WELS_LOG_QUIET"] = OpenH264.LogQuiet,
            ["enum.WELS_LOG_ERROR"] = 1,
            ["enum.WELS_LOG_WARNING"] = 2,
            ["enum.WELS_LOG_INFO"] = 4,
            ["enum.WELS_LOG_DEBUG"] = 8,
            ["enum.WELS_LOG_DETAIL"] = 16,

            ["enum.ENCODER_OPTION_DATAFORMAT"] = (long)EncoderOption.DataFormat,
            ["enum.ENCODER_OPTION_IDR_INTERVAL"] = 1,
            ["enum.ENCODER_OPTION_SVC_ENCODE_PARAM_BASE"] = 2,
            ["enum.ENCODER_OPTION_SVC_ENCODE_PARAM_EXT"] = (long)EncoderOption.ParametersExt,
            ["enum.ENCODER_OPTION_FRAME_RATE"] = 4,
            ["enum.ENCODER_OPTION_BITRATE"] = (long)EncoderOption.Bitrate,
            ["enum.ENCODER_OPTION_MAX_BITRATE"] = (long)EncoderOption.MaxBitrate,
            ["enum.ENCODER_OPTION_RC_MODE"] = (long)EncoderOption.RateControlMode,
            ["enum.ENCODER_OPTION_RC_FRAME_SKIP"] = (long)EncoderOption.FrameSkip,
            ["enum.ENCODER_OPTION_COMPLEXITY"] = (long)EncoderOption.Complexity,
            ["enum.ENCODER_OPTION_TRACE_LEVEL"] = (long)EncoderOption.TraceLevel,
            ["enum.ENCODER_OPTION_TRACE_CALLBACK"] = 26,
            ["enum.ENCODER_OPTION_TRACE_CALLBACK_CONTEXT"] = 27,
            ["enum.ENCODER_OPTION_GET_STATISTICS"] = (long)EncoderOption.GetStatistics,
            ["enum.ENCODER_OPTION_BITS_VARY_PERCENTAGE"] = (long)EncoderOption.BitsVaryPercentage,

            ["vtable.Initialize"] = OpenH264.SlotInitialize,
            ["vtable.InitializeExt"] = OpenH264.SlotInitializeExt,
            ["vtable.GetDefaultParams"] = OpenH264.SlotGetDefaultParams,
            ["vtable.Uninitialize"] = OpenH264.SlotUninitialize,
            ["vtable.EncodeFrame"] = OpenH264.SlotEncodeFrame,
            ["vtable.EncodeParameterSets"] = OpenH264.SlotEncodeParameterSets,
            ["vtable.ForceIntraFrame"] = OpenH264.SlotForceIntraFrame,
            ["vtable.SetOption"] = OpenH264.SlotSetOption,
            ["vtable.GetOption"] = OpenH264.SlotGetOption,
        };

        CodecVersion version;
        var origin = (byte*)&version;
        facts["sizeof.OpenH264Version"] = sizeof(CodecVersion);
        facts["offsetof.OpenH264Version.uMajor"] = (byte*)&version.Major - origin;
        facts["offsetof.OpenH264Version.uMinor"] = (byte*)&version.Minor - origin;
        facts["offsetof.OpenH264Version.uRevision"] = (byte*)&version.Revision - origin;
        facts["offsetof.OpenH264Version.uReserved"] = (byte*)&version.Reserved - origin;

        SliceArgument slices;
        origin = (byte*)&slices;
        facts["sizeof.SSliceArgument"] = sizeof(SliceArgument);
        facts["offsetof.SSliceArgument.uiSliceMode"] = (byte*)&slices.SliceMode - origin;
        facts["offsetof.SSliceArgument.uiSliceNum"] = (byte*)&slices.SliceNum - origin;
        facts["offsetof.SSliceArgument.uiSliceMbNum"] = (byte*)slices.SliceMbNum - origin;
        facts["offsetof.SSliceArgument.uiSliceSizeConstraint"] = (byte*)&slices.SliceSizeConstraint - origin;

        SpatialLayer layer;
        origin = (byte*)&layer;
        facts["sizeof.SSpatialLayerConfig"] = sizeof(SpatialLayer);
        facts["offsetof.SSpatialLayerConfig.iVideoWidth"] = (byte*)&layer.VideoWidth - origin;
        facts["offsetof.SSpatialLayerConfig.iVideoHeight"] = (byte*)&layer.VideoHeight - origin;
        facts["offsetof.SSpatialLayerConfig.fFrameRate"] = (byte*)&layer.FrameRate - origin;
        facts["offsetof.SSpatialLayerConfig.iSpatialBitrate"] = (byte*)&layer.SpatialBitrate - origin;
        facts["offsetof.SSpatialLayerConfig.iMaxSpatialBitrate"] = (byte*)&layer.MaxSpatialBitrate - origin;
        facts["offsetof.SSpatialLayerConfig.uiProfileIdc"] = (byte*)&layer.ProfileIdc - origin;
        facts["offsetof.SSpatialLayerConfig.uiLevelIdc"] = (byte*)&layer.LevelIdc - origin;
        facts["offsetof.SSpatialLayerConfig.iDLayerQp"] = (byte*)&layer.DLayerQp - origin;
        facts["offsetof.SSpatialLayerConfig.sSliceArgument"] = (byte*)&layer.SliceArgument - origin;
        facts["offsetof.SSpatialLayerConfig.bVideoSignalTypePresent"] = &layer.VideoSignalTypePresent - origin;
        facts["offsetof.SSpatialLayerConfig.uiVideoFormat"] = &layer.VideoFormat - origin;
        facts["offsetof.SSpatialLayerConfig.bFullRange"] = &layer.FullRange - origin;
        facts["offsetof.SSpatialLayerConfig.bColorDescriptionPresent"] = &layer.ColorDescriptionPresent - origin;
        facts["offsetof.SSpatialLayerConfig.uiColorPrimaries"] = &layer.ColorPrimaries - origin;
        facts["offsetof.SSpatialLayerConfig.uiTransferCharacteristics"] = &layer.TransferCharacteristics - origin;
        facts["offsetof.SSpatialLayerConfig.uiColorMatrix"] = &layer.ColorMatrix - origin;
        facts["offsetof.SSpatialLayerConfig.bAspectRatioPresent"] = &layer.AspectRatioPresent - origin;
        facts["offsetof.SSpatialLayerConfig.eAspectRatio"] = (byte*)&layer.AspectRatio - origin;
        facts["offsetof.SSpatialLayerConfig.sAspectRatioExtWidth"] = (byte*)&layer.AspectRatioExtWidth - origin;
        facts["offsetof.SSpatialLayerConfig.sAspectRatioExtHeight"] = (byte*)&layer.AspectRatioExtHeight - origin;

        EncoderParameters parameters;
        origin = (byte*)&parameters;
        facts["sizeof.SEncParamExt"] = sizeof(EncoderParameters);
        facts["offsetof.SEncParamExt.iUsageType"] = (byte*)&parameters.UsageType - origin;
        facts["offsetof.SEncParamExt.iPicWidth"] = (byte*)&parameters.PicWidth - origin;
        facts["offsetof.SEncParamExt.iPicHeight"] = (byte*)&parameters.PicHeight - origin;
        facts["offsetof.SEncParamExt.iTargetBitrate"] = (byte*)&parameters.TargetBitrate - origin;
        facts["offsetof.SEncParamExt.iRCMode"] = (byte*)&parameters.RCMode - origin;
        facts["offsetof.SEncParamExt.fMaxFrameRate"] = (byte*)&parameters.MaxFrameRate - origin;
        facts["offsetof.SEncParamExt.iTemporalLayerNum"] = (byte*)&parameters.TemporalLayerNum - origin;
        facts["offsetof.SEncParamExt.iSpatialLayerNum"] = (byte*)&parameters.SpatialLayerNum - origin;
        facts["offsetof.SEncParamExt.sSpatialLayers"] = (byte*)&parameters.SpatialLayers - origin;
        facts["offsetof.SEncParamExt.iComplexityMode"] = (byte*)&parameters.ComplexityMode - origin;
        facts["offsetof.SEncParamExt.uiIntraPeriod"] = (byte*)&parameters.IntraPeriod - origin;
        facts["offsetof.SEncParamExt.iNumRefFrame"] = (byte*)&parameters.NumRefFrame - origin;
        facts["offsetof.SEncParamExt.eSpsPpsIdStrategy"] = (byte*)&parameters.SpsPpsIdStrategy - origin;
        facts["offsetof.SEncParamExt.bPrefixNalAddingCtrl"] = &parameters.PrefixNalAddingCtrl - origin;
        facts["offsetof.SEncParamExt.bEnableSSEI"] = &parameters.EnableSSEI - origin;
        facts["offsetof.SEncParamExt.bSimulcastAVC"] = &parameters.SimulcastAVC - origin;
        facts["offsetof.SEncParamExt.iPaddingFlag"] = (byte*)&parameters.PaddingFlag - origin;
        facts["offsetof.SEncParamExt.iEntropyCodingModeFlag"] = (byte*)&parameters.EntropyCodingModeFlag - origin;
        facts["offsetof.SEncParamExt.bEnableFrameSkip"] = &parameters.EnableFrameSkip - origin;
        facts["offsetof.SEncParamExt.iMaxBitrate"] = (byte*)&parameters.MaxBitrate - origin;
        facts["offsetof.SEncParamExt.iMaxQp"] = (byte*)&parameters.MaxQp - origin;
        facts["offsetof.SEncParamExt.iMinQp"] = (byte*)&parameters.MinQp - origin;
        facts["offsetof.SEncParamExt.uiMaxNalSize"] = (byte*)&parameters.MaxNalSize - origin;
        facts["offsetof.SEncParamExt.bEnableLongTermReference"] = &parameters.EnableLongTermReference - origin;
        facts["offsetof.SEncParamExt.iLTRRefNum"] = (byte*)&parameters.LTRRefNum - origin;
        facts["offsetof.SEncParamExt.iLtrMarkPeriod"] = (byte*)&parameters.LtrMarkPeriod - origin;
        facts["offsetof.SEncParamExt.iMultipleThreadIdc"] = (byte*)&parameters.MultipleThreadIdc - origin;
        facts["offsetof.SEncParamExt.bUseLoadBalancing"] = &parameters.UseLoadBalancing - origin;
        facts["offsetof.SEncParamExt.iLoopFilterDisableIdc"] = (byte*)&parameters.LoopFilterDisableIdc - origin;
        facts["offsetof.SEncParamExt.iLoopFilterAlphaC0Offset"] = (byte*)&parameters.LoopFilterAlphaC0Offset - origin;
        facts["offsetof.SEncParamExt.iLoopFilterBetaOffset"] = (byte*)&parameters.LoopFilterBetaOffset - origin;
        facts["offsetof.SEncParamExt.bEnableDenoise"] = &parameters.EnableDenoise - origin;
        facts["offsetof.SEncParamExt.bEnableBackgroundDetection"] = &parameters.EnableBackgroundDetection - origin;
        facts["offsetof.SEncParamExt.bEnableAdaptiveQuant"] = &parameters.EnableAdaptiveQuant - origin;
        facts["offsetof.SEncParamExt.bEnableFrameCroppingFlag"] = &parameters.EnableFrameCroppingFlag - origin;
        facts["offsetof.SEncParamExt.bEnableSceneChangeDetect"] = &parameters.EnableSceneChangeDetect - origin;
        facts["offsetof.SEncParamExt.bIsLosslessLink"] = &parameters.IsLosslessLink - origin;
        facts["offsetof.SEncParamExt.bFixRCOverShoot"] = &parameters.FixRCOverShoot - origin;
        facts["offsetof.SEncParamExt.iIdrBitrateRatio"] = (byte*)&parameters.IdrBitrateRatio - origin;

        LayerBitstream layerBitstream;
        origin = (byte*)&layerBitstream;
        facts["sizeof.SLayerBSInfo"] = sizeof(LayerBitstream);
        facts["offsetof.SLayerBSInfo.uiTemporalId"] = &layerBitstream.TemporalId - origin;
        facts["offsetof.SLayerBSInfo.uiSpatialId"] = &layerBitstream.SpatialId - origin;
        facts["offsetof.SLayerBSInfo.uiQualityId"] = &layerBitstream.QualityId - origin;
        facts["offsetof.SLayerBSInfo.eFrameType"] = (byte*)&layerBitstream.FrameType - origin;
        facts["offsetof.SLayerBSInfo.uiLayerType"] = &layerBitstream.LayerType - origin;
        facts["offsetof.SLayerBSInfo.iSubSeqId"] = (byte*)&layerBitstream.SubSeqId - origin;
        facts["offsetof.SLayerBSInfo.iNalCount"] = (byte*)&layerBitstream.NalCount - origin;
        facts["offsetof.SLayerBSInfo.pNalLengthInByte"] = (byte*)&layerBitstream.NalLengthInByte - origin;
        facts["offsetof.SLayerBSInfo.pBsBuf"] = (byte*)&layerBitstream.BsBuf - origin;

        FrameBitstream frame;
        origin = (byte*)&frame;
        facts["sizeof.SFrameBSInfo"] = sizeof(FrameBitstream);
        facts["offsetof.SFrameBSInfo.iLayerNum"] = (byte*)&frame.LayerNum - origin;
        facts["offsetof.SFrameBSInfo.sLayerInfo"] = (byte*)&frame.LayerInfo - origin;
        facts["offsetof.SFrameBSInfo.eFrameType"] = (byte*)&frame.FrameType - origin;
        facts["offsetof.SFrameBSInfo.iFrameSizeInBytes"] = (byte*)&frame.FrameSizeInBytes - origin;
        facts["offsetof.SFrameBSInfo.uiTimeStamp"] = (byte*)&frame.TimeStamp - origin;

        SourcePicture picture;
        origin = (byte*)&picture;
        facts["sizeof.SSourcePicture"] = sizeof(SourcePicture);
        facts["offsetof.SSourcePicture.iColorFormat"] = (byte*)&picture.ColorFormat - origin;
        facts["offsetof.SSourcePicture.iStride"] = (byte*)picture.Stride - origin;
        facts["offsetof.SSourcePicture.pData"] = (byte*)&picture.Data0 - origin;
        facts["offsetof.SSourcePicture.iPicWidth"] = (byte*)&picture.PicWidth - origin;
        facts["offsetof.SSourcePicture.iPicHeight"] = (byte*)&picture.PicHeight - origin;
        facts["offsetof.SSourcePicture.uiTimeStamp"] = (byte*)&picture.TimeStamp - origin;

        // The four plane pointers are one array in C.
        Assert.Equal(sizeof(nint), (byte*)&picture.Data1 - (byte*)&picture.Data0);
        Assert.Equal(2 * sizeof(nint), (byte*)&picture.Data2 - (byte*)&picture.Data0);
        Assert.Equal(3 * sizeof(nint), (byte*)&picture.Data3 - (byte*)&picture.Data0);

        EncoderStatistics statistics;
        origin = (byte*)&statistics;
        facts["sizeof.SEncoderStatistics"] = sizeof(EncoderStatistics);
        facts["offsetof.SEncoderStatistics.uiWidth"] = (byte*)&statistics.Width - origin;
        facts["offsetof.SEncoderStatistics.uiHeight"] = (byte*)&statistics.Height - origin;
        facts["offsetof.SEncoderStatistics.fAverageFrameSpeedInMs"] = (byte*)&statistics.AverageFrameSpeedInMs - origin;
        facts["offsetof.SEncoderStatistics.fAverageFrameRate"] = (byte*)&statistics.AverageFrameRate - origin;
        facts["offsetof.SEncoderStatistics.fLatestFrameRate"] = (byte*)&statistics.LatestFrameRate - origin;
        facts["offsetof.SEncoderStatistics.uiBitRate"] = (byte*)&statistics.BitRate - origin;
        facts["offsetof.SEncoderStatistics.uiAverageFrameQP"] = (byte*)&statistics.AverageFrameQP - origin;
        facts["offsetof.SEncoderStatistics.uiInputFrameCount"] = (byte*)&statistics.InputFrameCount - origin;
        facts["offsetof.SEncoderStatistics.uiSkippedFrameCount"] = (byte*)&statistics.SkippedFrameCount - origin;
        facts["offsetof.SEncoderStatistics.uiResolutionChangeTimes"] = (byte*)&statistics.ResolutionChangeTimes - origin;
        facts["offsetof.SEncoderStatistics.uiIDRReqNum"] = (byte*)&statistics.IDRReqNum - origin;
        facts["offsetof.SEncoderStatistics.uiIDRSentNum"] = (byte*)&statistics.IDRSentNum - origin;
        facts["offsetof.SEncoderStatistics.uiLTRSentNum"] = (byte*)&statistics.LTRSentNum - origin;
        facts["offsetof.SEncoderStatistics.iStatisticsTs"] = (byte*)&statistics.StatisticsTs - origin;
        facts["offsetof.SEncoderStatistics.iTotalEncodedBytes"] = (byte*)&statistics.TotalEncodedBytes - origin;
        facts["offsetof.SEncoderStatistics.iLastStatisticsBytes"] = (byte*)&statistics.LastStatisticsBytes - origin;
        facts["offsetof.SEncoderStatistics.iLastStatisticsFrameCount"] = (byte*)&statistics.LastStatisticsFrameCount - origin;
        return facts;
    }

    private static Dictionary<string, string> Parse(string output)
    {
        var facts = new Dictionary<string, string>();
        foreach (var line in Lines(output))
        {
            var equals = line.IndexOf('=');
            Assert.True(equals > 0, "Not a name=value line: " + line);
            facts.Add(line[..equals], line[(equals + 1)..]);
        }

        return facts;
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
