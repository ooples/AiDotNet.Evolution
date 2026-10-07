using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>The device a worker ran on.</summary>
public sealed class PtxDeviceInfo
{
    internal PtxDeviceInfo(PtxWorkerDevice device)
    {
        Ordinal = device.Ordinal;
        Name = device.Name;
        SmVersion = device.ComputeMajor * 10 + device.ComputeMinor;
        DriverVersion = device.DriverVersion;
        MultiprocessorCount = device.MultiprocessorCount;
        MaxSharedMemoryPerBlockOptin = device.MaxSharedMemoryPerBlockOptin;
        MaxRegistersPerMultiprocessor = device.MaxRegistersPerMultiprocessor;
        MaxThreadsPerMultiprocessor = device.MaxThreadsPerMultiprocessor;
        TotalMemoryBytes = device.TotalMemoryBytes;
    }

    /// <summary>Gets the device ordinal.</summary>
    public int Ordinal { get; }
    /// <summary>Gets the device name.</summary>
    public string Name { get; }
    /// <summary>Gets the compute capability as major * 10 + minor.</summary>
    public int SmVersion { get; }
    /// <summary>Gets the CUDA driver version, such as 12040.</summary>
    public int DriverVersion { get; }
    /// <summary>Gets the number of streaming multiprocessors.</summary>
    public int MultiprocessorCount { get; }
    /// <summary>Gets the opt-in shared memory per block.</summary>
    public int MaxSharedMemoryPerBlockOptin { get; }
    /// <summary>Gets the registers per multiprocessor.</summary>
    public int MaxRegistersPerMultiprocessor { get; }
    /// <summary>Gets the resident threads per multiprocessor.</summary>
    public int MaxThreadsPerMultiprocessor { get; }
    /// <summary>Gets the device memory.</summary>
    public long TotalMemoryBytes { get; }

    /// <summary>Gets a stable identity for evidence: name, compute capability and driver.</summary>
    public string Identity => Name + "|sm_" + SmVersion + "|driver-" + DriverVersion;
}

/// <summary>Resources the JIT assigned to a loaded kernel.</summary>
public sealed class PtxKernelResources
{
    /// <summary>Creates a resource record.</summary>
    /// <param name="registers">Registers per thread.</param>
    /// <param name="staticSharedBytes">Static shared memory per block.</param>
    /// <param name="dynamicSharedBytes">The largest dynamic shared memory any contract shape requests.</param>
    /// <param name="localBytes">Local (spill) memory per thread.</param>
    /// <param name="constantBytes">User constant memory.</param>
    /// <param name="maxThreadsPerBlock">The most threads per block this kernel can launch with.</param>
    /// <param name="compileMilliseconds">JIT wall time.</param>
    public PtxKernelResources(int registers, int staticSharedBytes, int dynamicSharedBytes, int localBytes, int constantBytes,
        int maxThreadsPerBlock, double compileMilliseconds)
    {
        Registers = registers;
        StaticSharedBytes = staticSharedBytes;
        DynamicSharedBytes = dynamicSharedBytes;
        LocalBytes = localBytes;
        ConstantBytes = constantBytes;
        MaxThreadsPerBlock = maxThreadsPerBlock;
        CompileMilliseconds = compileMilliseconds;
    }

    /// <summary>Gets registers per thread.</summary>
    public int Registers { get; }
    /// <summary>Gets static shared memory per block.</summary>
    public int StaticSharedBytes { get; }
    /// <summary>Gets the largest dynamic shared memory per block.</summary>
    public int DynamicSharedBytes { get; }
    /// <summary>Gets local memory per thread.</summary>
    public int LocalBytes { get; }
    /// <summary>Gets constant memory.</summary>
    public int ConstantBytes { get; }
    /// <summary>Gets the launchable threads per block.</summary>
    public int MaxThreadsPerBlock { get; }
    /// <summary>Gets JIT wall time.</summary>
    public double CompileMilliseconds { get; }
}

/// <summary>The outcome of validating and loading one PTX candidate.</summary>
public sealed class PtxCompilationResult
{
    internal PtxCompilationResult(string source, IReadOnlyList<CompilationDiagnostic> diagnostics, PtxLaunchConfiguration launch,
        PtxKernelResources? resources, PtxDeviceInfo? device, string infoLog, string errorLog, bool infrastructureFailure)
    {
        Source = source;
        Diagnostics = diagnostics;
        Launch = launch;
        Resources = resources;
        Device = device;
        InfoLog = infoLog;
        ErrorLog = errorLog;
        IsInfrastructureFailure = infrastructureFailure;
    }

    /// <summary>Gets the compiled source, including any launch header.</summary>
    public string Source { get; }
    /// <summary>Gets every diagnostic: static checks, JIT messages and resource-limit violations.</summary>
    public IReadOnlyList<CompilationDiagnostic> Diagnostics { get; }
    /// <summary>Gets the launch configuration the candidate selects.</summary>
    public PtxLaunchConfiguration Launch { get; }
    /// <summary>Gets the JIT-assigned resources, or <c>null</c> when the kernel did not load.</summary>
    public PtxKernelResources? Resources { get; }
    /// <summary>Gets the device, or <c>null</c> when no worker reached one.</summary>
    public PtxDeviceInfo? Device { get; }
    /// <summary>Gets the raw JIT information log.</summary>
    public string InfoLog { get; }
    /// <summary>Gets the raw JIT error log.</summary>
    public string ErrorLog { get; }
    /// <summary>Gets whether the failure lies outside the candidate (no driver, no device, worker crash), so a repair cannot help.</summary>
    public bool IsInfrastructureFailure { get; }
    /// <summary>Gets whether the candidate loaded and respects every limit.</summary>
    public bool Succeeded => Resources is not null && !Diagnostics.Any(d => d.Severity == CompilationDiagnosticSeverity.Error);

    /// <summary>Formats the errors as bounded repair feedback for the next model attempt.</summary>
    /// <returns>At most eight errors, each with its code, line and a message of at most 240 characters.</returns>
    public string ToFeedback()
    {
        string[] errors = Diagnostics.Where(d => d.Severity == CompilationDiagnosticSeverity.Error).Take(8).Select(d =>
        {
            string message = d.Message.Length > 240 ? d.Message.Substring(0, 240) : d.Message;
            return (d.Code ?? "PTX") + (d.Line is { } line ? " at line " + line : string.Empty) + ": " + message;
        }).ToArray();
        return errors.Length == 0 ? string.Empty : "Compilation failed: " + string.Join("; ", errors) + ".";
    }
}