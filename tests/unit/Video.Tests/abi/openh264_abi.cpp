// Prints what Matgate's C# code assumes about libopenh264's binary interface, read from the library's own
// headers by the compiler that built the library: struct sizes, field offsets, enum values, the slots of the
// encoder's vtable, and the defaults the library fills in. The test AbiTests compares the output line by line
// with the constants in Matgate/Display/Video/OpenH264.cs.
//
//   g++ -std=c++17 -O0 -o openh264_abi openh264_abi.cpp -lopenh264 && ./openh264_abi
//
// Needs libopenh264-dev (headers) and libopenh264-7 (the library the numbers are checked against at run time).
#include <wels/codec_api.h>
#include <wels/codec_ver.h>

#include <cstddef>
#include <cstdio>
#include <cstring>

#define SIZE(type) std::printf("sizeof." #type "=%zu\n", sizeof(type))
#define FIELD(type, field) std::printf("offsetof." #type "." #field "=%zu\n", offsetof(type, field))
#define ENUM(name) std::printf("enum." #name "=%lld\n", (long long)(name))
#define DEFAULT(name, value) std::printf("default." name "=%lld\n", (long long)(value))

// Every virtual method answers with its own number, so calling slot N of the vtable tells which method lives
// there - for the compiler and C++ ABI the library was built with.
struct Spy : ISVCEncoder
{
    int EXTAPI Initialize(const SEncParamBase*) override { return 100; }
    int EXTAPI InitializeExt(const SEncParamExt*) override { return 101; }
    int EXTAPI GetDefaultParams(SEncParamExt*) override { return 102; }
    int EXTAPI Uninitialize() override { return 103; }
    int EXTAPI EncodeFrame(const SSourcePicture*, SFrameBSInfo*) override { return 104; }
    int EXTAPI EncodeParameterSets(SFrameBSInfo*) override { return 105; }
    int EXTAPI ForceIntraFrame(bool, int) override { return 106; }
    int EXTAPI SetOption(ENCODER_OPTION, void*) override { return 107; }
    int EXTAPI GetOption(ENCODER_OPTION, void*) override { return 108; }
};

typedef int (*RawMethod)(void* self, void* first, void* second);

static void** vtable_of(void* object)
{
    return *reinterpret_cast<void***>(object);
}

static void print_vtable()
{
    static const char* const names[] =
    {
        "Initialize", "InitializeExt", "GetDefaultParams", "Uninitialize", "EncodeFrame",
        "EncodeParameterSets", "ForceIntraFrame", "SetOption", "GetOption",
    };
    Spy spy;
    void** vtable = vtable_of(&spy);
    for (int slot = 0; slot < 9; slot++)
    {
        int answer = reinterpret_cast<RawMethod>(vtable[slot])(&spy, nullptr, nullptr);
        if (answer < 100 || answer > 108)
        {
            std::printf("vtable.slot%d=unknown\n", slot);
            continue;
        }

        std::printf("vtable.%s=%d\n", names[answer - 100], slot);
    }
}

