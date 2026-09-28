using System.Globalization;

namespace AiDotNet.Evolution.Programs;

/// <summary>Measures a Linux process and its live descendants from <c>/proc</c>.</summary>
/// <remarks>
/// A process limit set with <c>ulimit</c> is per process, and an address-space limit refuses an allocation rather
/// than reporting it, so neither can tell the sandbox that a tree exceeded its budget. Reading resident pages and
/// CPU ticks lets the engine terminate the whole tree and say which limit it crossed.
/// </remarks>
internal static class LinuxProcessTree
{
    // USER_HZ is fixed at 100 for every Linux architecture the runtime supports; /proc reports ticks in it.
    private const double TicksPerSecond = 100.0;
    private const int MaxProcesses = 4096;

    /// <summary>Sums resident memory and CPU time over a process and its live descendants.</summary>
    /// <param name="rootProcessId">The process at the root of the tree.</param>
    /// <returns>The totals, or <c>null</c> when the root cannot be read (for example because it already exited).</returns>
    public static (long ResidentBytes, TimeSpan CpuTime)? Measure(int rootProcessId)
    {
        if (rootProcessId <= 0) return null;

        long pageSize = Environment.SystemPageSize;
        long residentPages = 0;
        long ticks = 0;
        var pending = new Queue<int>();
        var seen = new HashSet<int>();
        pending.Enqueue(rootProcessId);
        bool readRoot = false;

        while (pending.Count > 0 && seen.Count < MaxProcesses)
        {
            int processId = pending.Dequeue();
            if (!seen.Add(processId)) continue;
            if (!TryReadStat(processId, out long resident, out long cpuTicks))
            {
                if (processId == rootProcessId) return null;
                continue;
            }

            if (processId == rootProcessId) readRoot = true;
            residentPages += resident;
            ticks += cpuTicks;
            foreach (int child in Children(processId)) pending.Enqueue(child);
        }

        return readRoot
            ? (residentPages * pageSize, TimeSpan.FromSeconds(ticks / TicksPerSecond))
            : null;
    }

    private static bool TryReadStat(int processId, out long residentPages, out long cpuTicks)
    {
        residentPages = 0;
        cpuTicks = 0;
        string text;
        try
        {
            text = File.ReadAllText("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/stat");
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        // The command name is parenthesised and may contain spaces, so fields are counted after the last ')'.
        int close = text.LastIndexOf(')');
        if (close < 0) return false;
        string[] fields = text.Substring(close + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        // fields[0] is field 3 (state): utime 14, stime 15, cutime 16, cstime 17, rss 24.
        if (fields.Length < 22) return false;
        for (int index = 11; index <= 14; index++)
        {
            if (!long.TryParse(fields[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) return false;
            cpuTicks += value;
        }

        return long.TryParse(fields[21], NumberStyles.Integer, CultureInfo.InvariantCulture, out residentPages);
    }

    private static IEnumerable<int> Children(int processId)
    {
        string tasks = "/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/task";
        string[] threads;
        try
        {
            threads = Directory.GetDirectories(tasks);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string thread in threads)
        {
            string text;
            try
            {
                text = File.ReadAllText(Path.Combine(thread, "children"));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string token in text.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int child)) yield return child;
            }
        }
    }
}
