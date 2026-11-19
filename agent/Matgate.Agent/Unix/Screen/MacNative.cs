using System.Runtime.InteropServices;

namespace Matgate.Agent.Unix.Screen;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X;
    public double Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X;
    public double Y;
    public double Width;
    public double Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CMTime
{
    public long Value;
    public int Timescale;
    public uint Flags;
    public long Epoch;
}

// What the screen uses of macOS: CoreGraphics (the monitors, their picture, mouse and keys), VideoToolbox
// with CoreMedia and CoreVideo (H.264 from the machine's own encoder), and the Objective-C runtime for the
// pasteboard.
internal static unsafe class Mac
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string VideoToolbox = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    public const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";

    // A constant a framework exports as a variable (a CFString, a CFBoolean): its value.
    public static nint Constant(string library, string name) =>
        NativeLibrary.TryLoad(library, out var handle) && NativeLibrary.TryGetExport(handle, name, out var address) ? *(nint*)address : 0;

    // A structure a framework exports: its address.
    public static nint Address(string library, string name) =>
        NativeLibrary.TryLoad(library, out var handle) && NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0;

    public static nint VideoConstant(string name) => Constant(VideoToolbox, name);

    public static nint True => Constant(CoreFoundation, "kCFBooleanTrue");

    public static nint False => Constant(CoreFoundation, "kCFBooleanFalse");

    public static nint Number(int value) => CFNumberCreate(0, 3, &value);

    public static nint Dictionary(nint key, nint value) =>
        CFDictionaryCreate(0, &key, &value, 1, Address(CoreFoundation, "kCFTypeDictionaryKeyCallBacks"), Address(CoreFoundation, "kCFTypeDictionaryValueCallBacks"));

    // ---- monitors and their picture ------------------------------------------------------------------------

    [DllImport(CoreGraphics)]
    public static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    public static extern int CGGetActiveDisplayList(uint most, uint* displays, out uint count);

    [DllImport(CoreGraphics)]
    public static extern CGRect CGDisplayBounds(uint display);

    [DllImport(CoreGraphics)]
    public static extern nint CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    public static extern nuint CGDisplayModeGetPixelWidth(nint mode);

    [DllImport(CoreGraphics)]
    public static extern nuint CGDisplayModeGetPixelHeight(nint mode);

    [DllImport(CoreGraphics)]
    public static extern void CGDisplayModeRelease(nint mode);

    [DllImport(CoreGraphics)]
    public static extern nint CGDisplayCreateImage(uint display);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetWidth(nint image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetHeight(nint image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetBytesPerRow(nint image);

    [DllImport(CoreGraphics)]
    public static extern nuint CGImageGetBitsPerPixel(nint image);

    [DllImport(CoreGraphics)]
    public static extern nint CGImageGetDataProvider(nint image);

    [DllImport(CoreGraphics)]
    public static extern nint CGDataProviderCopyData(nint provider);

    [DllImport(CoreGraphics)]
    public static extern byte CGPreflightScreenCaptureAccess();

    [DllImport(CoreGraphics)]
    public static extern byte CGRequestScreenCaptureAccess();

    [DllImport(ApplicationServices)]
    public static extern byte AXIsProcessTrusted();

    // ---- mouse and keys ------------------------------------------------------------------------------------

    [DllImport(CoreGraphics)]
    public static extern nint CGEventCreate(nint source);

    [DllImport(CoreGraphics)]
    public static extern CGPoint CGEventGetLocation(nint found);

    [DllImport(CoreGraphics)]
    public static extern nint CGEventCreateMouseEvent(nint source, uint type, CGPoint position, uint button);

    [DllImport(CoreGraphics)]
    public static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, byte down);

    [DllImport(CoreGraphics)]
    public static extern nint CGEventCreateScrollWheelEvent2(nint source, uint units, uint wheels, int first, int second, int third);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetFlags(nint sent, ulong flags);

    [DllImport(CoreGraphics)]
    public static extern void CGEventSetIntegerValueField(nint sent, uint field, long value);

    [DllImport(CoreGraphics)]
    public static extern void CGEventKeyboardSetUnicodeString(nint sent, nuint length, char* text);

    [DllImport(CoreGraphics)]
    public static extern void CGEventPost(uint tap, nint sent);

    // ---- CoreFoundation --------------------------------------------------------------------------------------

    [DllImport(CoreFoundation)]
    public static extern void CFRelease(nint value);

    [DllImport(CoreFoundation)]
    public static extern nint CFDataGetBytePtr(nint data);

    [DllImport(CoreFoundation)]
    public static extern nint CFDataGetLength(nint data);

    [DllImport(CoreFoundation)]
    private static extern nint CFNumberCreate(nint allocator, nint type, int* value);

    [DllImport(CoreFoundation)]
    private static extern nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallbacks, nint valueCallbacks);

    // ---- the encoder -----------------------------------------------------------------------------------------

    [DllImport(VideoToolbox)]
    public static extern int VTCompressionSessionCreate(nint allocator, int width, int height, uint codec, nint specification, nint sourceAttributes, nint dataAllocator, delegate* unmanaged[Cdecl]<nint, nint, int, uint, nint, void> output, nint context, out nint session);

    [DllImport(VideoToolbox)]
    public static extern int VTSessionSetProperty(nint session, nint key, nint value);

    [DllImport(VideoToolbox)]
    public static extern int VTCompressionSessionPrepareToEncodeFrames(nint session);

    [DllImport(VideoToolbox)]
    public static extern int VTCompressionSessionEncodeFrame(nint session, nint picture, CMTime time, CMTime duration, nint properties, nint frameContext, out uint flags);

    [DllImport(VideoToolbox)]
    public static extern int VTCompressionSessionCompleteFrames(nint session, CMTime until);

    [DllImport(VideoToolbox)]
    public static extern void VTCompressionSessionInvalidate(nint session);

    [DllImport(CoreVideo)]
    public static extern int CVPixelBufferCreate(nint allocator, nuint width, nuint height, uint format, nint attributes, out nint buffer);

    [DllImport(CoreVideo)]
    public static extern int CVPixelBufferLockBaseAddress(nint buffer, ulong flags);

    [DllImport(CoreVideo)]
    public static extern int CVPixelBufferUnlockBaseAddress(nint buffer, ulong flags);

    [DllImport(CoreVideo)]
    public static extern nint CVPixelBufferGetBaseAddressOfPlane(nint buffer, nuint plane);

    [DllImport(CoreVideo)]
    public static extern nuint CVPixelBufferGetBytesPerRowOfPlane(nint buffer, nuint plane);

    [DllImport(CoreMedia)]
    public static extern nint CMSampleBufferGetDataBuffer(nint sample);

    [DllImport(CoreMedia)]
    public static extern nint CMSampleBufferGetFormatDescription(nint sample);

    [DllImport(CoreMedia)]
    public static extern nuint CMBlockBufferGetDataLength(nint block);

    [DllImport(CoreMedia)]
    public static extern int CMBlockBufferCopyDataBytes(nint block, nuint offset, nuint length, byte* target);

    [DllImport(CoreMedia)]
    public static extern int CMVideoFormatDescriptionGetH264ParameterSetAtIndex(nint description, nuint index, out nint data, out nuint size, out nuint count, out int headerLength);

    // ---- Objective-C (the pasteboard) --------------------------------------------------------------------------

    [DllImport(ObjC)]
    public static extern nint objc_getClass(string name);

    [DllImport(ObjC)]
    public static extern nint sel_registerName(string name);

    [DllImport(ObjC)]
    public static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    public static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, nint first);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern nint Send(nint receiver, nint selector, nint first, nint second);

    [DllImport("libc", EntryPoint = "stat")]
    private static extern int Stat(string path, byte* description);

    // Who owns the console: the person signed in at the screen - root while the sign-in window is shown.
    public static int ConsoleUser()
    {
        var description = stackalloc byte[512];
        if (Stat("/dev/console", description) != 0)
        {
            return -1;
        }

        // The owner's place in struct stat: after device, mode, links and a 64-bit file number on Apple's
        // processors; after device, a 32-bit file number, mode and links in the old layout "stat" still has on Intel.
        return *(int*)(description + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 16 : 12));
    }
}