// The same slots in the object the library hands out: GetDefaultParams through the raw slot must fill the
// struct exactly as the ordinary C++ call does, and SetOption through the raw slot must accept a trace level.
static int check_library_object()
{
    ISVCEncoder* encoder = nullptr;
    if (WelsCreateSVCEncoder(&encoder) != 0 || encoder == nullptr)
    {
        std::printf("library.create=failed\n");
        return 1;
    }

    SEncParamExt direct;
    SEncParamExt raw;
    std::memset(&direct, 0xAA, sizeof(direct));
    std::memset(&raw, 0x55, sizeof(raw));
    encoder->GetDefaultParams(&direct);
    void** vtable = vtable_of(encoder);
    int result = reinterpret_cast<int (*)(void*, SEncParamExt*)>(vtable[2])(encoder, &raw);
    std::printf("library.vtable.GetDefaultParams=%d\n", result == 0 && std::memcmp(&direct, &raw, sizeof(direct)) == 0 ? 2 : -1);

    int quiet = WELS_LOG_QUIET;
    result = reinterpret_cast<int (*)(void*, int, void*)>(vtable[7])(encoder, ENCODER_OPTION_TRACE_LEVEL, &quiet);
    std::printf("library.vtable.SetOption=%d\n", result == 0 ? 7 : -1);

    // Before Initialize every option but the trace ones answers "initial action is expected".
    int value = 0;
    result = reinterpret_cast<int (*)(void*, int, void*)>(vtable[8])(encoder, ENCODER_OPTION_DATAFORMAT, &value);
    std::printf("library.vtable.GetOption=%d\n", result == cmInitExpected ? 8 : -1);
    result = reinterpret_cast<int (*)(void*)>(vtable[3])(encoder);
    std::printf("library.vtable.Uninitialize=%d\n", result == 0 ? 3 : -1);

    // What the library fills in: the C# side checks a few of these at start-up to be sure it talks to a
    // library with this very layout.
    DEFAULT("SEncParamExt.iUsageType", direct.iUsageType);
    DEFAULT("SEncParamExt.iPicWidth", direct.iPicWidth);
    DEFAULT("SEncParamExt.iTargetBitrate", direct.iTargetBitrate);
    DEFAULT("SEncParamExt.iRCMode", direct.iRCMode);
    DEFAULT("SEncParamExt.fMaxFrameRate", direct.fMaxFrameRate);
    DEFAULT("SEncParamExt.iTemporalLayerNum", direct.iTemporalLayerNum);
    DEFAULT("SEncParamExt.iSpatialLayerNum", direct.iSpatialLayerNum);
    DEFAULT("SEncParamExt.sSpatialLayers0.iDLayerQp", direct.sSpatialLayers[0].iDLayerQp);
    DEFAULT("SEncParamExt.sSpatialLayers0.uiProfileIdc", direct.sSpatialLayers[0].uiProfileIdc);
    DEFAULT("SEncParamExt.sSpatialLayers0.uiLevelIdc", direct.sSpatialLayers[0].uiLevelIdc);
    DEFAULT("SEncParamExt.sSpatialLayers0.uiSliceMode", direct.sSpatialLayers[0].sSliceArgument.uiSliceMode);
    DEFAULT("SEncParamExt.sSpatialLayers0.uiSliceNum", direct.sSpatialLayers[0].sSliceArgument.uiSliceNum);
    DEFAULT("SEncParamExt.sSpatialLayers0.uiVideoFormat", direct.sSpatialLayers[0].uiVideoFormat);
    DEFAULT("SEncParamExt.sSpatialLayers0.uiColorMatrix", direct.sSpatialLayers[0].uiColorMatrix);
    DEFAULT("SEncParamExt.sSpatialLayers3.uiSliceSizeConstraint", direct.sSpatialLayers[3].sSliceArgument.uiSliceSizeConstraint);
    DEFAULT("SEncParamExt.iComplexityMode", direct.iComplexityMode);
    DEFAULT("SEncParamExt.uiIntraPeriod", direct.uiIntraPeriod);
    DEFAULT("SEncParamExt.iNumRefFrame", direct.iNumRefFrame);
    DEFAULT("SEncParamExt.eSpsPpsIdStrategy", direct.eSpsPpsIdStrategy);
    DEFAULT("SEncParamExt.iEntropyCodingModeFlag", direct.iEntropyCodingModeFlag);
    DEFAULT("SEncParamExt.bEnableFrameSkip", direct.bEnableFrameSkip);
    DEFAULT("SEncParamExt.iMaxBitrate", direct.iMaxBitrate);
    DEFAULT("SEncParamExt.iMaxQp", direct.iMaxQp);
    DEFAULT("SEncParamExt.iMinQp", direct.iMinQp);
    DEFAULT("SEncParamExt.bEnableLongTermReference", direct.bEnableLongTermReference);
    DEFAULT("SEncParamExt.iLtrMarkPeriod", direct.iLtrMarkPeriod);
    DEFAULT("SEncParamExt.iMultipleThreadIdc", direct.iMultipleThreadIdc);
    DEFAULT("SEncParamExt.bUseLoadBalancing", direct.bUseLoadBalancing);
    DEFAULT("SEncParamExt.iLoopFilterDisableIdc", direct.iLoopFilterDisableIdc);
    DEFAULT("SEncParamExt.bEnableDenoise", direct.bEnableDenoise);
    DEFAULT("SEncParamExt.bEnableBackgroundDetection", direct.bEnableBackgroundDetection);
    DEFAULT("SEncParamExt.bEnableAdaptiveQuant", direct.bEnableAdaptiveQuant);
    DEFAULT("SEncParamExt.bEnableFrameCroppingFlag", direct.bEnableFrameCroppingFlag);
    DEFAULT("SEncParamExt.bEnableSceneChangeDetect", direct.bEnableSceneChangeDetect);
    DEFAULT("SEncParamExt.bIsLosslessLink", direct.bIsLosslessLink);
    DEFAULT("SEncParamExt.bFixRCOverShoot", direct.bFixRCOverShoot);
    DEFAULT("SEncParamExt.iIdrBitrateRatio", direct.iIdrBitrateRatio);
    WelsDestroySVCEncoder(encoder);
    return 0;
}

