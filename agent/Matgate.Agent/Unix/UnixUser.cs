using System.Runtime.InteropServices;

namespace Matgate.Agent;

// Becoming another user, for a program in a terminal.
//
// The agent is root (the system's service); a program in a terminal is to run as a person - with that
// person's rights, home folder and shell. The obvious tool, su, will not do: asked to run a command it puts
// the command into a session of its own, away from the terminal, and Ctrl+C then ends su and everything
// with it instead of the program in front.
//
// So the agent does it itself, in a second process of its own program ("matgate-agent as-user name --
// program arguments"): that process is the terminal's session, gives up root for the user (groups first,
// then the group, then the user - the order in which it can still be done), sets what a login sets (HOME,
// USER, LOGNAME, SHELL) and then becomes the program. Nothing of the agent is left in between.
public static class UnixUser
{
    public sealed record Account(string Name, uint Id, uint Group, string Home, string Shell);

    public static Account? Find(string name)
    {
        var buffer = Marshal.AllocHGlobal(16384);
        var entry = Marshal.AllocHGlobal(256);
        try
        {
            if (getpwnam_r(name, entry, buffer, 16384, out var found) != 0 || found == IntPtr.Zero)
            {
                return null;
            }

            // struct passwd: name, password, uid, gid - and from there the two systems differ (macOS has
            // the time the password changes and a class before the rest).
            var id = (uint)Marshal.ReadInt32(entry, 2 * IntPtr.Size);
            var group = (uint)Marshal.ReadInt32(entry, 2 * IntPtr.Size + 4);
            var afterIds = 2 * IntPtr.Size + 8;
            var (home, shell) = OperatingSystem.IsMacOS()
                ? (afterIds + 3 * IntPtr.Size, afterIds + 4 * IntPtr.Size)
                : (afterIds + IntPtr.Size, afterIds + 2 * IntPtr.Size);
            return new Account(
                name,
                id,
                group,
                Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry, home)) ?? "/",
                Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entry, shell)) is { Length: > 0 } login ? login : "/bin/sh");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(entry);
        }
    }

    // matgate-agent as-user <name> -- <program> [arguments]: never returns when it works.
    public static int Become(string[] args)
    {
        var split = Array.IndexOf(args, "--");
        if (args.Length < 4 || split != 2 || split + 1 >= args.Length)
        {
            Console.Error.WriteLine("as-user: name -- program [arguments]");
            return 64;
        }

        var account = Find(args[1]);
        if (account is null)
        {
            Console.Error.WriteLine($"The agent's settings name the user \"{args[1]}\" for programs (RunAs), and this machine has no such user.");
            return 67;
        }

        // The groups while still root, then the group, then the user: after the last one nothing of this can be done any more.
        var groups = OperatingSystem.IsMacOS() ? initgroups_mac(account.Name, (int)account.Group) : initgroups(account.Name, account.Group);
        if (groups != 0 || setgid(account.Group) != 0 || setuid(account.Id) != 0)
        {
            Console.Error.WriteLine($"The agent could not become \"{account.Name}\" (error {Marshal.GetLastPInvokeError()}).");
            return 77;
        }

        Environment.SetEnvironmentVariable("HOME", account.Home);
        Environment.SetEnvironmentVariable("USER", account.Name);
        Environment.SetEnvironmentVariable("LOGNAME", account.Name);
        Environment.SetEnvironmentVariable("SHELL", account.Shell);
        foreach (var roots in new[] { "SUDO_USER", "SUDO_UID", "SUDO_GID", "SUDO_COMMAND", "MAIL" })
        {
            Environment.SetEnvironmentVariable(roots, null);
        }

        try
        {
            Directory.SetCurrentDirectory(Directory.Exists(account.Home) ? account.Home : "/");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var program = args[split + 1];
        var pointers = args[(split + 1)..].Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => !((string)entry.Key).StartsWith("DOTNET_", StringComparison.Ordinal))
            .Select(entry => Marshal.StringToCoTaskMemUTF8(entry.Key + "=" + entry.Value))
            .Append(IntPtr.Zero)
            .ToArray();
        execve(program, pointers, environment);
        Console.Error.WriteLine($"The program could not be started ({program}: error {Marshal.GetLastPInvokeError()}).");
        return 127;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getpwnam_r([MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr entry, IntPtr buffer, nuint size, out IntPtr result);

    [DllImport("libc", SetLastError = true)]
    private static extern int initgroups([MarshalAs(UnmanagedType.LPUTF8Str)] string user, uint group);

    [DllImport("libc", EntryPoint = "initgroups", SetLastError = true)]
    private static extern int initgroups_mac([MarshalAs(UnmanagedType.LPUTF8Str)] string user, int group);

    [DllImport("libc", SetLastError = true)]
    private static extern int setgid(uint group);

    [DllImport("libc", SetLastError = true)]
    private static extern int setuid(uint user);

    [DllImport("libc", SetLastError = true)]
    private static extern int execve([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr[] arguments, IntPtr[] environment);
}
