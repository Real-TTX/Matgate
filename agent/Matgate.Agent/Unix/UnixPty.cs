using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Matgate.Agent;

// A program in a pseudo terminal on Linux and macOS.
//
// The terminal is a pair: the master end stays here (what the program writes is read from it, keys are
// written to it), the other end is the program's terminal. The program is started with posix_spawn - not
// fork, which a .NET process with its many threads must not do - as the leader of a session of its own,
// and opens its end of the pair first thing: that makes it the program's controlling terminal, so Ctrl+C
// and a changed window size reach it as they would in a real terminal.
//
// As the system's service the agent is root; programs then run as the user in the settings (RunAs) - with
// that user's rights, home folder and shell.
public sealed class UnixPtyStarter(AgentConfig config) : IPtyStarter
{
    public IPty Start(string id, string command, string? directory, int columns, int rows)
    {
        // The command is a command line, as in the settings: the shell takes it apart, and goes to the
        // folder first.
        var script = (string.IsNullOrEmpty(directory) ? "" : "cd " + Quote(directory) + " && ") + "exec " + command;
        var runAs = config.RunAs.Trim();
        // As root for another user: a second process of this program becomes that user and then the shell (see UnixUser).
        string[] arguments = runAs.Length > 0 && Environment.UserName != runAs && geteuid() == 0
            ? [Environment.ProcessPath!, "as-user", runAs, "--", "/bin/sh", "-c", script]
            : ["/bin/sh", "-c", script];
        return new UnixPty(arguments, columns, rows);
    }

    // For the shell, in single quotes: nothing inside them means anything but itself.
    public static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    [DllImport("libc")]
    private static extern uint geteuid();
}

public sealed class UnixPty : IPty
{
    private readonly FileStream _master;
    private readonly int _masterDescriptor;
    private readonly int _child;
    private int _exited;

    public event Action<byte[], int>? Output;

    public event Action<int>? Exited;

