using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Matgate.Agent;

// A program in a Windows pseudo console (ConPTY): what a terminal needs - raw bytes in, VT sequences
// out, a size that can change. Programs like Claude Code draw their whole interface with it.
[SupportedOSPlatform("windows")]
public sealed class ConPty : IPty, IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint Infinite = 0xFFFFFFFF;
    private const int UseStdHandles = 0x00000100;

    private readonly IntPtr _console;
    private readonly IntPtr _process;
    private readonly IntPtr _thread;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly IntPtr _attributeList;
    private int _disposed;

    public event Action<byte[], int>? Output;

    public event Action<int>? Exited;

    public ConPty(string commandLine, string workingDirectory, int columns, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)
            || !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var result = CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out _console);
        // The console holds its own ends now.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (result != 0)
        {
            throw new Win32Exception(result);
        }

        _input = new FileStream(inputWrite, FileAccess.Write, 1, false);
        _output = new FileStream(outputRead, FileAccess.Read, 1, false);

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attributeList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref size)
            || !UpdateProcThreadAttribute(_attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var startup = new StartupInfoEx { AttributeList = _attributeList };
        startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
        // Empty standard handles on purpose: without this flag a program inherits the agent's own
        // stdin/stdout (a service log, a pipe) and writes there instead of into the pseudo console.
        startup.StartupInfo.Flags = UseStdHandles;

        // TERM tells programs that colours and cursor movement are understood.
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => $"{entry.Key}={entry.Value}")
            .Where(entry => !entry.StartsWith("TERM=", StringComparison.OrdinalIgnoreCase))
            .Append("TERM=xterm-256color")
            .Append("COLORTERM=truecolor")
            .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
        var block = string.Join((char)0, environment) + (char)0 + (char)0;
        var environmentPointer = Marshal.StringToHGlobalUni(block);
        try
        {
            if (!CreateProcess(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment, environmentPointer, workingDirectory,
                    ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            _process = info.Process;
            _thread = info.Thread;
        }
        finally
        {
            Marshal.FreeHGlobal(environmentPointer);
        }

        new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" }.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "conpty-wait" }.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            _input.Write(data);
            _input.Flush();
        }
        catch (IOException)
        {
            // The program has ended; its exit is reported separately.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ResizePseudoConsole(_console, Size(columns, rows));
        }
    }

    public void Kill()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            TerminateProcess(_process, 1);
        }
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = _output.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                Output?.Invoke(buffer, read);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, Infinite);
        GetExitCodeProcess(_process, out var code);
        // Closing the console ends the read loop once the remaining output is drained.
        Dispose();
        Exited?.Invoke((int)code);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ClosePseudoConsole(_console);
        _input.Dispose();
        CloseHandle(_thread);
        CloseHandle(_process);
        DeleteProcThreadAttributeList(_attributeList);
        Marshal.FreeHGlobal(_attributeList);
    }

    private static Coord Size(int columns, int rows) => new()
    {
        X = (short)Math.Clamp(columns, 10, 1000),
        Y = (short)Math.Clamp(rows, 5, 500),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr process, uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
