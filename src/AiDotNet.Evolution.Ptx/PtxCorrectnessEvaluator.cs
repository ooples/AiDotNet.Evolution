using System.Globalization;

namespace AiDotNet.Evolution.Ptx;

/// <summary>Why a candidate was accepted or rejected by the correctness gate.</summary>
public enum PtxCorrectnessVerdict
{
    /// <summary>Every case matched the reference within tolerance.</summary>
    Passed = 0,
    /// <summary>The candidate failed static checks, the JIT, or a resource limit.</summary>
    CompileFailed = 1,
    /// <summary>A launch failed or faulted, or the worker died or hung running it.</summary>
    LaunchFailed = 2,
    /// <summary>The kernel wrote outside a buffer: into the guard region before or after it.</summary>
    OutOfBoundsWrite = 3,
    /// <summary>An output differed from the reference beyond tolerance, or was left unwritten.</summary>
    Mismatch = 4,
    /// <summary>The reference itself could not be produced, so nothing can be judged.</summary>
    ReferenceFailed = 5,
    /// <summary>No worker or device was available; the candidate was not judged.</summary>
    Unavailable = 6
}

/// <summary>The comparison on one shape.</summary>
public sealed class PtxCaseOutcome
{
    internal PtxCaseOutcome(PtxShapeCase shape, bool passed, string? failure, double maxAbsolute, double maxRelative, long compared, long mismatches, long unspecified)
    {
        Shape = shape;
        Passed = passed;
        Failure = failure;
        MaxAbsoluteError = maxAbsolute;
        MaxRelativeError = maxRelative;
        Compared = compared;
        Mismatches = mismatches;
        Unspecified = unspecified;
    }

    /// <summary>Gets the shape.</summary>
    public PtxShapeCase Shape { get; }
    /// <summary>Gets whether every compared element matched.</summary>
    public bool Passed { get; }
    /// <summary>Gets the first failure, such as <c>y[17]: candidate NaN, reference 0.25</c>, or <c>null</c>.</summary>
    public string? Failure { get; }
    /// <summary>Gets the largest finite absolute error.</summary>
    public double MaxAbsoluteError { get; }
    /// <summary>Gets the largest finite relative error.</summary>
    public double MaxRelativeError { get; }
    /// <summary>Gets how many elements were compared (each output element counts once per run).</summary>
    public long Compared { get; }
    /// <summary>Gets how many comparisons failed.</summary>
    public long Mismatches { get; }
    /// <summary>Gets how many elements the incumbent reference leaves unspecified (its own runs disagree).</summary>
    public long Unspecified { get; }
}

/// <summary>The correctness gate's decision for one candidate.</summary>
public sealed class PtxCorrectnessReport
{
    internal PtxCorrectnessReport(PtxCorrectnessVerdict verdict, string message, IReadOnlyList<PtxCaseOutcome> cases,
        PtxCompilationResult? compilation, string referenceIdentity)
    {
        Verdict = verdict;
        Message = message;
        Cases = cases;
        Compilation = compilation;
        ReferenceIdentity = referenceIdentity;
        MaxAbsoluteError = cases.Count == 0 ? 0 : cases.Max(c => c.MaxAbsoluteError);
        MaxRelativeError = cases.Count == 0 ? 0 : cases.Max(c => c.MaxRelativeError);
    }

    /// <summary>Gets the verdict.</summary>
    public PtxCorrectnessVerdict Verdict { get; }
    /// <summary>Gets whether the candidate may be timed.</summary>
    public bool Passed => Verdict == PtxCorrectnessVerdict.Passed;
    /// <summary>Gets a bounded explanation, suitable as repair feedback.</summary>
    public string Message { get; }
    /// <summary>Gets each judged shape, in order; empty when the candidate never ran.</summary>
    public IReadOnlyList<PtxCaseOutcome> Cases { get; }
    /// <summary>Gets the compilation, or <c>null</c>.</summary>
    public PtxCompilationResult? Compilation { get; }
    /// <summary>Gets the identity of the reference the outputs were judged against.</summary>
    public string ReferenceIdentity { get; }
    /// <summary>Gets the largest absolute error over every case.</summary>
    public double MaxAbsoluteError { get; }
    /// <summary>Gets the largest relative error over every case.</summary>
    public double MaxRelativeError { get; }
}

