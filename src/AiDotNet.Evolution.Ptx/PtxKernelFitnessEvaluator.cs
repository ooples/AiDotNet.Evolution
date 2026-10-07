using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Ptx;

/// <summary>Scores PTX candidates for program evolution: correctness gate first, then paired timing against the incumbent.</summary>
/// <remarks>
/// <para>Quality is the median within-pair speedup over the incumbent (maximize), so 1.0 means "as fast as the
/// incumbent". A candidate that fails to compile, launch or match the reference fails without being timed. Descriptors
/// carry the speedup, its lower bound and noise floor, the candidate's median and IQR, registers, shared and local
/// memory, the worst output errors, and whether the promotion gate passed, for archives and for the next prompt.</para>
/// <para>When no worker or device is available the evaluator throws instead of failing the candidate, so a missing GPU
/// stops a run rather than silently scoring every proposal as broken.</para>
/// </remarks>
public sealed class PtxKernelFitnessEvaluator : IProgramFitnessEvaluator
{
    private readonly PtxCorrectnessEvaluator _correctness;
    private readonly PtxTimingEvaluator _timing;
    private readonly string _incumbent;

    /// <summary>Creates an evaluator.</summary>
    /// <param name="correctness">The correctness gate.</param>
    /// <param name="timing">The paired timing evaluator.</param>
    /// <param name="incumbentSource">The incumbent every candidate is timed against.</param>
    public PtxKernelFitnessEvaluator(PtxCorrectnessEvaluator correctness, PtxTimingEvaluator timing, string incumbentSource)
    {
        _correctness = correctness ?? throw new ArgumentNullException(nameof(correctness));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _incumbent = string.IsNullOrWhiteSpace(incumbentSource) ? throw new ArgumentException("The incumbent is required.", nameof(incumbentSource)) : incumbentSource;
        VersionHash = EvolutionHash.Combine(new[] { "ptx-kernel-fitness-v1", correctness.ReferenceIdentity, timing.Identity, ProgramSnapshot.Digest(incumbentSource) });
    }

    /// <inheritdoc/>
    public string Id => "ptx-kernel-fitness";

    /// <inheritdoc/>
    public string VersionHash { get; }

    /// <inheritdoc/>
    public ValueTask<EvolutionTaskResult> EvaluateAsync(ProgramGenome candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (candidate is null) throw new ArgumentNullException(nameof(candidate));
        if (context is null) throw new ArgumentNullException(nameof(context));
        // The machine timing lock is a mutex, which belongs to one thread: run the whole evaluation on a dedicated one.
        return new ValueTask<EvolutionTaskResult>(Task.Factory.StartNew(() => Evaluate(candidate.Source, cancellationToken), cancellationToken,
            TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }

    private EvolutionTaskResult Evaluate(string source, CancellationToken cancellationToken)
    {
        PtxCorrectnessReport correctness = _correctness.Evaluate(source, cancellationToken);
        if (correctness.Verdict == PtxCorrectnessVerdict.Unavailable)
            throw new InvalidOperationException("No PTX worker or CUDA device is available: " + correctness.Message);
        if (!correctness.Passed) return EvolutionTaskResult.Failed("ptx_" + Code(correctness.Verdict), Bound(correctness.Message));
        PtxTimingReport timing = _timing.Measure(source, _incumbent, cancellationToken);
        if (!timing.Completed || timing.Evidence is not { } evidence) return EvolutionTaskResult.Failed("ptx_timing_failed", Bound(timing.Message));
        PtxKernelResources? resources = correctness.Compilation?.Resources;
        var descriptors = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["speedup"] = evidence.MedianSpeedup,
            ["lower_speedup"] = evidence.LowerSpeedupBound,
            ["noise_ratio"] = evidence.CalibratedNoiseRatio,
            ["candidate_median_ms"] = evidence.CandidateTiming.Median,
            ["candidate_iqr_ms"] = evidence.CandidateTiming.Iqr,
            ["incumbent_median_ms"] = evidence.IncumbentTiming.Median,
            ["registers"] = resources?.Registers ?? 0,
            ["shared_bytes"] = (resources?.StaticSharedBytes ?? 0) + (resources?.DynamicSharedBytes ?? 0),
            ["local_bytes"] = resources?.LocalBytes ?? 0,
            ["max_abs_error"] = correctness.MaxAbsoluteError,
            ["max_rel_error"] = correctness.MaxRelativeError,
            ["qualifies"] = timing.QualifiesForPromotion ? 1 : 0
        };
        return EvolutionTaskResult.Completed(evidence.MedianSpeedup, descriptors, EvolutionOptimizationDirection.Maximize);
    }

    private static string Code(PtxCorrectnessVerdict verdict) => verdict switch
    {
        PtxCorrectnessVerdict.CompileFailed => "compile_failed",
        PtxCorrectnessVerdict.LaunchFailed => "launch_failed",
        PtxCorrectnessVerdict.OutOfBoundsWrite => "out_of_bounds_write",
        PtxCorrectnessVerdict.Mismatch => "mismatch",
        _ => "reference_failed"
    };

    private static string Bound(string message) => message.Length > 1024 ? message.Substring(0, 1024) : message;
}