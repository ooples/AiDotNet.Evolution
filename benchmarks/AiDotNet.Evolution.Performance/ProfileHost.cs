using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace AiDotNet.Evolution.Performance;

/// <summary>Aggregate busy time on a set of logical CPUs; read-only counters, never a per-process enumeration.</summary>
public sealed record ProfileCpuLoadSample(double BusyMilliseconds, double TotalMilliseconds, string Source);

/// <summary>Platform facts the profile records instead of assuming: core topology, timer resolution, power policy and CPU load.</summary>
public static class ProfileHost
{
    /// <summary>Logical-processor masks of each physical core in the group, lowest core first; empty when topology is unavailable.</summary>
    public static IReadOnlyList<ulong> CoreMasks(ushort group)
    {
        if (OperatingSystem.IsWindows()) return WindowsCoreMasks(group);
        if (OperatingSystem.IsLinux()) return LinuxCoreMasks();
        return Array.Empty<ulong>();
    }

    /// <summary>Chooses one logical processor per physical core, avoiding CPU 0 and its sibling, so hyper-threads never share a core.</summary>
    /// <remarks>Pure: the caller supplies the permitted mask and the core layout, so the choice itself is unit-testable.</remarks>
    public static ulong SelectAffinity(ulong available, IReadOnlyList<ulong> coreMasks, int maximumProcessors = 4)
    {
        ArgumentNullException.ThrowIfNull(coreMasks);
        if (available == 0 || maximumProcessors < 1 || maximumProcessors > 4) throw new ArgumentOutOfRangeException(nameof(available));
        ulong selected = 0;
        int count = 0;
        // First pass: one logical CPU per physical core, skipping the core that owns CPU 0 (the OS/interrupt-heavy core).
        foreach (ulong core in coreMasks)
        {
            if (count == maximumProcessors) break;
            ulong candidates = core & available;
            if (candidates == 0 || (core & 1UL) != 0) continue;
            selected |= LowestBit(candidates); count++;
        }
        // Second pass: accept remaining cores (including CPU 0's) only when the host is too small to avoid them.
        if (count < maximumProcessors)
            foreach (ulong core in coreMasks)
            {
                if (count == maximumProcessors) break;
                ulong candidates = core & available & ~selected;
                if (candidates == 0 || (selected & core) != 0) continue;
                selected |= LowestBit(candidates); count++;
            }
        // Fallback: unknown topology, or fewer distinct cores than requested processors.
        for (int bit = 0; bit < 64 && count < maximumProcessors; bit++)
        {
            ulong candidate = 1UL << bit;
            if ((available & candidate) == 0 || (selected & candidate) != 0) continue;
            selected |= candidate; count++;
        }
        if (selected == 0) throw new ArgumentOutOfRangeException(nameof(available), "No permitted logical processor could be selected.");
        return selected;
    }

    /// <summary>Renders the simultaneous-multithreading layout of the selected CPUs, including each core's full sibling set.</summary>
    public static string DescribeTopology(IReadOnlyList<ulong> coreMasks, ulong selected)
    {
        ArgumentNullException.ThrowIfNull(coreMasks);
        if (coreMasks.Count == 0) return "cores=unknown;selected=" + selected.ToString("X", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"cores={coreMasks.Count};threadsPerCore={coreMasks.Max(BitOperations.PopCount)};selected=");
        bool first = true;
        for (int core = 0; core < coreMasks.Count; core++)
        {
            if ((coreMasks[core] & selected) == 0) continue;
            if (!first) builder.Append(',');
            first = false;
            builder.Append(CultureInfo.InvariantCulture, $"core{core}[{string.Join('+', Bits(coreMasks[core]))}]:cpu{Bits(coreMasks[core] & selected).First()}");
        }
        return builder.ToString();
    }

