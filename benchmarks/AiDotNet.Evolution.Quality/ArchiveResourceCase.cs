using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AiDotNet.Evolution.Quality;

internal static class ArchiveResourceCase
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 8 || args[0] is not ("Quadratic" or "Rippled") || args[1] is not ("SparseGrid" or "FixedCentroid") ||
            !int.TryParse(args[2], out int seed) || seed is < 0 or > 99 ||
            !int.TryParse(args[3], out int dimensions) || dimensions is not (8 or 12 or 20 or 32 or 64) ||
            !int.TryParse(args[4], out int budget) || budget is < 8 or > 4096 ||
            !int.TryParse(args[5], out int memoryMiB) || memoryMiB is < 1 or > 2048)
        {
            Console.Error.WriteLine("--archive-resource-case <Quadratic|Rippled> <SparseGrid|FixedCentroid> <seed 0..99> <dimensions 8|12|20|32|64> <budget 8..4096> <observed-peak-MiB 1..2048> <full-revision|working-tree-smoke> <new-report.json>");
            return 2;
        }
        using var probe = new ArchiveMemoryProbe((long)memoryMiB * 1024 * 1024);
        var scenario = new ArchiveCase(args[0] + dimensions.ToString(CultureInfo.InvariantCulture), args[1], (ulong)seed, dimensions, probe);
        return await ArchivePartitionPilot.RunAsync(new[] { (seed + 1).ToString(CultureInfo.InvariantCulture), args[4], args[6], args[7] }, scenario);
    }
}

internal sealed record ArchiveCase(string Task, string Method, ulong Seed, int Dimensions, ArchiveMemoryProbe Probe);

// A comparable OBSERVED physical-memory budget, not an allocator or kernel hard limit.
// OS process-lifetime peak includes startup/shared pages; one fresh child is mandatory per case.
internal sealed class ArchiveMemoryProbe : IDisposable
{
    internal const int ObservationStride = 16;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long _allocated = GC.GetTotalAllocatedBytes(precise: true);
    private readonly double _cpu;
    private long _peak;
    private int _observations;
    private int _evaluatedEvents;
    internal ArchiveMemoryProbe(long budget)
    {
        Budget = budget;
        _process.Refresh();
        _cpu = _process.TotalProcessorTime.TotalMilliseconds;
        Check();
    }
    internal long Budget { get; }
    internal bool Exceeded { get; private set; }
    internal Action? Stop { get; set; }
    internal void ObserveEvaluation()
    {
        // Refresh can be expensive; the OS retains the lifetime peak between observations.
        if (++_evaluatedEvents % ObservationStride == 0) Check();
    }
    internal void Check()
    {
        _process.Refresh();
        _peak = Math.Max(_peak, _process.PeakWorkingSet64);
        _observations++;
        if (_peak <= 0) throw new InvalidOperationException("Process peak resident memory is unavailable; do not substitute elite count.");
        Exceeded |= _peak > Budget;
        if (Exceeded) Stop?.Invoke();
    }
    internal object Capture()
    {
        Check();
        return new
        {
            ProcessId = Environment.ProcessId,
            PeakResidentBudgetBytes = Budget,
            PeakResidentBytes = _peak,
            MemoryBudgetExceeded = Exceeded,
            MemoryObservationStride = ObservationStride,
            MemoryObservationCount = _observations,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - _allocated,
            ElapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            CpuMilliseconds = _process.TotalProcessorTime.TotalMilliseconds - _cpu,
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            ServerGc = GCSettings.IsServerGC,
            GcHeapHardLimit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit"),
            MeasurementBoundary = "Process-lifetime peak resident memory through setup, search and common-reference projection, before artifact metadata hashing/JSON serialization. Wall/CPU/allocation deltas start at worker probe construction. Observations at construction, before search, every 16 Evaluated events and after projection stop admission after an observed overrun; not an OS-enforced hard cap. Admission can continue between observations; the final lifetime peak is authoritative.",
            CoreInformationalVersion = typeof(EvolutionEngineOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            WorkerSha256 = Hash(typeof(ArchiveResourceCase).Assembly.Location),
            CoreSha256 = Hash(typeof(EvolutionEngineOptions).Assembly.Location)
        };
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public void Dispose() => _process.Dispose();
}
