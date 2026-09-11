using System.Globalization;

namespace AiDotNet.Evolution;

public sealed partial class EvolutionFidelityScheduler<TGenome>
{
    private string ResourceLimitsHash() => EvolutionHash.Combine(_ledger.Limits.Amounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => EvolutionHash.Combine(new[] { pair.Key, pair.Value.ToString(CultureInfo.InvariantCulture) })));

    private EvolutionFidelityCheckpoint CaptureCheckpoint(string identity, IReadOnlyList<EvolutionFidelityBatch<TGenome>> batches,
        Dictionary<string, EvolutionFidelityResumeState?[]> states)
    {
        var resources = _ledger.Snapshot();
        if (resources.Admitted != resources.Settled || resources.Reserved.Values.Any(value => value != 0))
            throw new InvalidOperationException("Fidelity checkpoints require a coordinated settled ledger boundary.");
        int Rung(EvolutionFidelityLevel level) => _plan.Levels.Select((value, index) => (value, index)).Single(item => item.value.VersionHash == level.VersionHash).index;
        return new(new EvolutionFidelityCheckpointData
        {
            RunIdentity = identity,
            SchedulerVersionHash = VersionHash,
            ResourceConfigurationHash = _ledger.ConfigurationHash,
            ResourceLimitsHash = ResourceLimitsHash(),
            ResourceState = _ledger.CaptureState(),
            Batches = batches.Select(batch => new EvolutionFidelityBatchData
            {
                GenomeId = batch.Candidate.Id,
                Rung = Rung(batch.Level),
                Confirmation = batch.Purpose == EvolutionReplicationPurpose.Confirmation,
                BatchIdentity = batch.Measurements.BatchIdentity,
                StopReason = batch.Measurements.StopReason,
                PriorSamples = batch.ResumedFromSampleIdentities.ToArray(),
                AcceptedTokens = batch.AcceptedContinuationTokens,
                RejectedTokens = batch.RejectedContinuationTokens,
                Samples = batch.Measurements.Samples.Select(sample => new EvolutionFidelitySampleData
                {
                    Status = sample.Status,
                    Quality = sample.Quality,
                    Charged = sample.ChargedCostUnits,
                    Unknown = sample.UnknownCost,
                    Reported = sample.ReportedCostUnits,
                    OriginJson = sample.MeasurementOrigin?.ToJson()
                }).ToArray()
            }).ToArray(),
            States = states.ToDictionary(pair => pair.Key, pair => pair.Value.Select(state => state is null ? null : new EvolutionFidelityTokenData
            {
                Rung = Rung(state.SourceLevel),
                SourceSampleIdentity = state.SourceSampleIdentity,
                TokenHash = state.TokenHash,
                PayloadBase64 = Convert.ToBase64String(state.CopyToken())
            }).ToArray(), StringComparer.Ordinal)
        });
    }

    private (List<EvolutionFidelityBatch<TGenome>> Batches, Dictionary<string, EvolutionFidelityResumeState?[]> States)
        ReadCheckpoint(EvolutionFidelityCheckpoint checkpoint, string identity, EvolutionCanonicalGenome<TGenome>[] initial, ulong seed)
    {
        var data = checkpoint.ReadData();
        var snapshot = _ledger.Snapshot();
        if (data.RunIdentity != identity || data.SchedulerVersionHash != VersionHash || data.ResourceConfigurationHash != _ledger.ConfigurationHash ||
            data.ResourceLimitsHash != ResourceLimitsHash() || data.ResourceState != _ledger.CaptureState() || snapshot.Admitted != snapshot.Settled ||
            snapshot.Reserved.Values.Any(value => value != 0) || data.Batches is null || data.Batches.Length is < 1 or > 576 || data.States is null || data.States.Count > initial.Length)
            throw new ArgumentException("Checkpoint run, configuration, caps or exact settled ledger state differs.", nameof(checkpoint));
        var candidates = initial.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var indices = initial.Select((candidate, index) => (candidate.Id, index)).ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        var restored = new List<EvolutionFidelityBatch<TGenome>>();
        var latest = new Dictionary<string, EvolutionFidelityBatch<TGenome>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.Batches)
        {
            if (item is null || !candidates.TryGetValue(item.GenomeId, out var candidate) || item.Rung < 0 || item.Rung >= _plan.Levels.Count ||
                !item.Confirmation.HasValue || !item.StopReason.HasValue || item.Samples is null || item.Samples.Length < 1 || item.Samples.Length > _plan.Replicates ||
                item.PriorSamples is null || item.PriorSamples.Length != item.Samples.Length || item.AcceptedTokens < 0 || item.RejectedTokens < 0 ||
                item.AcceptedTokens > item.Samples.Length || item.RejectedTokens > item.Samples.Length ||
                item.AcceptedTokens + item.RejectedTokens > item.Samples.Length ||
                item.StopReason is not (EvolutionReplicationStopReason.Completed or EvolutionReplicationStopReason.InvalidMeasurement or EvolutionReplicationStopReason.UnknownCost))
                throw new ArgumentException("Invalid settled batch checkpoint.", nameof(checkpoint));
            bool confirmation = item.Confirmation.Value, complete = item.StopReason == EvolutionReplicationStopReason.Completed;
            if ((complete && item.Samples.Length != _plan.Replicates) || (!complete && item.AcceptedTokens != 0) ||
                (confirmation && (item.Rung != _plan.Levels.Count - 1 || item.AcceptedTokens != 0 || item.RejectedTokens != 0 || item.PriorSamples.Any(value => value is not null))))
                throw new ArgumentException("Invalid completion or confirmation checkpoint.", nameof(checkpoint));
            var level = _plan.Levels[item.Rung];
            var purpose = confirmation ? EvolutionReplicationPurpose.Confirmation : EvolutionReplicationPurpose.Search;
            var plan = _plan.Replication(level, confirmation);
            var runner = new EvolutionReplicateRunner<TGenome>(EvolutionHash.Combine(new[] { VersionHash,
                confirmation ? _confirmationVersion : _searchVersion, level.VersionHash }), plan, _ledger,
                (_, _, _) => throw new InvalidOperationException("Checkpoint validation must not dispatch an evaluator."));
            var context = new EvolutionEvaluationContext(indices[candidate.Id], seed, (ulong)(item.Rung + 1), 1);
            string batchId = EvolutionReplicateRunner<TGenome>.IdentifyBatch(runner.VersionHash, candidate.Id, context, identity, purpose);
            if (item.BatchIdentity != batchId || !seen.Add(batchId)) throw new ArgumentException("Duplicate or mismatched batch identity.", nameof(checkpoint));
            var samples = new List<EvolutionReplicateMeasurement>(); var moments = new EvolutionReplicationMoments();
            string? originScope = null;
            latest.TryGetValue(candidate.Id, out var previous);
            if (item.PriorSamples.Count(value => value is not null) > (previous?.AcceptedContinuationTokens ?? 0))
                throw new ArgumentException("Continuation references exceed available prior tokens.", nameof(checkpoint));
            for (int i = 0; i < item.Samples.Length; i++)
            {
                var sample = item.Samples[i];
                if (sample is null || !sample.Status.HasValue || !Enum.IsDefined(typeof(EvolutionEvaluationStatus), sample.Status.Value) ||
                    !sample.Charged.HasValue || sample.Charged < 0 || sample.Charged > level.MaximumCostPerReplicate || !sample.Unknown.HasValue ||
                    (sample.Quality.HasValue && !EvolutionDescriptorDefinition.IsFinite(sample.Quality.Value)) ||
                    (sample.Reported.HasValue && (!EvolutionDescriptorDefinition.IsFinite(sample.Reported.Value) || sample.Reported < 0)))
                    throw new ArgumentException("Invalid checkpoint sample.", nameof(checkpoint));
                var sampleContext = new EvolutionReplicateContext(batchId, i, purpose, context);
                var receipt = _ledger.FindReceipt("replicate/" + sampleContext.SampleIdentity);
                var expectedOutcome = sample.Unknown.Value ? EvolutionResourceOutcome.Unknown :
                    (!complete && i == item.Samples.Length - 1 ? EvolutionResourceOutcome.Failed : EvolutionResourceOutcome.Completed);
                if (receipt is null || receipt.Stage != (confirmation ? EvolutionResourceStage.Confirmation : EvolutionResourceStage.Evaluation) ||
                    receipt.Maximum.Amounts.Count != 1 || receipt.Estimated.Amounts.Count != 1 || receipt.Charged.Amounts.Count != 1 ||
                    receipt.Estimated["cost_units"] != level.MaximumCostPerReplicate ||
                    receipt.Attempt != 1 || receipt.Maximum["cost_units"] != level.MaximumCostPerReplicate || receipt.Charged["cost_units"] != sample.Charged ||
                    receipt.Outcome != expectedOutcome || (sample.Unknown.Value && sample.Charged != level.MaximumCostPerReplicate) ||
                    (!sample.Unknown.Value && (!sample.Reported.HasValue || sample.Reported > (double)EvolutionResources.MaximumAmount ||
                        (sample.Reported > 0 && (decimal)sample.Reported.Value == 0) || (decimal)sample.Reported.Value != sample.Charged)))
                    throw new ArgumentException("Checkpoint sample does not match its settled ledger receipt.", nameof(checkpoint));
                if (item.PriorSamples[i] is { } source && (previous is null || !previous.Measurements.IsComplete ||
                    previous.Purpose != EvolutionReplicationPurpose.Search || previous.Level.ResourceLevel >= level.ResourceLevel ||
                    source != previous.Measurements.Samples[i].Context.SampleIdentity))
                    throw new ArgumentException("Continuation source sample differs.", nameof(checkpoint));
                var origin = sample.OriginJson is null ? null : EvolutionMeasurementOrigin.FromJson(sample.OriginJson);
                bool accepted = complete || i < item.Samples.Length - 1;
                if (accepted && (sample.Status != EvolutionEvaluationStatus.Completed || !sample.Quality.HasValue || sample.Quality < _plan.MinimumQuality ||
                    sample.Quality > _plan.MaximumQuality || sample.Unknown.Value || (origin is not null && (origin.Kind != EvolutionMeasurementOriginKind.Measured ||
                        origin.SampleCount != 1 || origin.SampleIds[0] != sampleContext.SampleIdentity || (originScope is not null && originScope != origin.ScopeKey)))))
                    throw new ArgumentException("Invalid sample cannot contribute to restored statistics.", nameof(checkpoint));
                if (origin is not null) originScope = origin.ScopeKey;
                if (accepted) moments.Add(sample.Quality!.Value);
                if (!accepted && (sample.Unknown.Value != (item.StopReason == EvolutionReplicationStopReason.UnknownCost)))
                    throw new ArgumentException("Failure reason differs from its receipt.", nameof(checkpoint));
                samples.Add(new(sampleContext, sample.Status.Value, sample.Quality, sample.Charged.Value, sample.Unknown.Value, sample.Reported, origin));
            }
            var measurements = new EvolutionReplicationReport(batchId, plan, item.StopReason.Value, samples, moments.Mean, moments.DeviationScale, moments.ScaledSquares);
            var batch = new EvolutionFidelityBatch<TGenome>(candidate, level, purpose, measurements, item.PriorSamples, item.AcceptedTokens, item.RejectedTokens);
            restored.Add(batch); latest[candidate.Id] = batch;
        }
        if (restored.Any(batch => batch.Purpose == EvolutionReplicationPurpose.Confirmation) && data.States.Count != 0)
            throw new ArgumentException("Search tokens cannot survive into confirmation.", nameof(checkpoint));
        var states = new Dictionary<string, EvolutionFidelityResumeState?[]>(StringComparer.Ordinal);
        foreach (var entry in data.States)
        {
            if (!latest.TryGetValue(entry.Key, out var batch) || batch.Purpose != EvolutionReplicationPurpose.Search || !batch.Measurements.IsComplete ||
                entry.Value is null || entry.Value.Length != _plan.Replicates || entry.Value.Count(value => value is not null) != batch.AcceptedContinuationTokens)
                throw new ArgumentException("Continuation state has no matching complete source batch.", nameof(checkpoint));
            var slots = new EvolutionFidelityResumeState?[_plan.Replicates];
            for (int i = 0; i < slots.Length; i++)
            {
                var token = entry.Value[i]; if (token is null) continue;
                if (token.Rung < 0 || token.Rung >= _plan.Levels.Count || _plan.Levels[token.Rung].VersionHash != batch.Level.VersionHash ||
                    token.SourceSampleIdentity != batch.Measurements.Samples[i].Context.SampleIdentity || token.PayloadBase64 is null || token.PayloadBase64.Length > 5464)
                    throw new ArgumentException("Continuation token metadata differs.", nameof(checkpoint));
                byte[] payload = Convert.FromBase64String(token.PayloadBase64);
                if (payload.Length is < 1 or > 4096) throw new ArgumentException("Continuation token exceeds its bound.", nameof(checkpoint));
                var state = new EvolutionFidelityResumeState(entry.Key, _searchVersion, _stateVersion, batch.Level, batch.Measurements.Samples[i].Context, payload);
                if (state.TokenHash != token.TokenHash) throw new ArgumentException("Continuation token hash differs.", nameof(checkpoint));
                slots[i] = state;
            }
            states.Add(entry.Key, slots);
        }
        return (restored, states);
    }
}