    public UnixPty(string[] arguments, int columns, int rows)
    {
        var mac = OperatingSystem.IsMacOS();
        // read and write, not the agent's own terminal, not inherited by the program
        _masterDescriptor = posix_openpt(2 | (mac ? 0x20000 : 0x100) | (mac ? 0x1000000 : 0x80000));
        if (_masterDescriptor < 0 || grantpt(_masterDescriptor) != 0 || unlockpt(_masterDescriptor) != 0)
        {
            throw new InvalidOperationException("This machine gave the agent no terminal (" + Marshal.GetLastPInvokeError() + ").");
        }

        var name = new byte[256];
        if (ptsname_r(_masterDescriptor, name, (nuint)name.Length) != 0)
        {
            close(_masterDescriptor);
            throw new InvalidOperationException("This machine gave the agent no terminal (" + Marshal.GetLastPInvokeError() + ").");
        }

        var slave = System.Text.Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0));
        SetSize(columns, rows);
        _child = Spawn(arguments, slave, mac);
        _master = new FileStream(new SafeFileHandle(_masterDescriptor, ownsHandle: true), FileAccess.ReadWrite, 1, isAsync: false);
        new Thread(Read) { IsBackground = true, Name = "Matgate terminal" }.Start();
        new Thread(Wait) { IsBackground = true, Name = "Matgate terminal exit" }.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        try
        {
            _master.Write(data);
            _master.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The program is gone; its exit says so.
        }
    }

    public void Resize(int columns, int rows) => SetSize(columns, rows);

    public void Kill()
    {
        if (Volatile.Read(ref _exited) != 0)
        {
            return;
        }

        // The whole session: the program and whatever it started. Asked first, then made to.
        kill(-_child, 1 /* SIGHUP */);
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            if (Volatile.Read(ref _exited) == 0)
            {
                kill(-_child, 9 /* SIGKILL */);
            }
        });
    }

    private void Read()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = _master.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                // A copy: the listener may keep it after this buffer is filled again.
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                Output?.Invoke(chunk, read);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Linux ends the reading with an error once the program closed its end.
        }
    }

    private void Wait()
    {
        int status;
        while (waitpid(_child, out status, 0) < 0 && Marshal.GetLastPInvokeError() == 4 /* interrupted */)
        {
        }

        // Exited normally: the code is in the second byte; ended by a signal: 128 + the signal, as a shell says it.
        var code = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
        if (Interlocked.Exchange(ref _exited, 1) == 0)
        {
            // What the program wrote last is read before the end is told.
            Thread.Sleep(100);
            Exited?.Invoke(code);
            try
            {
                _master.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    private void SetSize(int columns, int rows)
    {
        var size = new WindowSize { Rows = (ushort)Math.Clamp(rows, 2, 500), Columns = (ushort)Math.Clamp(columns, 2, 1000) };
        if (OperatingSystem.IsMacOS())
        {
            // ioctl takes its third argument "variadic", which on Apple's ARM goes on the stack and not into
            // a register: six placeholders fill the registers so that the size lands where ioctl looks.
            if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                ioctl_mac_arm(_masterDescriptor, 0x80087467, 0, 0, 0, 0, 0, 0, ref size);
            }
            else
            {
                ioctl(_masterDescriptor, 0x80087467, ref size);
            }
        }
        else
        {
            ioctl(_masterDescriptor, 0x5414, ref size);
        }
    }

    // The program, as the leader of a new session with the terminal as its standard input, output and error.
    private static unsafe int Spawn(string[] arguments, string slave, bool mac)
    {
        var actions = Marshal.AllocHGlobal(1024);
        var attributes = Marshal.AllocHGlobal(1024);
        var pointers = new List<IntPtr>();
        try
        {
            new Span<byte>((void*)actions, 1024).Clear();
            new Span<byte>((void*)attributes, 1024).Clear();
            posix_spawn_file_actions_init(actions);
            posix_spawnattr_init(attributes);
            posix_spawn_file_actions_addopen(actions, 0, slave, 2, 0);
            posix_spawn_file_actions_adddup2(actions, 0, 1);
            posix_spawn_file_actions_adddup2(actions, 0, 2);
            posix_spawnattr_setflags(attributes, (short)(mac ? 0x0400 : 0x80) /* a session of its own */);

            IntPtr Text(string value)
            {
                var pointer = Marshal.StringToCoTaskMemUTF8(value);
                pointers.Add(pointer);
                return pointer;
            }

            var argv = arguments.Select(Text).Append(IntPtr.Zero).ToArray();
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                environment[(string)entry.Key] = (string?)entry.Value ?? "";
            }

            environment["TERM"] = "xterm-256color";
            environment["COLORTERM"] = "truecolor";
            if (!environment.ContainsKey("LANG") && !environment.ContainsKey("LC_ALL"))
            {
                environment["LANG"] = "C.UTF-8";
            }

            // Nothing of how the agent itself was started is the program's business.
            foreach (var own in environment.Keys.Where(key => key.StartsWith("DOTNET_", StringComparison.Ordinal) || key.StartsWith("MATGATE_", StringComparison.Ordinal) || key is "INVOCATION_ID" or "JOURNAL_STREAM" or "NOTIFY_SOCKET").ToList())
            {
                environment.Remove(own);
            }

            var envp = environment.Select(pair => Text(pair.Key + "=" + pair.Value)).Append(IntPtr.Zero).ToArray();
            var result = posix_spawnp(out var child, arguments[0], actions, attributes, argv, envp);
            if (result != 0)
            {
                throw new InvalidOperationException($"The program could not be started ({arguments[0]}: error {result}).");
            }

            return child;
        }
        finally
        {
            posix_spawn_file_actions_destroy(actions);
            posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
            foreach (var pointer in pointers)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort WidthPixels;
        public ushort HeightPixels;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int ptsname_r(int descriptor, byte[] buffer, nuint length);

    [DllImport("libc")]
    private static extern int close(int descriptor);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int descriptor, ulong request, ref WindowSize size);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_mac_arm(int descriptor, ulong request, nint a, nint b, nint c, nint d, nint e, nint f, ref WindowSize size);

    [DllImport("libc")]
    private static extern int kill(int process, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int process, out int status, int options);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(IntPtr actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int descriptor, int target);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(IntPtr attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnp(out int process, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr actions, IntPtr attributes, IntPtr[] arguments, IntPtr[] environment);
}
