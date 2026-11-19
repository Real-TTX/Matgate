using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Matgate.Agent.Screen;

// Game controllers of the person in front of the browser, as controllers of this machine.
//
// Windows has no way for a program to pretend to be a controller - that takes a driver. The one everybody
// uses for it is ViGEmBus (https://github.com/nefarius/ViGEmBus, the virtual gamepad bus behind most
// remote-play tools): where it is installed, each controller of the browser becomes an Xbox 360
// controller here, which is what every game understands. Where it is not, there are no controllers and
// the page says why.
//
// The driver is spoken to directly (its interface is public and small: plug in, send a report, unplug) -
// no native library to ship beside the agent.
public interface IGamepads : IDisposable
{
    // The state of controller `index` (0-3), in the terms of an Xbox controller.
    void Update(int index, ushort buttons, byte leftTrigger, byte rightTrigger, short leftX, short leftY, short rightX, short rightY);

    // The controller was taken away (unplugged in the browser, or the viewer left).
    void Remove(int index);
}

// For the tests: what would be done, said instead. And since no game is there to make a controller rumble, the
// two triggers do: what they are pressed to comes back as the two motors.
public sealed class RecordingGamepads(Action<string> report, Action<int, byte, byte>? rumble = null) : IGamepads
{
    private readonly HashSet<int> _plugged = [];
    private readonly Dictionary<int, (byte Large, byte Small)> _motors = [];

    public void Update(int index, ushort buttons, byte leftTrigger, byte rightTrigger, short leftX, short leftY, short rightX, short rightY)
    {
        if (_plugged.Add(index))
        {
            report($"pad {index} plugged");
        }

        report($"pad {index} buttons={buttons:x4} lt={leftTrigger} rt={rightTrigger} l={leftX},{leftY} r={rightX},{rightY}");
        if (rumble is not null && _motors.GetValueOrDefault(index) != (leftTrigger, rightTrigger))
        {
            _motors[index] = (leftTrigger, rightTrigger);
            rumble(index, leftTrigger, rightTrigger);
        }
    }

    public void Remove(int index)
    {
        if (_plugged.Remove(index))
        {
            report($"pad {index} unplugged");
        }
    }

    public void Dispose()
    {
        foreach (var index in _plugged.ToList())
        {
            Remove(index);
        }
    }
}

#if AGENT_WINDOWS
public sealed class ViGEmGamepads : IGamepads
{
    public const int MaxPads = 4;

    private const uint CommonVersion = 0x0001;
    private const uint Xbox360Wired = 0;
    private const ushort MicrosoftVendor = 0x045E;
    private const ushort Xbox360Product = 0x028E;

    // CTL_CODE(FILE_DEVICE_BUS_EXTENDER, 0x801 + n, METHOD_BUFFERED, access) - as in ViGEm's BusShared.h.
    private static uint Code(int function, uint access) => (0x2Au << 16) | (access << 14) | ((uint)(0x801 + function) << 2);

    private static readonly uint PlugIn = Code(0x000, 2);
    private static readonly uint Unplug = Code(0x001, 2);
    private static readonly uint CheckVersion = Code(0x002, 2);
    private static readonly uint WaitReady = Code(0x003, 2);
    private static readonly uint Notification = Code(0x200, 3);
    private static readonly uint SubmitReport = Code(0x201, 2);

    private static readonly Guid BusInterface = new("96E42B22-F5E9-42F8-B043-ED0F932F014F");

    private readonly object _gate = new();
    private readonly SafeFileHandle _bus;
    private readonly uint[] _serial = new uint[MaxPads];
    private readonly Action<int, byte, byte>? _rumble;
    private volatile bool _disposed;

    // rumble: (controller, large motor, small motor), 0-255 each - what a game makes the controller do.
    public ViGEmGamepads(Action<int, byte, byte>? rumble = null)
    {
        _rumble = rumble;
        _bus = Open() ?? throw new InvalidOperationException("The virtual gamepad driver (ViGEmBus) is not installed on this machine.");
    }