int main()
{
    OpenH264Version version;
    std::memset(&version, 0, sizeof(version));
    WelsGetCodecVersionEx(&version);
    std::printf("header.version=%d.%d.%d\n", OPENH264_MAJOR, OPENH264_MINOR, OPENH264_REVISION);
    std::printf("library.version=%u.%u.%u\n", version.uMajor, version.uMinor, version.uRevision);

    SIZE(void*);
    SIZE(bool);
    SIZE(int);
    SIZE(long);
    SIZE(EUsageType);

    SIZE(OpenH264Version);
    FIELD(OpenH264Version, uMajor);
    FIELD(OpenH264Version, uMinor);
    FIELD(OpenH264Version, uRevision);
    FIELD(OpenH264Version, uReserved);

    SIZE(SSliceArgument);
    FIELD(SSliceArgument, uiSliceMode);
    FIELD(SSliceArgument, uiSliceNum);
    FIELD(SSliceArgument, uiSliceMbNum);
    FIELD(SSliceArgument, uiSliceSizeConstraint);

    SIZE(SSpatialLayerConfig);
    FIELD(SSpatialLayerConfig, iVideoWidth);
    FIELD(SSpatialLayerConfig, iVideoHeight);
    FIELD(SSpatialLayerConfig, fFrameRate);
    FIELD(SSpatialLayerConfig, iSpatialBitrate);
    FIELD(SSpatialLayerConfig, iMaxSpatialBitrate);
    FIELD(SSpatialLayerConfig, uiProfileIdc);
    FIELD(SSpatialLayerConfig, uiLevelIdc);
    FIELD(SSpatialLayerConfig, iDLayerQp);
    FIELD(SSpatialLayerConfig, sSliceArgument);
    FIELD(SSpatialLayerConfig, bVideoSignalTypePresent);
    FIELD(SSpatialLayerConfig, uiVideoFormat);
    FIELD(SSpatialLayerConfig, bFullRange);
    FIELD(SSpatialLayerConfig, bColorDescriptionPresent);
    FIELD(SSpatialLayerConfig, uiColorPrimaries);
    FIELD(SSpatialLayerConfig, uiTransferCharacteristics);
    FIELD(SSpatialLayerConfig, uiColorMatrix);
    FIELD(SSpatialLayerConfig, bAspectRatioPresent);
    FIELD(SSpatialLayerConfig, eAspectRatio);
    FIELD(SSpatialLayerConfig, sAspectRatioExtWidth);
    FIELD(SSpatialLayerConfig, sAspectRatioExtHeight);

    SIZE(SEncParamExt);
    FIELD(SEncParamExt, iUsageType);
    FIELD(SEncParamExt, iPicWidth);
    FIELD(SEncParamExt, iPicHeight);
    FIELD(SEncParamExt, iTargetBitrate);
    FIELD(SEncParamExt, iRCMode);
    FIELD(SEncParamExt, fMaxFrameRate);
    FIELD(SEncParamExt, iTemporalLayerNum);
    FIELD(SEncParamExt, iSpatialLayerNum);
    FIELD(SEncParamExt, sSpatialLayers);
    FIELD(SEncParamExt, iComplexityMode);
    FIELD(SEncParamExt, uiIntraPeriod);
    FIELD(SEncParamExt, iNumRefFrame);
    FIELD(SEncParamExt, eSpsPpsIdStrategy);
    FIELD(SEncParamExt, bPrefixNalAddingCtrl);
    FIELD(SEncParamExt, bEnableSSEI);
    FIELD(SEncParamExt, bSimulcastAVC);
    FIELD(SEncParamExt, iPaddingFlag);
    FIELD(SEncParamExt, iEntropyCodingModeFlag);
    FIELD(SEncParamExt, bEnableFrameSkip);
    FIELD(SEncParamExt, iMaxBitrate);
    FIELD(SEncParamExt, iMaxQp);
    FIELD(SEncParamExt, iMinQp);
    FIELD(SEncParamExt, uiMaxNalSize);
    FIELD(SEncParamExt, bEnableLongTermReference);
    FIELD(SEncParamExt, iLTRRefNum);
    FIELD(SEncParamExt, iLtrMarkPeriod);
    FIELD(SEncParamExt, iMultipleThreadIdc);
    FIELD(SEncParamExt, bUseLoadBalancing);
    FIELD(SEncParamExt, iLoopFilterDisableIdc);
    FIELD(SEncParamExt, iLoopFilterAlphaC0Offset);
    FIELD(SEncParamExt, iLoopFilterBetaOffset);
    FIELD(SEncParamExt, bEnableDenoise);
    FIELD(SEncParamExt, bEnableBackgroundDetection);
    FIELD(SEncParamExt, bEnableAdaptiveQuant);
    FIELD(SEncParamExt, bEnableFrameCroppingFlag);
    FIELD(SEncParamExt, bEnableSceneChangeDetect);
    FIELD(SEncParamExt, bIsLosslessLink);
    FIELD(SEncParamExt, bFixRCOverShoot);
    FIELD(SEncParamExt, iIdrBitrateRatio);

    SIZE(SLayerBSInfo);
    FIELD(SLayerBSInfo, uiTemporalId);
    FIELD(SLayerBSInfo, uiSpatialId);
    FIELD(SLayerBSInfo, uiQualityId);
    FIELD(SLayerBSInfo, eFrameType);
    FIELD(SLayerBSInfo, uiLayerType);
    FIELD(SLayerBSInfo, iSubSeqId);
    FIELD(SLayerBSInfo, iNalCount);
    FIELD(SLayerBSInfo, pNalLengthInByte);
    FIELD(SLayerBSInfo, pBsBuf);

    SIZE(SFrameBSInfo);
    FIELD(SFrameBSInfo, iLayerNum);
    FIELD(SFrameBSInfo, sLayerInfo);
    FIELD(SFrameBSInfo, eFrameType);
    FIELD(SFrameBSInfo, iFrameSizeInBytes);
    FIELD(SFrameBSInfo, uiTimeStamp);

    SIZE(SSourcePicture);
    FIELD(SSourcePicture, iColorFormat);
    FIELD(SSourcePicture, iStride);
    FIELD(SSourcePicture, pData);
    FIELD(SSourcePicture, iPicWidth);
    FIELD(SSourcePicture, iPicHeight);
    FIELD(SSourcePicture, uiTimeStamp);

    SIZE(SEncoderStatistics);
    FIELD(SEncoderStatistics, uiWidth);
    FIELD(SEncoderStatistics, uiHeight);
    FIELD(SEncoderStatistics, fAverageFrameSpeedInMs);
    FIELD(SEncoderStatistics, fAverageFrameRate);
    FIELD(SEncoderStatistics, fLatestFrameRate);
    FIELD(SEncoderStatistics, uiBitRate);
    FIELD(SEncoderStatistics, uiAverageFrameQP);
    FIELD(SEncoderStatistics, uiInputFrameCount);
    FIELD(SEncoderStatistics, uiSkippedFrameCount);
    FIELD(SEncoderStatistics, uiResolutionChangeTimes);
    FIELD(SEncoderStatistics, uiIDRReqNum);
    FIELD(SEncoderStatistics, uiIDRSentNum);
    FIELD(SEncoderStatistics, uiLTRSentNum);
    FIELD(SEncoderStatistics, iStatisticsTs);
    FIELD(SEncoderStatistics, iTotalEncodedBytes);
    FIELD(SEncoderStatistics, iLastStatisticsBytes);
    FIELD(SEncoderStatistics, iLastStatisticsFrameCount);

    ENUM(MAX_SPATIAL_LAYER_NUM);
    ENUM(MAX_LAYER_NUM_OF_FRAME);
    ENUM(MAX_SLICES_NUM_TMP);
    ENUM(AUTO_REF_PIC_COUNT);
    ENUM(UNSPECIFIED_BIT_RATE);

    ENUM(cmResultSuccess);
    ENUM(cmInitParaError);
    ENUM(cmUnknownReason);
    ENUM(cmMallocMemeError);
    ENUM(cmInitExpected);
    ENUM(cmUnsupportedData);

    ENUM(CAMERA_VIDEO_REAL_TIME);
    ENUM(SCREEN_CONTENT_REAL_TIME);

    ENUM(RC_QUALITY_MODE);
    ENUM(RC_BITRATE_MODE);
    ENUM(RC_BUFFERBASED_MODE);
    ENUM(RC_TIMESTAMP_MODE);
    ENUM(RC_OFF_MODE);

    ENUM(LOW_COMPLEXITY);
    ENUM(MEDIUM_COMPLEXITY);
    ENUM(HIGH_COMPLEXITY);

    ENUM(CONSTANT_ID);
    ENUM(INCREASING_ID);

    ENUM(SM_SINGLE_SLICE);
    ENUM(SM_FIXEDSLCNUM_SLICE);

    ENUM(PRO_UNKNOWN);
    ENUM(PRO_BASELINE);
    ENUM(PRO_MAIN);
    ENUM(PRO_HIGH);
    ENUM(LEVEL_UNKNOWN);

    ENUM(videoFormatI420);

    ENUM(videoFrameTypeInvalid);
    ENUM(videoFrameTypeIDR);
    ENUM(videoFrameTypeI);
    ENUM(videoFrameTypeP);
    ENUM(videoFrameTypeSkip);
    ENUM(videoFrameTypeIPMixed);

    ENUM(NON_VIDEO_CODING_LAYER);
    ENUM(VIDEO_CODING_LAYER);

    ENUM(VF_UNDEF);
    ENUM(CP_BT709);
    ENUM(CP_UNDEF);
    ENUM(TRC_BT709);
    ENUM(TRC_UNDEF);
    ENUM(TRC_IEC61966_2_1);
    ENUM(CM_UNDEF);
    ENUM(CM_SMPTE170M);

    ENUM(WELS_LOG_QUIET);
    ENUM(WELS_LOG_ERROR);
    ENUM(WELS_LOG_WARNING);
    ENUM(WELS_LOG_INFO);
    ENUM(WELS_LOG_DEBUG);
    ENUM(WELS_LOG_DETAIL);

    ENUM(ENCODER_OPTION_DATAFORMAT);
    ENUM(ENCODER_OPTION_IDR_INTERVAL);
    ENUM(ENCODER_OPTION_SVC_ENCODE_PARAM_BASE);
    ENUM(ENCODER_OPTION_SVC_ENCODE_PARAM_EXT);
    ENUM(ENCODER_OPTION_FRAME_RATE);
    ENUM(ENCODER_OPTION_BITRATE);
    ENUM(ENCODER_OPTION_MAX_BITRATE);
    ENUM(ENCODER_OPTION_RC_MODE);
    ENUM(ENCODER_OPTION_RC_FRAME_SKIP);
    ENUM(ENCODER_OPTION_COMPLEXITY);
    ENUM(ENCODER_OPTION_TRACE_LEVEL);
    ENUM(ENCODER_OPTION_TRACE_CALLBACK);
    ENUM(ENCODER_OPTION_TRACE_CALLBACK_CONTEXT);
    ENUM(ENCODER_OPTION_GET_STATISTICS);
    ENUM(ENCODER_OPTION_BITS_VARY_PERCENTAGE);

    print_vtable();
    return check_library_object();
}