/// <summary>Runs a candidate on seeded inputs over fixed and fuzzed shapes and compares every output with a reference.</summary>
/// <remarks>
/// <para>Each case runs the candidate twice, once onto output buffers pre-filled with <c>0xFF</c> bytes (NaN for floats)
/// and once onto <c>0x00</c>, and both runs must match: an element the kernel never writes cannot match in both. Every
/// buffer sits between 4 KiB guard regions, so a write past either end is reported as
/// <see cref="PtxCorrectnessVerdict.OutOfBoundsWrite"/> even when the outputs happen to be right.</para>
/// <para>The reference is a trusted CPU implementation when one is supplied, otherwise the incumbent kernel run in the
/// same worker on the same inputs. A candidate that fails any case is rejected; nothing is timed until all pass.</para>
/// </remarks>
public sealed class PtxCorrectnessEvaluator
{
    private readonly PtxProgramCompiler _compiler;
    private readonly IPtxKernelReference? _reference;
    private readonly string? _incumbent;
    private readonly PtxIsolationOptions _isolation;

    /// <summary>Creates an evaluator.</summary>
    /// <param name="compiler">The contract's compiler.</param>
    /// <param name="reference">A CPU reference, or <c>null</c> to judge against <paramref name="incumbentSource"/>.</param>
    /// <param name="incumbentSource">The incumbent kernel; required when <paramref name="reference"/> is <c>null</c>.</param>
    /// <exception cref="ArgumentException">Neither a reference nor an incumbent is supplied.</exception>
    /// <remarks>The compiler's worker settings (timeout, memory and output bounds) apply to every run here too.</remarks>
    public PtxCorrectnessEvaluator(PtxProgramCompiler compiler, IPtxKernelReference? reference, string? incumbentSource)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        if (reference is null && string.IsNullOrWhiteSpace(incumbentSource))
            throw new ArgumentException("Supply a CPU reference or an incumbent kernel to judge against.", nameof(reference));
        _reference = reference;
        _incumbent = reference is null ? incumbentSource : null;
        _isolation = compiler.Isolation;
        ReferenceIdentity = reference?.Identity ?? "incumbent:" + Programs.ProgramSnapshot.Digest(incumbentSource ?? string.Empty);
    }

    /// <summary>Gets what outputs are judged against.</summary>
    public string ReferenceIdentity { get; }

    /// <summary>Compiles the candidate, runs every case and compares the outputs.</summary>
    /// <param name="candidateSource">The candidate PTX.</param>
    /// <param name="cancellationToken">Cancels the work and kills the worker.</param>
    /// <returns>The verdict and per-case evidence.</returns>
    public PtxCorrectnessReport Evaluate(string candidateSource, CancellationToken cancellationToken = default)
    {
        if (candidateSource is null) throw new ArgumentNullException(nameof(candidateSource));
        PtxKernelContract contract = _compiler.Contract;
        PtxCompilationResult compilation = _compiler.Compile(candidateSource, cancellationToken);
        if (!compilation.Succeeded)
            return Report(compilation.IsInfrastructureFailure ? PtxCorrectnessVerdict.Unavailable : PtxCorrectnessVerdict.CompileFailed,
                compilation.ToFeedback(), Array.Empty<PtxCaseOutcome>(), compilation);
        var launches = new List<PtxLaunchConfiguration> { compilation.Launch };
        var kernels = new List<PtxWorkerKernel> { _compiler.Kernel(candidateSource) };
        if (_incumbent is not null)
        {
            PtxSourceInspection incumbent = PtxSourceInspector.Inspect(_incumbent, contract);
            if (incumbent.HasErrors) return Report(PtxCorrectnessVerdict.ReferenceFailed, "The incumbent fails its own contract checks.", Array.Empty<PtxCaseOutcome>(), compilation);
            launches.Add(incumbent.Launch);
            kernels.Add(_compiler.Kernel(_incumbent));
        }
        // Smallest shapes first: a broken candidate is rejected on the cheapest case, with the most readable feedback.
        IReadOnlyList<PtxShapeCase> shapes = contract.GetValidationCases().OrderBy(c => ReadbackBytes(contract, c, 1)).ToList();
        var outcomes = new List<PtxCaseOutcome>();
        foreach (List<int> batch in Batches(contract, shapes, kernels.Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new PtxWorkerRequest { Operation = PtxWorkerOperation.Validate, Kernels = kernels };
            foreach (int index in batch) request.Cases.Add(PtxCaseBuilder.Build(contract, shapes[index], index, launches));
            PtxWorkerExchange exchange = _compiler.Transport.Exchange(request, cancellationToken);
            PtxWorkerResponse? response = exchange.Response;
            if (exchange.Status == PtxWorkerExchangeStatus.Unavailable || (response is not null && PtxProgramCompiler.IsInfrastructure(response.Status)))
                return Report(PtxCorrectnessVerdict.Unavailable, exchange.Describe(), outcomes, compilation);
            if (exchange.Status != PtxWorkerExchangeStatus.Completed || response is null)
                return Report(PtxCorrectnessVerdict.LaunchFailed, "Running " + shapes[batch[0]] + ": " + exchange.Describe(), outcomes, compilation);
            for (int position = 0; position < batch.Count; position++)
            {
                int index = batch[position];
                if (position >= response.Cases.Count)
                    return Report(PtxCorrectnessVerdict.LaunchFailed, "Running " + shapes[index] + ": " + exchange.Describe(), outcomes, compilation);
                PtxWorkerCaseResult result = response.Cases[position];
                PtxWorkerRun candidate = result.Runs[0];
                if (candidate.Status != "ok")
                    return Report(PtxCorrectnessVerdict.LaunchFailed, shapes[index] + ": the launch failed with " + candidate.Status + ".", outcomes, compilation);
                if (candidate.GuardViolations.Count > 0)
                    return Report(PtxCorrectnessVerdict.OutOfBoundsWrite, shapes[index] + ": the kernel wrote outside buffer(s) " +
                        string.Join(", ", candidate.GuardViolations.Select(b => BufferName(contract, b))) + ".", outcomes, compilation);
                PtxCaseOutcome outcome;
                try
                {
                    outcome = Compare(contract, shapes[index], request.Cases[position], candidate, _incumbent is null ? null : result.Runs[1]);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    return Report(PtxCorrectnessVerdict.ReferenceFailed, shapes[index] + ": the reference failed (" + exception.GetType().Name + ").", outcomes, compilation);
                }
                outcomes.Add(outcome);
                if (!outcome.Passed)
                    return Report(PtxCorrectnessVerdict.Mismatch, shapes[index] + ": " + outcome.Failure + " (" + outcome.Mismatches + " of " +
                        outcome.Compared + " comparisons failed).", outcomes, compilation);
            }
            if (response.Status != "ok")
                return Report(PtxCorrectnessVerdict.LaunchFailed, exchange.Describe(), outcomes, compilation);
        }
        return Report(PtxCorrectnessVerdict.Passed, "All " + outcomes.Count + " cases matched " + ReferenceIdentity + ".", outcomes, compilation);
    }

    private PtxCorrectnessReport Report(PtxCorrectnessVerdict verdict, string message, IReadOnlyList<PtxCaseOutcome> cases, PtxCompilationResult? compilation) =>
        new(verdict, message.Length > 2048 ? message.Substring(0, 2048) : message, cases.ToList().AsReadOnly(), compilation, ReferenceIdentity);

    private IEnumerable<List<int>> Batches(PtxKernelContract contract, IReadOnlyList<PtxShapeCase> shapes, int kernels)
    {
        var batch = new List<int>();
        long bytes = 0, budget = Math.Min(_isolation.MaxReadbackBytes, _isolation.MaxResponseBytes / 2);
        for (int index = 0; index < shapes.Count; index++)
        {
            long readback = ReadbackBytes(contract, shapes[index], kernels);
            if (readback > budget) throw new ArgumentException("Case " + shapes[index] + " reads back " + readback + " bytes, more than the isolation bound allows.");
            if (batch.Count > 0 && bytes + readback > budget)
            {
                yield return batch;
                batch = new List<int>();
                bytes = 0;
            }
            batch.Add(index);
            bytes += readback;
        }
        if (batch.Count > 0) yield return batch;
    }

    private PtxCaseOutcome Compare(PtxKernelContract contract, PtxShapeCase shape, PtxWorkerCase item, PtxWorkerRun candidate, PtxWorkerRun? incumbent)
    {
        Dictionary<string, byte[]>? cpu = _reference is null ? null : PtxCaseBuilder.RunReference(contract, _reference, shape, item);
        double maxAbsolute = 0, maxRelative = 0;
        long compared = 0, mismatches = 0, unspecified = 0;
        string? failure = null;
        int output = 0, buffer = 0;
        foreach (PtxKernelParameter parameter in contract.Parameters.Where(p => p.Kind == PtxParameterKind.Buffer))
        {
            PtxWorkerBuffer wire = item.Buffers[buffer++];
            if (!parameter.IsOutput) continue;
            PtxTolerance tolerance = parameter.Tolerance ?? contract.Tolerance;
            int size = parameter.ElementType.Size();
            PtxWorkerElementType type = parameter.ElementType.ToWire();
            byte[] first = Convert.FromBase64String(candidate.FirstOutputs[output]), second = Convert.FromBase64String(candidate.SecondOutputs[output]);
            byte[] reference, referenceCheck;
            if (cpu is not null) reference = referenceCheck = cpu[parameter.Name];
            else
            {
                PtxWorkerRun run = incumbent ?? throw new InvalidOperationException("No reference.");
                if (run.Status != "ok" || run.GuardViolations.Count > 0) throw new InvalidOperationException("The incumbent reference failed.");
                reference = Convert.FromBase64String(run.FirstOutputs[output]);
                referenceCheck = Convert.FromBase64String(run.SecondOutputs[output]);
            }
            output++;
            long expectedBytes = wire.Elements * size;
            if (first.Length != expectedBytes || second.Length != expectedBytes || reference.Length != expectedBytes || referenceCheck.Length != expectedBytes)
                throw new InvalidDataException("An output has the wrong length.");
            for (long element = 0; element < wire.Elements; element++)
            {
                int at = checked((int)(element * size));
                double r = PtxDeterministicFill.Read(reference.AsSpan(at, size), type);
                if (cpu is null && !tolerance.Accepts(PtxDeterministicFill.Read(referenceCheck.AsSpan(at, size), type), r))
                {
                    unspecified++;
                    continue;
                }
                foreach (byte[] run in new[] { first, second })
                {
                    double c = PtxDeterministicFill.Read(run.AsSpan(at, size), type);
                    compared++;
                    if (double.IsFinite(c) && double.IsFinite(r))
                    {
                        double absolute = Math.Abs(c - r);
                        maxAbsolute = Math.Max(maxAbsolute, absolute);
                        if (r != 0) maxRelative = Math.Max(maxRelative, absolute / Math.Abs(r));
                    }
                    if (tolerance.Accepts(c, r)) continue;
                    mismatches++;
                    failure ??= parameter.Name + "[" + element.ToString(CultureInfo.InvariantCulture) + "]: candidate " +
                        c.ToString("R", CultureInfo.InvariantCulture) + ", reference " + r.ToString("R", CultureInfo.InvariantCulture) +
                        (run == first ? "" : " (on the second run: the element may be unwritten)");
                }
            }
        }
        return new PtxCaseOutcome(shape, mismatches == 0, failure, maxAbsolute, maxRelative, compared, mismatches, unspecified);
    }

    private static long ReadbackBytes(PtxKernelContract contract, PtxShapeCase shape, int kernels) =>
        checked(contract.Parameters.Where(p => p.IsOutput).Sum(p => checked(PtxKernelContract.BufferElements(p, shape.Symbols) * p.ElementType.Size())) * 2 * kernels);

    private static string BufferName(PtxKernelContract contract, int bufferIndex) =>
        contract.Parameters.Where(p => p.Kind == PtxParameterKind.Buffer).ElementAtOrDefault(bufferIndex)?.Name ?? "#" + bufferIndex;
}