    // Whether the driver is there. Asked rarely (when Matgate connects, when a screen opens).
    public static bool Installed
    {
        get
        {
            try
            {
                using var bus = Open();
                return bus is not null;
            }
            catch (Exception exception) when (exception is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public void Update(int index, ushort buttons, byte leftTrigger, byte rightTrigger, short leftX, short leftY, short rightX, short rightY)
    {
        if (index is < 0 or >= MaxPads || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            if (_serial[index] == 0 && !Plug(index))
            {
                return;
            }

            // XUSB_SUBMIT_REPORT: Size, SerialNo, then the report as XInput has it.
            Span<byte> report = stackalloc byte[20];
            BitConverter.TryWriteBytes(report, 20u);
            BitConverter.TryWriteBytes(report[4..], _serial[index]);
            BitConverter.TryWriteBytes(report[8..], buttons);
            report[10] = leftTrigger;
            report[11] = rightTrigger;
            BitConverter.TryWriteBytes(report[12..], leftX);
            BitConverter.TryWriteBytes(report[14..], leftY);
            BitConverter.TryWriteBytes(report[16..], rightX);
            BitConverter.TryWriteBytes(report[18..], rightY);
            Control(SubmitReport, report, default, out _);
        }
    }

    public void Remove(int index)
    {
        if (index is < 0 or >= MaxPads)
        {
            return;
        }

        lock (_gate)
        {
            var serial = _serial[index];
            if (serial == 0)
            {
                return;
            }

            _serial[index] = 0;
            Span<byte> request = stackalloc byte[8];
            BitConverter.TryWriteBytes(request, 8u);
            BitConverter.TryWriteBytes(request[4..], serial);
            Control(Unplug, request, default, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var index = 0; index < MaxPads; index++)
        {
            Remove(index);
        }

        _bus.Dispose();
    }

    // Plugs a new Xbox 360 controller into the bus. The bus knows its devices by a serial number the
    // client picks; one that is taken (another program's controller) is refused, so the next is tried.
    private bool Plug(int index)
    {
        Span<byte> request = stackalloc byte[16];
        for (uint serial = 1; serial <= 64; serial++)
        {
            request.Clear();
            BitConverter.TryWriteBytes(request, 16u);
            BitConverter.TryWriteBytes(request[4..], serial);
            BitConverter.TryWriteBytes(request[8..], Xbox360Wired);
            BitConverter.TryWriteBytes(request[12..], MicrosoftVendor);
            BitConverter.TryWriteBytes(request[14..], Xbox360Product);
            if (!Control(PlugIn, request, default, out _))
            {
                continue;
            }

            // Kept pending by the driver until Windows has brought the device up (ViGEmBus 1.17 and later;
            // an older one answers "invalid parameter", and the device is there all the same).
            Span<byte> ready = stackalloc byte[8];
            BitConverter.TryWriteBytes(ready, 8u);
            BitConverter.TryWriteBytes(ready[4..], serial);
            if (!Control(WaitReady, ready, default, out var error) && error != 87)
            {
                Span<byte> undo = stackalloc byte[8];
                BitConverter.TryWriteBytes(undo, 8u);
                BitConverter.TryWriteBytes(undo[4..], serial);
                Control(Unplug, undo, default, out _);
                AgentLog.Write($"Controller {index + 1}: the virtual gamepad did not come up (error {error}).");
                return false;
            }

            _serial[index] = serial;
            AgentLog.Write($"Controller {index + 1} plugged in (virtual Xbox 360 controller).");
            if (_rumble is not null)
            {
                var thread = new Thread(() => ListenForRumble(index, serial)) { IsBackground = true, Name = "Matgate controller " + (index + 1) };
                thread.Start();
            }

            return true;
        }

        AgentLog.Write($"Controller {index + 1}: the virtual gamepad bus took no new device.");
        return false;
    }

    // A game makes the controller rumble: the driver answers a waiting request with the two motors.
    private void ListenForRumble(int index, uint serial)
    {
        var request = new byte[12];
        var answer = new byte[12];
        byte lastLarge = 0, lastSmall = 0;
        while (!_disposed && _serial[index] == serial)
        {
            Array.Clear(request);
            BitConverter.TryWriteBytes(request, 12u);
            BitConverter.TryWriteBytes(request.AsSpan(4), serial);
            if (!Control(Notification, request, answer, out _))
            {
                // Unplugged, or the bus is gone.
                return;
            }

            var large = answer[8];
            var small = answer[9];
            if (large != lastLarge || small != lastSmall)
            {
                lastLarge = large;
                lastSmall = small;
                try
                {
                    _rumble?.Invoke(index, large, small);
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }

    // One request to the driver, waited for. The bus is opened for overlapped I/O (as its own client
    // does), so a request that stays pending - the wait for rumble - does not hold up the reports.
    private unsafe bool Control(uint code, ReadOnlySpan<byte> input, Span<byte> output, out int error)
    {
        error = 0;
        if (_bus.IsInvalid || _bus.IsClosed)
        {
            error = 6;
            return false;
        }

        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* inPointer = input)
        fixed (byte* outPointer = output)
        {
            try
            {
                if (!DeviceIoControl(_bus, code, inPointer, (uint)input.Length, outPointer, (uint)output.Length, out _, &overlapped))
                {
                    error = Marshal.GetLastWin32Error();
                    if (error != 997)
                    {
                        return false;
                    }
                }

                if (!GetOverlappedResult(_bus, &overlapped, out _, true))
                {
                    error = Marshal.GetLastWin32Error();
                    return false;
                }

                error = 0;
                return true;
            }
            catch (ObjectDisposedException)
            {
                error = 6;
                return false;
            }
        }
    }

    private static SafeFileHandle? Open()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var guid = BusInterface;
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12 /* present | device interface */);
        if (set == new IntPtr(-1))
        {
            return null;
        }

        try
        {
            var data = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
            for (uint member = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, member, ref data); member++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
                if (needed == 0)
                {
                    continue;
                }

                var detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: its size field counts the fixed part only.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(detail + 4);
                    var handle = CreateFile(path!, 0xC0000000 /* read | write */, 3, IntPtr.Zero, 3 /* open existing */,
                        0x80 | 0x20000000 | 0x80000000 | 0x40000000 /* normal | no buffering | write through | overlapped */, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        continue;
                    }

                    if (VersionFits(handle))
                    {
                        return handle;
                    }

                    handle.Dispose();
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return null;
    }

    private static unsafe bool VersionFits(SafeFileHandle bus)
    {
        Span<byte> request = stackalloc byte[8];
        BitConverter.TryWriteBytes(request, 8u);
        BitConverter.TryWriteBytes(request[4..], CommonVersion);
        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* pointer = request)
        {
            if (!DeviceIoControl(bus, CheckVersion, pointer, 8, null, 0, out _, &overlapped) && Marshal.GetLastWin32Error() != 997)
            {
                return false;
            }

            return GetOverlappedResult(bus, &overlapped, out _, true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClass;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceClass, uint member, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint size, out uint needed, IntPtr deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, uint inputSize, byte* output, uint outputSize, out uint returned, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
#endif
