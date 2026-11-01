using System.Runtime.InteropServices;

namespace Matgate.Agent;

// Where the agent writes what happens. As a service or a logon task nobody sees a console, so every
// line also goes into agent.log next to the configuration (kept below 1 MB, one previous file).
public static class AgentLog
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void UseFile(string directory)
    {
        _file = Path.Combine(directory, "agent.log");
    }

    public static void Write(string line)
    {
        var stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}";
        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
        }

        if (_file is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    File.Move(_file, _file + ".1", overwrite: true);
                }

                File.AppendAllText(_file, stamped + Environment.NewLine);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // A windowed exe has no console. Typed in one, the commands still answer there.
    public static void AttachToParentConsole()
    {
        // Output that goes into a pipe or a file already has somewhere to go.
        if (!Console.IsOutputRedirected && AttachConsole(-1))
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(output);
            Console.SetError(output);
            Console.WriteLine();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
