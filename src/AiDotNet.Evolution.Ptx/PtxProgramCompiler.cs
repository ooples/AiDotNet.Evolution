using System.Text;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>Validates, JIT-loads and resource-checks PTX candidates for one kernel contract.</summary>
/// <remarks>
/// <para>Compilation has three gates, each reported as <see cref="CompilationDiagnostic"/>s. Static checks need no GPU:
/// the header directives, the <c>.target</c> against the contract's SM, and the <c>.entry</c> signature against the
/// contract's parameters. The driver JIT (<c>cuModuleLoadDataEx</c> with info and error logs) runs in the isolated
/// worker, and its ptxas messages become diagnostics with line numbers. Finally the loaded kernel's registers, static
/// plus dynamic shared memory, local (spill) memory and launchable threads are checked against
/// <see cref="PtxKernelContract.Target"/> and the device.</para>
/// <para>As an <see cref="IProgramCompiler"/> it plugs into <see cref="ProgramImprovement"/>: the single file is
/// <see cref="FileName"/>, the catalog is the entry body's lines, and a built artifact's image is the PTX text.
/// Compilation does not run the kernel and proves nothing about correctness or speed.</para>
/// </remarks>
public sealed class PtxProgramCompiler : IProgramCompiler
{
    /// <summary>The logical file name of the kernel in a <see cref="ProgramSnapshot"/>.</summary>
    public const string FileName = "kernel.ptx";

    private readonly IPtxWorkerTransport _transport;

    /// <summary>Creates a compiler for a contract.</summary>
    /// <param name="contract">The kernel contract every candidate must satisfy.</param>
    /// <param name="isolation">Worker settings, or <c>null</c> for defaults.</param>
    public PtxProgramCompiler(PtxKernelContract contract, PtxIsolationOptions? isolation = null)
        : this(contract, (isolation ?? new PtxIsolationOptions()).Snapshot(), processWorker: true)
    {
    }

    private PtxProgramCompiler(PtxKernelContract contract, PtxIsolationOptions isolation, bool processWorker)
        : this(contract, processWorker ? new PtxWorkerProcessTransport(isolation) : throw new ArgumentException("A worker is required."), isolation)
    {
    }

    internal PtxProgramCompiler(PtxKernelContract contract, IPtxWorkerTransport transport, PtxIsolationOptions? isolation = null)
    {
        Contract = contract ?? throw new ArgumentNullException(nameof(contract));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Isolation = (isolation ?? new PtxIsolationOptions()).Snapshot();
        Fingerprint = ProgramSnapshot.Digest(string.Join("\n", "evolution-ptx-compiler-v1", contract.Fingerprint, transport.Identity,
            typeof(PtxProgramCompiler).Assembly.ManifestModule.ModuleVersionId.ToString("D")));
    }

    /// <summary>Gets the contract.</summary>
    public PtxKernelContract Contract { get; }

    /// <summary>Gets the identity of the compiler, its contract and its worker.</summary>
    public string Fingerprint { get; }

    internal IPtxWorkerTransport Transport => _transport;

    /// <summary>The worker settings every evaluator built on this compiler shares, so one timeout and one set of bounds apply.</summary>
    internal PtxIsolationOptions Isolation { get; }

