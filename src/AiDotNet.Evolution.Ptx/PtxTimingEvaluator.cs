using System.Diagnostics.CodeAnalysis;
namespace AiDotNet.Evolution.Ptx;

/// <summary>How kernels are timed.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxTimingOptions
{
    /// <summary>Gets or sets warm-up runs of each kernel before any sample. Default: 10.</summary>
    public int Warmup { get; set; } = 10;
    /// <summary>Gets or sets candidate/incumbent pairs, at least 7. Default: 31.</summary>
    public int Pairs { get; set; } = 31;
    /// <summary>Gets or sets incumbent/incumbent control pairs for the noise floor, at least 7. Default: 21.</summary>
    public int ControlPairs { get; set; } = 21;
    /// <summary>Gets or sets back-to-back launches inside one CUDA-event sample, so microsecond kernels are measurable. Default: 10.</summary>
    public int LaunchesPerSample { get; set; } = 10;
    /// <summary>Gets or sets the least median speedup worth promoting. Default: 1.05, the Tensors gate.</summary>
    public double MinimumPromotionRatio { get; set; } = 1.05;
    /// <summary>Gets or sets the largest allowed candidate/incumbent P95 ratio. Default: 1.0, the Tensors gate.</summary>
    public double MaximumP95LatencyRatio { get; set; } = 1.0;
    /// <summary>Gets or sets the machine-wide lock held while timing, or <c>null</c> for none. Default: <c>Global\AiDotNetBenchLock</c>.</summary>
    /// <remarks>Every process that times on this machine takes the same named mutex, so no two measurements overlap.</remarks>
    public string? MachineLockName { get; set; } = @"Global\AiDotNetBenchLock";
    /// <summary>Gets or sets how long to wait for the machine lock. Default: 30 minutes.</summary>
    public TimeSpan MachineLockTimeout { get; set; } = TimeSpan.FromMinutes(30);

    internal PtxTimingOptions Snapshot()
    {
        var copy = (PtxTimingOptions)MemberwiseClone();
        if (Warmup is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(Warmup));
        if (Pairs is < PtxPairedTimingEvidence.MinimumSampleCount or > 10_000) throw new ArgumentOutOfRangeException(nameof(Pairs));
        if (ControlPairs is < PtxPairedTimingEvidence.MinimumSampleCount or > 10_000) throw new ArgumentOutOfRangeException(nameof(ControlPairs));
        if (LaunchesPerSample is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(LaunchesPerSample));
        if (!double.IsFinite(MinimumPromotionRatio) || MinimumPromotionRatio < 1) throw new ArgumentOutOfRangeException(nameof(MinimumPromotionRatio));
        if (!double.IsFinite(MaximumP95LatencyRatio) || MaximumP95LatencyRatio <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumP95LatencyRatio));
        if (MachineLockName is { } name && (name.Length is 0 or > 250 || name.Any(char.IsControl))) throw new ArgumentException("Invalid machine lock name.");
        if (MachineLockTimeout < TimeSpan.Zero || MachineLockTimeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(MachineLockTimeout));
        return copy;
    }

    internal string Identity => string.Join(",", "ptx-timing-v1", Warmup, Pairs, ControlPairs, LaunchesPerSample,
        MinimumPromotionRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        MaximumP95LatencyRatio.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>The outcome of one paired timing replay.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxTimingReport
{
    internal PtxTimingReport(bool completed, string message, PtxPairedTimingEvidence? evidence, PtxDeviceInfo? device, bool qualifies, bool lockAbandoned,
        string candidateSha256, string incumbentSha256)
    {
        CandidateSha256 = candidateSha256;
        IncumbentSha256 = incumbentSha256;
        Completed = completed;
        Message = message;
        Evidence = evidence;
        Device = device;
        QualifiesForPromotion = qualifies;
        MachineLockWasAbandoned = lockAbandoned;
    }

    /// <summary>Gets whether timing completed.</summary>
    public bool Completed { get; }
    /// <summary>Gets an explanation.</summary>
    public string Message { get; }
    /// <summary>Gets the paired evidence, or <c>null</c> when timing did not complete.</summary>
    public PtxPairedTimingEvidence? Evidence { get; }
    /// <summary>Gets the device timed on.</summary>
    public PtxDeviceInfo? Device { get; }
    /// <summary>Gets whether the candidate passes the promotion gate.</summary>
    public bool QualifiesForPromotion { get; }
    /// <summary>Gets whether the machine lock was taken over from a process that died holding it.</summary>
    public bool MachineLockWasAbandoned { get; }
    /// <summary>Gets the SHA-256 of the candidate source that was timed.</summary>
    public string CandidateSha256 { get; }
    /// <summary>Gets the SHA-256 of the incumbent source it was timed against.</summary>
    public string IncumbentSha256 { get; }
}

/// <summary>Times a candidate against the incumbent with CUDA events in one isolated worker, under the machine-wide lock.</summary>
/// <remarks>
/// Both kernels are loaded in the same worker and context and share the same buffers at
/// <see cref="PtxKernelContract.TimingShape"/>. After warm-up, identical incumbent/incumbent control pairs calibrate the
/// noise floor, then candidate/incumbent pairs are measured with alternating order, as the Tensors paired replay does.
/// Timing never establishes correctness: run <see cref="PtxCorrectnessEvaluator"/> first and time only what passed.
/// </remarks>
[Experimental("AIDEVO005")]
public sealed class PtxTimingEvaluator
{
    private readonly PtxProgramCompiler _compiler;
    private readonly PtxTimingOptions _options;

    /// <summary>Creates a timing evaluator.</summary>
    /// <param name="compiler">The contract's compiler.</param>
    /// <param name="options">Timing settings, or <c>null</c> for defaults.</param>
    public PtxTimingEvaluator(PtxProgramCompiler compiler, PtxTimingOptions? options = null)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _options = (options ?? new PtxTimingOptions()).Snapshot();
    }

    /// <summary>Gets the identity of the timing protocol.</summary>
    public string Identity => _options.Identity;

    internal PtxTimingOptions Options => _options;

    /// <summary>Measures the candidate against the incumbent.</summary>
    /// <param name="candidateSource">A candidate that already passed correctness.</param>
    /// <param name="incumbentSource">The incumbent.</param>
    /// <param name="cancellationToken">Cancels the work and kills the worker.</param>
    /// <returns>The evidence and the promotion decision.</returns>
    public PtxTimingReport Measure(string candidateSource, string incumbentSource, CancellationToken cancellationToken = default)
    {
        if (candidateSource is null) throw new ArgumentNullException(nameof(candidateSource));
        if (incumbentSource is null) throw new ArgumentNullException(nameof(incumbentSource));
        PtxKernelContract contract = _compiler.Contract;
        string candidateSha = Programs.ProgramSnapshot.Digest(candidateSource), incumbentSha = Programs.ProgramSnapshot.Digest(incumbentSource);
        PtxSourceInspection candidate = PtxSourceInspector.Inspect(candidateSource, contract), incumbent = PtxSourceInspector.Inspect(incumbentSource, contract);
        if (candidate.HasErrors || incumbent.HasErrors) return new(false, "A kernel fails its static contract checks.", null, null, false, false, candidateSha, incumbentSha);
        var shape = new PtxShapeCase("timing", contract.TimingShape);
        var request = new PtxWorkerRequest
        {
            Operation = PtxWorkerOperation.Time,
            Kernels = { _compiler.Kernel(candidateSource), _compiler.Kernel(incumbentSource) },
            Cases = { PtxCaseBuilder.Build(contract, shape, 0, new[] { candidate.Launch, incumbent.Launch }) },
            Timing = new PtxWorkerTimingPlan
            {
                Warmup = _options.Warmup,
                Pairs = _options.Pairs,
                ControlPairs = _options.ControlPairs,
                LaunchesPerSample = _options.LaunchesPerSample
            }
        };
        foreach (PtxWorkerBuffer buffer in request.Cases[0].Buffers) buffer.IsOutput &= buffer.IsInput; // Nothing is read back while timing.
        bool abandoned = false;
        PtxWorkerExchange exchange;
        try
        {
            exchange = PtxMachineLock.Run(_options.MachineLockName, _options.MachineLockTimeout, cancellationToken,
                wasAbandoned =>
                {
                    abandoned = wasAbandoned;
                    return _compiler.Transport.Exchange(request, cancellationToken);
                });
        }
        catch (TimeoutException exception)
        {
            return new(false, exception.Message, null, null, false, false, candidateSha, incumbentSha);
        }
        PtxWorkerResponse? response = exchange.Response;
        PtxDeviceInfo? device = response?.Device is { } wire ? new PtxDeviceInfo(wire) : null;
        if (exchange.Status != PtxWorkerExchangeStatus.Completed || response is null || response.Status != "ok" || response.Timing is not { } timing)
            return new(false, exchange.Describe(), null, device, false, abandoned, candidateSha, incumbentSha);
        try
        {
            var controls = timing.ControlFirstMilliseconds.Zip(timing.ControlSecondMilliseconds, (a, b) => new PtxPairedSample(a, b)).ToArray();
            var pairs = timing.CandidateMilliseconds.Zip(timing.IncumbentMilliseconds, (a, b) => new PtxPairedSample(a, b)).ToArray();
            if (controls.Length != _options.ControlPairs || pairs.Length != _options.Pairs) throw new InvalidDataException("The worker returned the wrong number of samples.");
            var evidence = new PtxPairedTimingEvidence(pairs, PtxPairedTimingEvidence.NoiseRatio(controls));
            bool qualifies = evidence.QualifiesForPromotion(_options.MinimumPromotionRatio, _options.MaximumP95LatencyRatio);
            string message = "Median speedup " + evidence.MedianSpeedup.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                "x (lower 5% " + evidence.LowerSpeedupBound.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) +
                "x, noise " + evidence.CalibratedNoiseRatio.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + "x): " +
                (qualifies ? "qualifies for promotion." : "does not qualify for promotion.");
            return new(true, message, evidence, device, qualifies, abandoned, candidateSha, incumbentSha);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return new(false, "The worker returned unusable samples: " + exception.Message, null, device, false, abandoned, candidateSha, incumbentSha);
        }
    }
}

/// <summary>Holds a named machine-wide mutex around GPU timing, on one thread, as a mutex requires.</summary>
internal static class PtxMachineLock
{
    internal static T Run<T>(string? name, TimeSpan timeout, CancellationToken cancellationToken, Func<bool, T> work)
    {
        if (name is null) return work(false);
        using var mutex = new Mutex(false, name);
        bool abandoned = false, owned = false;
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!owned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    owned = mutex.WaitOne(TimeSpan.FromMilliseconds(250));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                    abandoned = true;
                }
                if (!owned && DateTime.UtcNow >= deadline) throw new TimeoutException("The machine timing lock '" + name + "' was not acquired within " + timeout + ".");
            }
            return work(abandoned);
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }
}