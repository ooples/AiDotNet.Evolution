using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Bounds an opt-in proposal/evaluation pipeline with deterministic feedback-wave boundaries.</summary>
/// <remarks>Within a wave, generation and evaluation overlap. Parent/archive snapshots and learning stay fixed until
/// every admitted proposal has settled; commits and learning updates then follow the configured execution order.
/// This explicit barrier bounds staleness and keeps default stateful operators safe. It is not continuous asynchronous
/// learning. Queue capacities bound submitted callbacks waiting for workers; a separate WaveSize-bounded planning
/// buffer holds identities/snapshots and reserves resources before submission. Worker counts/queues/rates are recorded in pipeline semantics;
/// these options do not alter compatibility hashes when pipeline dispatch is disabled.</remarks>
public sealed class EvolutionPipelineOptions
{
    /// <summary>Gets or sets the maximum proposals sharing one feedback boundary, in 1..4096.</summary>
    public int WaveSize { get; set; } = 16;
    /// <summary>Gets or sets proposal workers, in 1..256; ordinary operators still use one.</summary>
    public int MaxProposalConcurrency { get; set; } = 1;
    /// <summary>Gets or sets pending proposal queue capacity, in 1..256.</summary>
    public int ProposalQueueCapacity { get; set; } = 16;
    /// <summary>Gets or sets pending evaluator queue capacity, in 1..256.</summary>
    public int EvaluationQueueCapacity { get; set; } = 16;
    /// <summary>Gets or sets the minimum spacing between proposal callback starts; zero disables throttling.</summary>
    public TimeSpan MinimumProposalStartInterval { get; set; }
    /// <summary>Gets or sets the minimum spacing between evaluator callback starts; zero disables throttling.</summary>
    public TimeSpan MinimumEvaluationStartInterval { get; set; }
    /// <summary>Gets or sets the maximum retained schedule records, in 0..65536; dropped records invalidate full replay evidence.</summary>
    public int MaximumScheduleRecords { get; set; } = 4096;

    internal EvolutionPipelineOptions SnapshotAndValidate()
    {
        if (WaveSize is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(WaveSize));
        if (MaxProposalConcurrency is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(MaxProposalConcurrency));
        if (ProposalQueueCapacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(ProposalQueueCapacity));
        if (EvaluationQueueCapacity is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(EvaluationQueueCapacity));
        if (MaximumScheduleRecords is < 0 or > 65536) throw new ArgumentOutOfRangeException(nameof(MaximumScheduleRecords));
        ValidateInterval(MinimumProposalStartInterval, nameof(MinimumProposalStartInterval));
        ValidateInterval(MinimumEvaluationStartInterval, nameof(MinimumEvaluationStartInterval));
        return (EvolutionPipelineOptions)MemberwiseClone();
    }

    internal string ToCanonicalString() => string.Join("|", "pipeline-waves-v2-snapshot-content", WaveSize.ToString(CultureInfo.InvariantCulture),
        MaxProposalConcurrency.ToString(CultureInfo.InvariantCulture), ProposalQueueCapacity.ToString(CultureInfo.InvariantCulture),
        EvaluationQueueCapacity.ToString(CultureInfo.InvariantCulture), MinimumProposalStartInterval.Ticks.ToString(CultureInfo.InvariantCulture),
        MinimumEvaluationStartInterval.Ticks.ToString(CultureInfo.InvariantCulture));

    private static void ValidateInterval(TimeSpan value, string argument)
    {
        if (value < TimeSpan.Zero || value > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(argument);
    }
}
