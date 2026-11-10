using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Matgate.Agent;

// Windows sessions, for an agent that runs as a service under LocalSystem. Such a service lives in session 0,
// which has neither a screen nor a user. So it starts helpers where the work is:
//
//   - the screen: a helper as SYSTEM in the session the monitor shows (the console session) - SYSTEM, so it
//     can also show and operate the sign-in screen, the lock screen, UAC prompts and administrator windows;
//   - programs in a terminal: a helper with the token of the person who is signed in, in their session - so
//     Claude Code runs as that person, with their login, exactly as in user mode.
internal static class SessionNative
{
    private const uint MaximumAllowed = 0x02000000;
    private const uint TokenAllAccess = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int WtsActive = 0;

    public static bool IsLocalSystem { get; } = OperatingSystem.IsWindows() && WindowsIdentity.GetCurrent().IsSystem;

    public static int CurrentSession { get; } = Process.GetCurrentProcess().SessionId;

    // A LocalSystem service: the case in which the helpers are needed - and possible at all.
    public static bool IsSystemService => IsLocalSystem && CurrentSession == 0;

    // The session the monitor shows; -1 while there is none (a moment during a switch).
    public static int ConsoleSession()
    {
        var id = WTSGetActiveConsoleSessionId();
        return id == 0xFFFFFFFF ? -1 : (int)id;
    }

    // Someone who is signed in: at the console first, otherwise in an active remote session. Null at the
    // sign-in screen. The token is the caller's to close.
    public static (int Session, IntPtr Token)? SignedInUser()
    {
        var console = ConsoleSession();
        if (console > 0 && WTSQueryUserToken((uint)console, out var token))
        {
            return (console, token);
        }

        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            return null;
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(list + i * size);
                if (info.SessionId != 0 && info.State == WtsActive && WTSQueryUserToken((uint)info.SessionId, out token))
                {
                    return (info.SessionId, token);
                }
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }

        return null;
    }

    // The token of whoever is signed in to this session, or null. The caller closes it.
    public static IntPtr? UserToken(int session) => WTSQueryUserToken((uint)session, out var token) ? token : null;

    public static SecurityIdentifier? UserOf(IntPtr token)
    {
        using var identity = new WindowsIdentity(token);
        return identity.User;
    }

    // %TEMP% of whoever is signed in - where a helper running as SYSTEM puts files that person's Explorer must
    // read (SYSTEM's own temp folder is closed to everyone else). Null when nobody is signed in.
    public static string? SignedInUserTemp()
    {
        if (SignedInUser() is not { } user)
        {
            return null;
        }

        try
        {
            var size = 260;
            var buffer = new StringBuilder(size);
            return GetUserProfileDirectory(user.Token, buffer, ref size)
                ? Path.Combine(buffer.ToString(), "AppData", "Local", "Temp")
                : null;
        }
        finally
        {
            CloseHandle(user.Token);
        }
    }

    // This exe again, as SYSTEM, in another session (needs SeTcbPrivilege, which LocalSystem has).
    public static Process StartAsSystem(int session, string arguments)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenAllAccess, out var own))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!DuplicateTokenEx(own, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var token))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (!SetTokenInformation(token, TokenSessionId, ref session, sizeof(int)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return Start(token, arguments, IntPtr.Zero);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(own);
        }
    }

    // This exe again, as the person the token belongs to, in their session and with their environment.
    public static Process StartAsUser(IntPtr userToken, string arguments)
    {
        if (!DuplicateTokenEx(userToken, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            if (!CreateEnvironmentBlock(out var environment, token, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                return Start(token, arguments, environment);
            }
            finally
            {
                DestroyEnvironmentBlock(environment);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // The tests: the helper as an ordinary child process of the same user.
    public static Process StartChild(string arguments)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false, CreateNoWindow = true };
        return Process.Start(info) ?? throw new InvalidOperationException("The helper did not start.");
    }

    private static Process Start(IntPtr token, string arguments, IntPtr environment)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
        var commandLine = new StringBuilder($"\"{Environment.ProcessPath}\" {arguments}");
        var flags = CreateNoWindow | (environment != IntPtr.Zero ? CreateUnicodeEnvironment : 0);
        if (!CreateProcessAsUser(token, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, environment,
                Path.GetDirectoryName(Environment.ProcessPath), ref startup, out var info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            return Process.GetProcessById(info.ProcessId);
        }
        finally
        {
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
    }

    // Ctrl+Alt+Del. Windows takes it from a LocalSystem service, and only where the policy
    // SoftwareSASGeneration allows services to (the setup sets that).
    public static bool SendSecureAttention()
    {
        try
        {
            SendSAS(false);
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
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
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint session, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr duplicate);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int value, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfo startup,
        out ProcessInformation info);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserProfileDirectory(IntPtr token, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);
}

// Which desktop the monitor shows: "Default" normally, "Winlogon" for the sign-in and lock screen and for UAC
// prompts. A thread draws from and types into the desktop it is on; a helper running as SYSTEM moves its
// capture and input threads along (a normal user's process may not open Winlogon, so for it this changes
// nothing).
internal static class InputDesktop
{
    private const uint GenericAll = 0x10000000;
    private const int UoiName = 2;

    [ThreadStatic]
    private static IntPtr _opened;

    // True when this thread is now on the desktop the monitor shows.
    public static bool Follow()
    {
        var input = OpenInputDesktop(0, false, GenericAll);
        if (input == IntPtr.Zero)
        {
            return false;
        }

        if (string.Equals(Name(input), Name(GetThreadDesktop(GetCurrentThreadId())), StringComparison.OrdinalIgnoreCase))
        {
            CloseDesktop(input);
            return true;
        }

        // Fails for a thread that owns windows or hooks - capture and input threads own none.
        if (!SetThreadDesktop(input))
        {
            CloseDesktop(input);
            return false;
        }

        if (_opened != IntPtr.Zero)
        {
            CloseDesktop(_opened);
        }

        _opened = input;
        return true;
    }

    private static string Name(IntPtr desktop)
    {
        var buffer = new byte[256];
        return GetUserObjectInformation(desktop, UoiName, buffer, buffer.Length, out var needed)
            ? Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2))
            : "";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, byte[] info, int length, out int needed);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