    /// <summary>Validates and JIT-loads one candidate.</summary>
    /// <param name="source">The PTX text, optionally starting with a launch header.</param>
    /// <param name="cancellationToken">Cancels the work and kills the worker.</param>
    /// <returns>The diagnostics, resources and selected launch.</returns>
    public PtxCompilationResult Compile(string source, CancellationToken cancellationToken = default)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        PtxSourceInspection inspection = PtxSourceInspector.Inspect(source, Contract);
        var diagnostics = new List<CompilationDiagnostic>(inspection.Diagnostics);
        if (inspection.HasErrors) return new(source, diagnostics, inspection.Launch, null, null, string.Empty, string.Empty, false);
        var request = new PtxWorkerRequest
        {
            Operation = PtxWorkerOperation.Compile,
            Kernels = { Kernel(source) }
        };
        PtxWorkerExchange exchange = _transport.Exchange(request, cancellationToken);
        if (exchange.Status != PtxWorkerExchangeStatus.Completed || exchange.Response is not { } response || response.Kernels.Count != 1)
        {
            bool infrastructure = exchange.Status == PtxWorkerExchangeStatus.Unavailable || (exchange.Response is { } failed && IsInfrastructure(failed.Status));
            diagnostics.Add(Error("PTX100", "The JIT did not complete: " + exchange.Describe()));
            return new(source, diagnostics, inspection.Launch, null, exchange.Response?.Device is { } d ? new PtxDeviceInfo(d) : null,
                string.Empty, string.Empty, infrastructure);
        }
        PtxDeviceInfo? device = response.Device is { } wireDevice ? new PtxDeviceInfo(wireDevice) : null;
        PtxWorkerCompiled kernel = response.Kernels[0];
        diagnostics.AddRange(PtxJitLog.Parse(kernel.ErrorLog, includeInfo: false));
        diagnostics.AddRange(PtxJitLog.Parse(kernel.InfoLog, includeInfo: false).Where(d => d.Severity != CompilationDiagnosticSeverity.Info));
        if (!kernel.Loaded)
        {
            if (!diagnostics.Any(d => d.Severity == CompilationDiagnosticSeverity.Error))
                diagnostics.Add(Error("PTX101", "The driver refused the module: " + (kernel.ErrorCode ?? "unknown error") + "."));
            return new(source, diagnostics, inspection.Launch, null, device, kernel.InfoLog, kernel.ErrorLog, false);
        }
        int dynamicShared = (int)Math.Min(int.MaxValue, Contract.GetValidationCases()
            .Max(c => inspection.Launch.DynamicSharedMemoryBytes.Evaluate(inspection.Launch.WithBlockSymbols(c.Symbols))));
        var resources = new PtxKernelResources(kernel.Registers, kernel.StaticSharedBytes, dynamicShared, kernel.LocalBytes,
            kernel.ConstantBytes, kernel.MaxThreadsPerBlock, kernel.WallTimeMilliseconds);
        bool environment = false;
        if (device is not null && device.SmVersion != Contract.Target.SmVersion)
        {
            environment = true;
            diagnostics.Add(Error("PTX110", "The device is sm_" + device.SmVersion + " but the contract targets " + Contract.Target.TargetName +
                "; evidence must come from the target architecture."));
        }
        PtxTargetLimits limits = Contract.Target;
        if (resources.Registers > limits.MaxRegistersPerThread)
            diagnostics.Add(Error("PTX111", "Uses " + resources.Registers + " registers per thread; the limit is " + limits.MaxRegistersPerThread + "."));
        int sharedLimit = device is null ? limits.MaxSharedMemoryPerBlockBytes : Math.Min(limits.MaxSharedMemoryPerBlockBytes, device.MaxSharedMemoryPerBlockOptin);
        if ((long)resources.StaticSharedBytes + resources.DynamicSharedBytes > sharedLimit)
            diagnostics.Add(Error("PTX112", "Uses " + resources.StaticSharedBytes + " static + " + resources.DynamicSharedBytes +
                " dynamic shared bytes per block; the limit is " + sharedLimit + "."));
        if (resources.LocalBytes > limits.MaxLocalBytesPerThread)
            diagnostics.Add(Error("PTX113", "Spills " + resources.LocalBytes + " local bytes per thread; the limit is " + limits.MaxLocalBytesPerThread + "."));
        if (inspection.Launch.ThreadsPerBlock > resources.MaxThreadsPerBlock)
            diagnostics.Add(Error("PTX114", "The block has " + inspection.Launch.ThreadsPerBlock + " threads but this kernel's register use allows at most " +
                resources.MaxThreadsPerBlock + "."));
        return new(source, diagnostics, inspection.Launch, resources, device, kernel.InfoLog, kernel.ErrorLog, environment);
    }

    /// <inheritdoc />
    public IReadOnlyList<EditTarget> Catalog(ProgramSnapshot source, CancellationToken cancellationToken = default)
    {
        string text = Single(source);
        PtxSourceInspection inspection = PtxSourceInspector.Inspect(text, Contract);
        if (inspection.HasErrors) throw new ArgumentException("The parent must pass the static PTX checks.");
        return PtxSourceEditor.Catalog(text, inspection)
            .Select(t => new EditTarget(FileName, t.Start, t.Length, t.Kind, t.Sha256)).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public ProgramSnapshot Apply(ProgramSnapshot parent, PatchPlan plan, CancellationToken cancellationToken = default)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (plan.ParentFingerprint != parent.Fingerprint || string.IsNullOrWhiteSpace(plan.Hypothesis) || plan.Hypothesis.Length > 1024 ||
            plan.Edits is null || plan.Edits.Count is < 1 or > 64)
            throw new ArgumentException("The plan does not identify a bounded original snapshot.");
        string text = Single(parent);
        PtxSourceInspection inspection = PtxSourceInspector.Inspect(text, Contract);
        IReadOnlyList<PtxLineTarget> catalog = PtxSourceEditor.Catalog(text, inspection);
        var edits = plan.Edits.Select(edit =>
        {
            PtxLineTarget? target = edit?.Target is { } t && t.File == FileName
                ? catalog.FirstOrDefault(c => c.Start == t.Start && c.Length == t.Length && c.Sha256 == t.ExpectedHash)
                : null;
            if (target is null || edit?.Replacement is null) throw new ArgumentException("The edit does not match an original line target.");
            return (target.Line, target.Sha256, edit.Replacement);
        }).ToList();
        string patched = PtxSourceEditor.Apply(text, catalog, edits, 64);
        return new ProgramSnapshot(new Dictionary<string, string>(StringComparer.Ordinal) { [FileName] = patched });
    }

    /// <inheritdoc />
    public ProgramBuild Build(ProgramSnapshot source, CancellationToken cancellationToken = default)
    {
        PtxCompilationResult result = Compile(Single(source), cancellationToken);
        if (!result.Succeeded) return new ProgramBuild(null, result.ToFeedback());
        return new ProgramBuild(new ProgramArtifact(source, Fingerprint, Contract.Fingerprint, Encoding.UTF8.GetBytes(result.Source)), string.Empty);
    }

    internal PtxWorkerKernel Kernel(string source) => new()
    {
        Ptx = source,
        EntryPoint = Contract.EntryPoint,
        MaxRegisters = Contract.Target.MaxRegistersPerThread,
        OptimizationLevel = 4
    };

        /// <summary>A response-level failure is environmental: candidate faults are reported per kernel or per run, never here.</summary>
    internal static bool IsInfrastructure(string status) =>
        status is "cuda-unavailable" or "no-device" or "invalid-request" or "worker-error" || status.StartsWith("CUDA_ERROR", StringComparison.Ordinal);

    private static string Single(ProgramSnapshot source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (source.Files.Count != 1 || !source.Files.TryGetValue(FileName, out string? text))
            throw new ArgumentException("A PTX program is exactly one file named " + FileName + ".", nameof(source));
        return text;
    }

    private static CompilationDiagnostic Error(string code, string message) => new()
    {
        Code = code,
        Message = message,
        Severity = CompilationDiagnosticSeverity.Error,
        Tool = PtxSourceInspector.Tool
    };
}

