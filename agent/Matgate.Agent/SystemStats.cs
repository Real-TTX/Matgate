using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Matgate.Agent;

// How busy the machine is, for Matgate's status bar: the processor in percent since the last question (or over a
// quarter of a second the first time), and the memory in bytes. Windows and Linux; on a Mac the agent answers that
// it cannot tell, and Matgate shows nothing.
internal static class SystemStats
{
    private static readonly object Gate = new();
    private static (ulong Idle, ulong Total)? _last;

    public static bool IsAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static JsonObject Read()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("This agent cannot tell how busy its machine is.");
        }

        var cpu = CpuPercent();
        var (total, available, swapTotal, swapFree) = Memory();
        return new JsonObject
        {
            ["cpu"] = cpu is null ? null : Math.Round(cpu.Value, 1),
            ["cores"] = Environment.ProcessorCount,
            ["memTotal"] = total,
            ["memAvailable"] = available,
            ["memUsed"] = total > available ? total - available : 0,
            ["swapTotal"] = swapTotal,
            ["swapUsed"] = swapTotal > swapFree ? swapTotal - swapFree : 0,
            ["uptime"] = Environment.TickCount64 / 1000,
        };
    }

    private static double? CpuPercent()
    {
        var now = CpuTimes();
        if (now is null)
        {
            return null;
        }

        (ulong Idle, ulong Total) before;
        lock (Gate)
        {
            before = _last ?? default;
            _last = now;
        }

        if (before.Total == 0 || now.Value.Total <= before.Total)
        {
            // The first question: a short look of its own.
            Thread.Sleep(250);
            var later = CpuTimes();
            if (later is null)
            {
                return null;
            }

            lock (Gate)
            {
                _last = later;
            }

            before = now.Value;
            now = later;
        }

        var total = now.Value.Total - before.Total;
        var idle = now.Value.Idle - before.Idle;
        return total == 0 ? 0 : Math.Clamp(100.0 * (total - Math.Min(idle, total)) / total, 0, 100);
    }

    private static (ulong Idle, ulong Total)? CpuTimes()
    {
        if (OperatingSystem.IsWindows())
        {
            // Kernel time includes idle time.
            return GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : null;
        }

        // /proc/stat: "cpu  user nice system idle iowait irq softirq steal ..." in ticks.
        var line = File.ReadLines("/proc/stat").FirstOrDefault(entry => entry.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(ulong.Parse).ToArray();
        if (values.Length < 4)
        {
            return null;
        }

        var idleTicks = values[3] + (values.Length > 4 ? values[4] : 0);
        ulong totalTicks = 0;
        foreach (var value in values)
        {
            totalTicks += value;
        }

        return (idleTicks, totalTicks);
    }

    private static (long Total, long Available, long SwapTotal, long SwapFree) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return (0, 0, 0, 0);
            }

            // Windows counts its page file as part of the commit limit, not as a swap of its own: none is reported.
            return ((long)status.TotalPhys, (long)status.AvailPhys, 0, 0);
        }

        long Kb(Dictionary<string, long> table, string key) => table.TryGetValue(key, out var value) ? value * 1024 : 0;
        var info = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in File.ReadLines("/proc/meminfo"))
        {
            var colon = entry.IndexOf(':');
            if (colon > 0 && long.TryParse(entry[(colon + 1)..].Trim().Split(' ')[0], out var value))
            {
                info[entry[..colon]] = value;
            }
        }

        var memTotal = Kb(info, "MemTotal");
        var memAvailable = info.ContainsKey("MemAvailable") ? Kb(info, "MemAvailable") : Kb(info, "MemFree") + Kb(info, "Cached");
        return (memTotal, memAvailable, Kb(info, "SwapTotal"), Kb(info, "SwapFree"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
