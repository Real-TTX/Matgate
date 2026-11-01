using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Matgate.Agent.Setup;

// The Windows calls the setup needs and .NET does not wrap: the user at the screen, the right to log on
// as a service, a service created with an account and its password (passed to Windows directly - never
// on a command line, never in a file of ours), and a check of that password.
internal static class WindowsNative
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // The user signed in at this session - not the administrator whose name UAC may have asked for.
    public static string InteractiveUser()
    {
        var user = SessionString(5);
        var domain = SessionString(7);
        if (string.IsNullOrWhiteSpace(user))
        {
            return WindowsIdentity.GetCurrent().Name;
        }

        return string.IsNullOrWhiteSpace(domain) ? user : domain + (char)92 + user;
    }

    public static bool IsLocalSystem(string account)
    {
        return string.IsNullOrWhiteSpace(account)
            || account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || account.Equals("NT AUTHORITY" + (char)92 + "SYSTEM", StringComparison.OrdinalIgnoreCase);
    }

    // ".\name" and "name" mean a local account.
    public static string Qualify(string account)
    {
        var trimmed = account.Trim();
        if (trimmed.StartsWith("." + (char)92, StringComparison.Ordinal))
        {
            return Environment.MachineName + trimmed[1..];
        }

        return trimmed.Contains((char)92) || trimmed.Contains('@') ? trimmed : Environment.MachineName + (char)92 + trimmed;
    }

    public static void CheckPassword(string account, string password)
    {
        var qualified = Qualify(account);
        var separator = qualified.IndexOf((char)92);
        var domain = separator > 0 ? qualified[..separator] : null;
        var user = separator > 0 ? qualified[(separator + 1)..] : qualified;
        // LOGON32_LOGON_NETWORK only checks the credentials; it needs no logon right.
        if (!LogonUser(user, domain, password, 3, 0, out var token))
        {
            throw new InvalidOperationException("Account or password is not correct: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }

        CloseHandle(token);
    }

    public static void GrantServiceLogonRight(string account)
    {
        var sid = (SecurityIdentifier)new NTAccount(Qualify(account)).Translate(typeof(SecurityIdentifier));
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        var attributes = new LsaObjectAttributes();
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x00000010 | 0x00000800, out var policy);
        if (status != 0)
        {
            throw new Win32Exception(LsaNtStatusToWinError(status));
        }

        try
        {
            var right = LsaString("SeServiceLogonRight");
            status = LsaAddAccountRights(policy, sidBytes, [right], 1);
            Marshal.FreeHGlobal(right.Buffer);
            if (status != 0)
            {
                throw new Win32Exception(LsaNtStatusToWinError(status));
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    // Creates the service, or changes the existing one, with this account and password.
    public static void CreateOrUpdateService(string name, string displayName, string commandLine, string? account, string? password)
    {
        var manager = OpenSCManager(null, null, 0x0002 | 0x0001);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenService(manager, name, 0xF01FF);
            if (service == IntPtr.Zero)
            {
                service = CreateService(manager, name, displayName, 0xF01FF, 0x10, 0x2, 0x1, commandLine, null, IntPtr.Zero, null, account, password);
                if (service == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            else if (!ChangeServiceConfig(service, 0x10, 0x2, 0x1, commandLine, null, IntPtr.Zero, null, account, password, displayName))
            {
                var error = Marshal.GetLastWin32Error();
                CloseServiceHandle(service);
                throw new Win32Exception(error);
            }

            CloseServiceHandle(service);
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static string SessionString(int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, -1, infoClass, out var buffer, out _))
        {
            return "";
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static LsaUnicodeString LsaString(string value) => new()
    {
        Buffer = Marshal.StringToHGlobalUni(value),
        Length = (ushort)(value.Length * 2),
        MaximumLength = (ushort)((value.Length + 1) * 2),
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr system, ref LsaObjectAttributes attributes, int access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LsaUnicodeString[] rights, int count);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateService(IntPtr manager, string name, string displayName, uint access, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string displayName);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
