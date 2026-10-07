// Compiled into both AiDotNet.Evolution.Ptx and the isolated AiDotNet.Evolution.Ptx.Worker process, so the two sides of
// the process boundary can never disagree about the wire format. Keep this file free of every other dependency.
namespace AiDotNet.Evolution.Ptx;

/// <summary>What one worker invocation does.</summary>
internal enum PtxWorkerOperation
{
    /// <summary>Initialise the driver and describe the device; load nothing.</summary>
    Probe = 0,
    /// <summary>JIT every kernel and report logs and resource usage; launch nothing.</summary>
    Compile = 1,
    /// <summary>JIT every kernel, then run every case and return the raw outputs.</summary>
    Validate = 2,
    /// <summary>JIT both kernels, then measure paired candidate/incumbent and incumbent/incumbent control samples.</summary>
    Time = 3
}

/// <summary>How a buffer is filled before a launch.</summary>
internal enum PtxWorkerFillKind
{
    /// <summary>Uniform real values in [Minimum, Maximum).</summary>
    Uniform = 0,
    /// <summary>Uniform integers in [Minimum, Maximum].</summary>
    Integers = 1,
    /// <summary>Every element equals Minimum.</summary>
    Constant = 2
}

/// <summary>Element types a kernel argument buffer may hold. Values are part of the wire format.</summary>
internal enum PtxWorkerElementType
{
    Float32 = 0,
    Float64 = 1,
    Float16 = 2,
    Int32 = 3,
    UInt32 = 4,
    Int64 = 5,
    UInt8 = 6
}

internal sealed class PtxWorkerRequest
{
    public const int CurrentProtocol = 1;
    public int Protocol { get; set; } = CurrentProtocol;
    public PtxWorkerOperation Operation { get; set; }
    public int DeviceOrdinal { get; set; }
    public long MaxReadbackBytes { get; set; }
    public long MaxDeviceBytes { get; set; }
    public List<PtxWorkerKernel> Kernels { get; set; } = new();
    public List<PtxWorkerCase> Cases { get; set; } = new();
    public PtxWorkerTimingPlan? Timing { get; set; }
}

internal sealed class PtxWorkerKernel
{
    public string Ptx { get; set; } = string.Empty;
    public string EntryPoint { get; set; } = string.Empty;
    public int MaxRegisters { get; set; }
    public int OptimizationLevel { get; set; } = 4;
}

internal sealed class PtxWorkerCase
{
    public List<PtxWorkerBuffer> Buffers { get; set; } = new();
    public List<PtxWorkerArgument> Arguments { get; set; } = new();
    /// <summary>One launch per kernel, in kernel order.</summary>
    public List<PtxWorkerLaunch> Launches { get; set; } = new();
}

internal sealed class PtxWorkerBuffer
{
    public long Elements { get; set; }
    public PtxWorkerElementType ElementType { get; set; }
    /// <summary>True when the kernel reads the generated contents; outputs start from a sentinel instead.</summary>
    public bool IsInput { get; set; }
    /// <summary>True when the kernel writes the buffer and the worker returns its contents.</summary>
    public bool IsOutput { get; set; }
    public PtxWorkerFillKind Fill { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public ulong Seed { get; set; }
}

internal sealed class PtxWorkerArgument
{
    /// <summary>The buffer this argument points at, or -1 for a by-value scalar.</summary>
    public int BufferIndex { get; set; } = -1;
    /// <summary>The scalar's little-endian bytes as base64 when <see cref="BufferIndex"/> is -1.</summary>
    public string? ScalarBase64 { get; set; }
}

internal sealed class PtxWorkerLaunch
{
    public uint GridX { get; set; } = 1;
    public uint GridY { get; set; } = 1;
    public uint GridZ { get; set; } = 1;
    public uint BlockX { get; set; } = 1;
    public uint BlockY { get; set; } = 1;
    public uint BlockZ { get; set; } = 1;
    public uint SharedBytes { get; set; }
}

internal sealed class PtxWorkerTimingPlan
{
    public int Warmup { get; set; }
    public int Pairs { get; set; }
    public int ControlPairs { get; set; }
    public int LaunchesPerSample { get; set; } = 1;
}

internal sealed class PtxWorkerResponse
{
    public int Protocol { get; set; } = PtxWorkerRequest.CurrentProtocol;
    /// <summary><c>ok</c>, or a stable failure code such as <c>cuda-unavailable</c> or <c>invalid-request</c>.</summary>
    public string Status { get; set; } = "ok";
    public string? Message { get; set; }
    public PtxWorkerDevice? Device { get; set; }
    public List<PtxWorkerCompiled> Kernels { get; set; } = new();
    public List<PtxWorkerCaseResult> Cases { get; set; } = new();
    public PtxWorkerTimingResult? Timing { get; set; }
}

internal sealed class PtxWorkerDevice
{
    public int Ordinal { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ComputeMajor { get; set; }
    public int ComputeMinor { get; set; }
    public int DriverVersion { get; set; }
    public int MultiprocessorCount { get; set; }
    public int MaxThreadsPerBlock { get; set; }
    public int MaxThreadsPerMultiprocessor { get; set; }
    public int MaxSharedMemoryPerBlock { get; set; }
    public int MaxSharedMemoryPerBlockOptin { get; set; }
    public int MaxSharedMemoryPerMultiprocessor { get; set; }
    public int MaxRegistersPerBlock { get; set; }
    public int MaxRegistersPerMultiprocessor { get; set; }
    public long TotalMemoryBytes { get; set; }
}

internal sealed class PtxWorkerCompiled
{
    public bool Loaded { get; set; }
    /// <summary>The CUDA error name when loading or function lookup failed.</summary>
    public string? ErrorCode { get; set; }
    public string InfoLog { get; set; } = string.Empty;
    public string ErrorLog { get; set; } = string.Empty;
    public double WallTimeMilliseconds { get; set; }
    public int Registers { get; set; }
    public int StaticSharedBytes { get; set; }
    public int ConstantBytes { get; set; }
    public int LocalBytes { get; set; }
    public int MaxThreadsPerBlock { get; set; }
    public int PtxVersion { get; set; }
    public int BinaryVersion { get; set; }
}

internal sealed class PtxWorkerCaseResult
{
    /// <summary>One run per kernel, in kernel order.</summary>
    public List<PtxWorkerRun> Runs { get; set; } = new();
}

internal sealed class PtxWorkerRun
{
    /// <summary><c>ok</c>, <c>not-run</c>, or the CUDA error name that stopped the run.</summary>
    public string Status { get; set; } = "ok";
    public string? Message { get; set; }
    /// <summary>Outputs after a launch onto buffers pre-filled with 0xFF, base64, one per output buffer in buffer order.</summary>
    public List<string> FirstOutputs { get; set; } = new();
    /// <summary>Outputs after a second launch onto buffers pre-filled with 0x00.</summary>
    public List<string> SecondOutputs { get; set; } = new();
    /// <summary>Indices of buffers whose guard regions changed: the kernel wrote outside its allocation.</summary>
    public List<int> GuardViolations { get; set; } = new();
}

internal sealed class PtxWorkerTimingResult
{
    public List<double> CandidateMilliseconds { get; set; } = new();
    public List<double> IncumbentMilliseconds { get; set; } = new();
    public List<double> ControlFirstMilliseconds { get; set; } = new();
    public List<double> ControlSecondMilliseconds { get; set; } = new();
}
