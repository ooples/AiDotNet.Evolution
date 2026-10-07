using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace AiDotNet.Evolution.Ptx;

/// <summary>The incumbent's measured profile, shown to the model so rewrites aim at the real bottleneck.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxIncumbentProfile
{
    /// <summary>Creates a profile.</summary>
    /// <param name="medianMilliseconds">The incumbent's median time at the timing shape.</param>
    /// <param name="iqrMilliseconds">Its interquartile range.</param>
    /// <param name="registers">Registers per thread.</param>
    /// <param name="sharedBytes">Static plus dynamic shared memory per block.</param>
    /// <param name="localBytes">Local (spill) bytes per thread.</param>
    /// <param name="bottleneck">What profiling says limits it, such as <c>DRAM-bound: 86% of peak bandwidth</c>; at most 1024 characters.</param>
    public PtxIncumbentProfile(double medianMilliseconds, double iqrMilliseconds, int registers, int sharedBytes, int localBytes, string? bottleneck)
    {
        if (!double.IsFinite(medianMilliseconds) || medianMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(medianMilliseconds));
        if (!double.IsFinite(iqrMilliseconds) || iqrMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(iqrMilliseconds));
        if (registers < 0 || sharedBytes < 0 || localBytes < 0) throw new ArgumentOutOfRangeException(nameof(registers));
        if (bottleneck?.Length > 1024) throw new ArgumentException("The bottleneck note exceeds 1024 characters.", nameof(bottleneck));
        MedianMilliseconds = medianMilliseconds;
        IqrMilliseconds = iqrMilliseconds;
        Registers = registers;
        SharedBytes = sharedBytes;
        LocalBytes = localBytes;
        Bottleneck = bottleneck ?? string.Empty;
    }

    /// <summary>Gets the median time.</summary>
    public double MedianMilliseconds { get; }
    /// <summary>Gets the interquartile range.</summary>
    public double IqrMilliseconds { get; }
    /// <summary>Gets registers per thread.</summary>
    public int Registers { get; }
    /// <summary>Gets shared memory per block.</summary>
    public int SharedBytes { get; }
    /// <summary>Gets local bytes per thread.</summary>
    public int LocalBytes { get; }
    /// <summary>Gets the measured bottleneck.</summary>
    public string Bottleneck { get; }

    /// <summary>Builds a profile from a measurement and the incumbent's resources.</summary>
    /// <param name="timing">The incumbent's timing statistics.</param>
    /// <param name="resources">The incumbent's JIT resources.</param>
    /// <param name="bottleneck">What profiling says limits it.</param>
    /// <returns>The profile.</returns>
    public static PtxIncumbentProfile From(PtxTimingStatistics timing, PtxKernelResources resources, string? bottleneck = null)
    {
        if (timing is null) throw new ArgumentNullException(nameof(timing));
        if (resources is null) throw new ArgumentNullException(nameof(resources));
        return new(timing.Median, timing.Iqr, resources.Registers, resources.StaticSharedBytes + resources.DynamicSharedBytes, resources.LocalBytes, bottleneck);
    }

    internal string Identity => string.Join("|", MedianMilliseconds.ToString("R", CultureInfo.InvariantCulture),
        IqrMilliseconds.ToString("R", CultureInfo.InvariantCulture), Registers, SharedBytes, LocalBytes, Bottleneck);
}