    /// <summary>Processor identity from the OS rather than an environment variable that Linux does not define.</summary>
    public static string CpuName()
    {
        string? identifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(identifier)) return identifier.Trim();
        if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
        {
            string? model = null, vendor = null;
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (model is null && line.StartsWith("model name", StringComparison.Ordinal)) model = Value(line);
                else if (vendor is null && line.StartsWith("vendor_id", StringComparison.Ordinal)) vendor = Value(line);
                if (model is not null && vendor is not null) break;
            }
            if (model is not null) return vendor is null ? model : model + ", " + vendor;
        }
        return "not-reported";

        static string Value(string line) => line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    /// <summary>The host's active power plan (Windows) or CPU frequency governor (Linux); neither is changed by the profiler.</summary>
    public static string PowerPolicy()
    {
        if (OperatingSystem.IsWindows())
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr scheme) != 0 || scheme == IntPtr.Zero) return "unavailable";
            try
            {
                var guid = Marshal.PtrToStructure<Guid>(scheme);
                return "scheme=" + guid.ToString("D", CultureInfo.InvariantCulture) + NameOf(guid);
            }
            finally { LocalFree(scheme); }
        }
        if (OperatingSystem.IsLinux())
        {
            const string governor = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor";
            const string driver = "/sys/devices/system/cpu/cpu0/cpufreq/scaling_driver";
            if (File.Exists(governor))
                return "governor=" + File.ReadAllText(governor).Trim() + (File.Exists(driver) ? ";driver=" + File.ReadAllText(driver).Trim() : string.Empty);
            return "governor=unavailable";
        }
        return "unavailable";

        static string NameOf(Guid guid) => guid.ToString("D", CultureInfo.InvariantCulture) switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => " (Balanced)",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => " (High performance)",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => " (Power saver)",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => " (Ultimate performance)",
            _ => " (custom)"
        };
    }

    /// <summary>Raises the platform timer resolution for this process so Task.Delay(1) is not rounded to a 15.6 ms tick.</summary>
    public static IDisposable RaiseTimerResolution() => OperatingSystem.IsWindows() ? new WindowsTimerPeriod() : new NoTimerPeriod();

    /// <summary>The timer resolution actually in force, in milliseconds, as reported by the OS.</summary>
    public static double TimerResolutionMilliseconds()
    {
        if (!OperatingSystem.IsWindows()) return 1; // Linux delays use high-resolution timers; the profiler changes nothing.
        return NtQueryTimerResolution(out _, out _, out uint current) == 0 ? current / 10000d : double.NaN;
    }

    /// <summary>Resolution of <see cref="System.Diagnostics.Process.TotalProcessorTime"/> on this platform, in milliseconds.</summary>
    public static double ProcessorTimeResolutionMilliseconds()
    {
        if (OperatingSystem.IsWindows())
            return GetSystemTimeAdjustment(out _, out uint increment, out _) ? increment / 10000d : 15.625;
        return 10; // Linux exposes process CPU time in USER_HZ clock ticks (typically 100 Hz).
    }

    /// <summary>Unhalted cycles charged to this process (Windows); a finer counter than the 15.6 ms process-time clock.</summary>
    public static ulong? ProcessCycles()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return QueryProcessCycleTime(GetCurrentProcess(), out ulong cycles) ? cycles : null;
    }

    /// <summary>Nanosecond-resolution CPU time for this process from /proc (Linux); null elsewhere.</summary>
    public static double? ProcessFineCpuMilliseconds()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            string[] fields = File.ReadAllText("/proc/self/schedstat").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 0 && double.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out double nanoseconds)
                ? nanoseconds / 1_000_000d : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>Busy and total time accumulated by the given logical CPUs, for measuring load this profile does not own.</summary>
    public static ProfileCpuLoadSample? SampleCpuLoad(ulong mask, ushort group)
    {
        if (OperatingSystem.IsWindows()) return WindowsCpuLoad(mask, group);
        if (OperatingSystem.IsLinux()) return LinuxCpuLoad(mask);
        return null;
    }

    private static ulong LowestBit(ulong value) => value & (~value + 1);

    private static IEnumerable<int> Bits(ulong mask)
    {
        for (int bit = 0; bit < 64; bit++) if ((mask & (1UL << bit)) != 0) yield return bit;
    }

    private static IReadOnlyList<ulong> WindowsCoreMasks(ushort group)
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return Array.Empty<ulong>();
        IntPtr buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var cores = new List<ulong>();
            long end = buffer.ToInt64() + length;
            for (IntPtr entry = buffer; entry.ToInt64() < end;)
            {
                int size = Marshal.ReadInt32(entry, 4);
                if (size <= 0) break;
                ushort groupCount = (ushort)Marshal.ReadInt16(entry, 30);
                for (int index = 0; index < groupCount; index++)
                {
                    ulong affinity = unchecked((ulong)Marshal.ReadInt64(entry, 32 + (index * 16)));
                    ushort affinityGroup = (ushort)Marshal.ReadInt16(entry, 40 + (index * 16));
                    if (affinityGroup == group && affinity != 0) cores.Add(affinity);
                }
                entry = new IntPtr(entry.ToInt64() + size);
            }
            cores.Sort();
            return cores;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IReadOnlyList<ulong> LinuxCoreMasks()
    {
        var cores = new List<ulong>();
        var seen = new HashSet<ulong>();
        for (int cpu = 0; cpu < 64; cpu++)
        {
            string path = $"/sys/devices/system/cpu/cpu{cpu.ToString(CultureInfo.InvariantCulture)}/topology/thread_siblings_list";
            if (!File.Exists(path)) continue;
            ulong mask = 0;
            foreach (string part in File.ReadAllText(path).Trim().Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] range = part.Split('-');
                if (!int.TryParse(range[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int first)) continue;
                int last = range.Length > 1 && int.TryParse(range[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : first;
                for (int sibling = first; sibling <= last && sibling < 64; sibling++) mask |= 1UL << sibling;
            }
            if (mask != 0 && seen.Add(mask)) cores.Add(mask);
        }
        cores.Sort();
        return cores;
    }

    private static ProfileCpuLoadSample? WindowsCpuLoad(ulong mask, ushort group)
    {
        int processors = (int)GetActiveProcessorCount(group);
        if (processors <= 0) return null;
        int entrySize = 48;
        byte[] buffer = new byte[processors * entrySize];
        if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buffer, (uint)buffer.Length, out uint written) != 0) return null;
        int available = (int)(written / entrySize);
        double busy = 0, total = 0;
        for (int cpu = 0; cpu < available && cpu < 64; cpu++)
        {
            if ((mask & (1UL << cpu)) == 0) continue;
            long idle = BitConverter.ToInt64(buffer, (cpu * entrySize) + 0);
            long kernel = BitConverter.ToInt64(buffer, (cpu * entrySize) + 8);
            long user = BitConverter.ToInt64(buffer, (cpu * entrySize) + 16);
            busy += (kernel - idle + user) / 10000d;
            total += (kernel + user) / 10000d;
        }
        return new ProfileCpuLoadSample(busy, total, "NtQuerySystemInformation/SystemProcessorPerformanceInformation");
    }

    private static ProfileCpuLoadSample? LinuxCpuLoad(ulong mask)
    {
        if (!File.Exists("/proc/stat")) return null;
        double busy = 0, total = 0;
        foreach (string line in File.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal) || line.Length < 4 || !char.IsAsciiDigit(line[3])) continue;
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!int.TryParse(fields[0][3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpu) || cpu >= 64) continue;
            if ((mask & (1UL << cpu)) == 0) continue;
            double[] values = fields.Skip(1).Select(field => double.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out double value) ? value : 0).ToArray();
            double idle = values.Length > 4 ? values[3] + values[4] : 0;
            double sum = values.Sum();
            busy += (sum - idle) * 10; // USER_HZ ticks to milliseconds.
            total += sum * 10;
        }
        return total == 0 ? null : new ProfileCpuLoadSample(busy, total, "/proc/stat");
    }

    private sealed class WindowsTimerPeriod : IDisposable
    {
        private readonly bool _raised;
        public WindowsTimerPeriod() => _raised = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        public void Dispose() { if (_raised && OperatingSystem.IsWindows()) TimeEndPeriod(1); }
    }

    private sealed class NoTimerPeriod : IDisposable
    {
        public void Dispose() { }
    }

    private const int RelationProcessorCore = 0;
    private const int SystemProcessorPerformanceInformation = 8;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycleTime);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimeAdjustment(out uint adjustment, out uint increment, [MarshalAs(UnmanagedType.Bool)] out bool disabled);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, byte[] buffer, uint length, out uint returnLength);
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRoot, out IntPtr activePolicy);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