/// <summary>Whether a usable CUDA device is reachable through the isolated worker.</summary>
public sealed class PtxDeviceProbe
{
    private PtxDeviceProbe(PtxDeviceInfo? device, string message)
    {
        Device = device;
        Message = message;
    }

    /// <summary>Gets the device, or <c>null</c> when none is available.</summary>
    public PtxDeviceInfo? Device { get; }
    /// <summary>Gets whether a device answered.</summary>
    public bool IsAvailable => Device is not null;
    /// <summary>Gets why no device is available, or <c>ok</c>.</summary>
    public string Message { get; }

    /// <summary>Starts a worker that initialises the driver and describes the device, without loading any kernel.</summary>
    /// <param name="isolation">Worker settings, or <c>null</c> for defaults.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The probe result; never throws for a missing driver or device.</returns>
    public static PtxDeviceProbe Run(PtxIsolationOptions? isolation = null, CancellationToken cancellationToken = default) =>
        Run(new PtxWorkerProcessTransport(isolation ?? new PtxIsolationOptions()), cancellationToken);

    internal static PtxDeviceProbe Run(IPtxWorkerTransport transport, CancellationToken cancellationToken)
    {
        PtxWorkerExchange exchange = transport.Exchange(new PtxWorkerRequest { Operation = PtxWorkerOperation.Probe }, cancellationToken);
        if (exchange.Status == PtxWorkerExchangeStatus.Completed && exchange.Response is { Status: "ok", Device: { } device })
            return new(new PtxDeviceInfo(device), "ok");
        return new(null, exchange.Describe());
    }

    /// <summary>Formats the result.</summary>
    /// <returns>The device identity or the reason none is available.</returns>
    public override string ToString() => Device?.Identity ?? Message;
}