/// <summary>Bounds, prices and identities for the opt-in PTX rewrite proposal loop.</summary>
/// <remarks>Prices are synthetic work units, not money, and must match the evaluator's cost-unit identity. Every model
/// request, parse, JIT and evidence write is charged, including failures. Evidence records contain the full prompt,
/// source and model output; keep <see cref="AuditDirectory"/> private.</remarks>
[Experimental("AIDEVO005")]
public sealed class PtxProgramEvolutionOptions
{
    /// <summary>Gets or sets the operator identity. Default: <c>ptx-kernel-rewrite</c>.</summary>
    public string Id { get; set; } = "ptx-kernel-rewrite";
    /// <summary>Gets or sets a pinned model/provider identity recorded with every proposal.</summary>
    public string ModelVersionIdentity { get; set; } = string.Empty;
    /// <summary>Gets or sets the caller-owned directory for per-attempt evidence.</summary>
    public string AuditDirectory { get; set; } = string.Empty;
    /// <summary>Gets or sets whether the model may return a whole-kernel rewrite. Default: true.</summary>
    public bool AllowRewrites { get; set; } = true;
    /// <summary>Gets or sets whether the model may return line patches. Default: true.</summary>
    public bool AllowPatches { get; set; } = true;
    /// <summary>Gets or sets the most line edits in one patch, 1 to 256. Default: 32.</summary>
    public int MaxEdits { get; set; } = 32;
    /// <summary>Gets or sets repairs after the first attempt, 0 to 7. Default: 2.</summary>
    public int MaxRepairs { get; set; } = 2;
    /// <summary>Gets or sets the response bound in characters, 256 to 524,288. Default: 262,144.</summary>
    public int MaxResponseChars { get; set; } = 262_144;
    /// <summary>Gets or sets the declared per-request input-token maximum. Default: 131,072.</summary>
    public int MaxInputTokens { get; set; } = 131_072;
    /// <summary>Gets or sets the requested output-token maximum. Default: 16,384 (a full rewrite is long).</summary>
    public int MaxOutputTokens { get; set; } = 16_384;
    /// <summary>Gets or sets the sampling temperature, 0 to 2. Default: 0.3.</summary>
    public double Temperature { get; set; } = 0.3;
    /// <summary>Gets or sets the incumbent's measured profile shown to the model, or <c>null</c>.</summary>
    public PtxIncumbentProfile? IncumbentProfile { get; set; }
    /// <summary>Gets or sets the common cost-unit identity shared with evaluation.</summary>
    public string CostUnitVersionHash { get; set; } = "ptx-synthetic-work-v1";
    /// <summary>Gets or sets the one-time setup charge.</summary>
    public decimal SetupCostUnits { get; set; } = 0.1m;
    /// <summary>Gets or sets the charge per model request, including unusable answers.</summary>
    public decimal ModelCallCostUnits { get; set; } = 1m;
    /// <summary>Gets or sets the charge per reported input token.</summary>
    public decimal InputTokenCostUnits { get; set; } = 0.00001m;
    /// <summary>Gets or sets the charge per reported output token.</summary>
    public decimal OutputTokenCostUnits { get; set; } = 0.00002m;
    /// <summary>Gets or sets the charge per JIT compilation in the worker, including failures.</summary>
    public decimal CompilationCostUnits { get; set; } = 0.25m;
    /// <summary>Gets or sets the charge per parse or patch application.</summary>
    public decimal ParseCostUnits { get; set; } = 0.01m;
    /// <summary>Gets or sets the charge per evidence record.</summary>
    public decimal AuditCostUnits { get; set; } = 0.01m;

    internal PtxProgramEvolutionOptions Snapshot()
    {
        var copy = (PtxProgramEvolutionOptions)MemberwiseClone();
        foreach (string value in new[] { Id, ModelVersionIdentity, CostUnitVersionHash })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
                throw new ArgumentException("Operator, model and cost-unit identities must be bounded printable strings.");
            new UTF8Encoding(false, true).GetByteCount(value);
        }
        if (Id.Length > 64) throw new ArgumentException("The operator identity must not exceed 64 characters.");
        if (string.IsNullOrWhiteSpace(AuditDirectory)) throw new ArgumentException("An audit directory is required.");
        copy.AuditDirectory = Path.GetFullPath(AuditDirectory);
        if (!AllowRewrites && !AllowPatches) throw new ArgumentException("Allow rewrites, patches, or both.");
        if (MaxEdits is < 1 or > 256 || MaxRepairs is < 0 or > 7 || MaxResponseChars is < 256 or > 524_288 ||
            MaxInputTokens is < 1 or > 2_097_152 || MaxOutputTokens is < 1 or > 131_072 || !double.IsFinite(Temperature) || Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(PtxProgramEvolutionOptions), "A proposal bound is outside its supported range.");
        foreach (decimal price in new[] { SetupCostUnits, ModelCallCostUnits, CompilationCostUnits, ParseCostUnits, AuditCostUnits })
            if (price <= 0 || price > 1_000_000_000m) throw new ArgumentOutOfRangeException(nameof(PtxProgramEvolutionOptions), "Fixed work prices must be positive and bounded.");
        foreach (decimal price in new[] { InputTokenCostUnits, OutputTokenCostUnits })
            if (price < 0 || price > 1_000_000m) throw new ArgumentOutOfRangeException(nameof(PtxProgramEvolutionOptions), "Token prices must be nonnegative and bounded.");
        return copy;
    }

    internal string ConfigurationHash => EvolutionHash.Combine(new[]
    {
        "ptx-proposal-options-v1", Id, ModelVersionIdentity, CostUnitVersionHash, AllowRewrites ? "rewrite" : "-", AllowPatches ? "patch" : "-",
        MaxEdits.ToString(CultureInfo.InvariantCulture), MaxRepairs.ToString(CultureInfo.InvariantCulture),
        MaxResponseChars.ToString(CultureInfo.InvariantCulture), MaxInputTokens.ToString(CultureInfo.InvariantCulture),
        MaxOutputTokens.ToString(CultureInfo.InvariantCulture), Temperature.ToString("R", CultureInfo.InvariantCulture),
        IncumbentProfile?.Identity ?? "-", SetupCostUnits.ToString(CultureInfo.InvariantCulture),
        ModelCallCostUnits.ToString(CultureInfo.InvariantCulture), InputTokenCostUnits.ToString(CultureInfo.InvariantCulture),
        OutputTokenCostUnits.ToString(CultureInfo.InvariantCulture), CompilationCostUnits.ToString(CultureInfo.InvariantCulture),
        ParseCostUnits.ToString(CultureInfo.InvariantCulture), AuditCostUnits.ToString(CultureInfo.InvariantCulture)
    });
}