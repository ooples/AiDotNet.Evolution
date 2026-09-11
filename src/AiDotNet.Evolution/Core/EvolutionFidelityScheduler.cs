using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Sequential resource-aware successive halving, versioned per-replicate state reuse and fresh final confirmation.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
/// <remarks>Search and confirmation callbacks are separate and explicitly versioned. They must return actual costs,
/// execute requested work without hidden cross-fidelity cache reuse, enforce external resource/isolation limits and
/// validate any external state referenced by a continuation token. No token is passed to confirmation.
/// The bracket is not a persistent worker scheduler and does not automatically admit any result to an archive.
/// Serialize runs sharing stateful callbacks unless the backend explicitly supports concurrent execution.</remarks>
public sealed class EvolutionFidelityScheduler<TGenome>
{
    private readonly EvolutionFidelityPlan _plan;
    private readonly EvolutionResourceLedger _ledger;
    private readonly string _searchVersion, _confirmationVersion, _stateVersion;
    private readonly Func<TGenome, EvolutionFidelityEvaluationContext, CancellationToken, ValueTask<EvolutionFidelityEvaluationResult>> _search, _confirm;

    /// <summary>Creates a bounded bracket with independent search and confirmation evaluator boundaries.</summary>
    public EvolutionFidelityScheduler(EvolutionFidelityPlan plan, EvolutionResourceLedger ledger, string searchEvaluatorVersionHash,
        string confirmationEvaluatorVersionHash, string stateVersionHash,
        Func<TGenome, EvolutionFidelityEvaluationContext, CancellationToken, ValueTask<EvolutionFidelityEvaluationResult>> evaluateSearch,
        Func<TGenome, EvolutionFidelityEvaluationContext, CancellationToken, ValueTask<EvolutionFidelityEvaluationResult>> evaluateConfirmation)
    {
        Guard.NotNull(plan); Guard.NotNull(ledger); Guard.NotNullOrWhiteSpace(searchEvaluatorVersionHash);
        Guard.NotNullOrWhiteSpace(confirmationEvaluatorVersionHash); Guard.NotNullOrWhiteSpace(stateVersionHash);
        Guard.NotNull(evaluateSearch); Guard.NotNull(evaluateConfirmation);
        if (searchEvaluatorVersionHash.Length > 256 || confirmationEvaluatorVersionHash.Length > 256 ||
            searchEvaluatorVersionHash.Any(char.IsControl) || confirmationEvaluatorVersionHash.Any(char.IsControl))
            throw new ArgumentException("Bounded printable evaluator versions required.", nameof(searchEvaluatorVersionHash));
        if (!ledger.Limits.Amounts.ContainsKey("cost_units")) throw new ArgumentException("Declare cost_units in the shared ledger.", nameof(ledger));
        if (stateVersionHash.Length > 128 || stateVersionHash.Any(char.IsControl)) throw new ArgumentException("Invalid state version.", nameof(stateVersionHash));
        _plan = plan; _ledger = ledger; _searchVersion = searchEvaluatorVersionHash; _confirmationVersion = confirmationEvaluatorVersionHash;
        _stateVersion = stateVersionHash; _search = evaluateSearch; _confirm = evaluateConfirmation;
        VersionHash = EvolutionHash.Combine(new[] { "fidelity-scheduler-v1", plan.VersionHash, _searchVersion, _confirmationVersion, _stateVersion });
    }
    /// <summary>Gets the immutable bracket and callback semantics.</summary>
    public string VersionHash { get; }

    /// <summary>Processes 2..64 candidates and then separately confirms all complete final-level survivors from scratch.</summary>
    public async ValueTask<EvolutionFidelityReport<TGenome>> RunAsync(string runId, IReadOnlyList<EvolutionCanonicalGenome<TGenome>> candidates,
        ulong seed, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(runId); Guard.NotNull(candidates);
        if (runId.Length > 128 || runId.Any(char.IsControl)) throw new ArgumentException("Bounded printable run identity required.", nameof(runId));
        cancellationToken.ThrowIfCancellationRequested();
        var initial = EvolutionCollection.CopyBounded(candidates, 64, nameof(candidates));
        if (initial.Length < 2 || initial.Any(candidate => candidate is null) || initial.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != initial.Length)
            throw new ArgumentException("Require 2..64 uniquely identified owned candidates.", nameof(candidates));
        string identity = EvolutionHash.Combine(new[] { VersionHash, runId, seed.ToString(CultureInfo.InvariantCulture) }.Concat(initial.Select(candidate => candidate.Id)));
        var indices = initial.Select((candidate, index) => new { candidate.Id, Index = index }).ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        var current = initial.ToList();
        var states = new Dictionary<string, EvolutionFidelityResumeState?[]>(StringComparer.Ordinal);
        var batches = new List<EvolutionFidelityBatch<TGenome>>(); var promotions = new List<EvolutionFidelityPromotion>();
        EvolutionFidelityReport<TGenome> Report(EvolutionFidelityStopReason reason) => new(identity, VersionHash, _searchVersion, _confirmationVersion,
            initial.Select(candidate => candidate.Id), _plan, reason, batches, promotions, _ledger.Snapshot());

        async ValueTask<EvolutionFidelityBatch<TGenome>> Evaluate(EvolutionCanonicalGenome<TGenome> candidate, int rung, bool confirmation)
        {
            EvolutionFidelityLevel level = _plan.Levels[rung];
            var pending = new EvolutionFidelityResumeState?[_plan.Replicates];
            var prior = new List<string?>(); int offered = 0;
            var purpose = confirmation ? EvolutionReplicationPurpose.Confirmation : EvolutionReplicationPurpose.Search;
            string evaluator = confirmation ? _confirmationVersion : _searchVersion;
            var runner = new EvolutionReplicateRunner<TGenome>(EvolutionHash.Combine(new[] { VersionHash, evaluator, level.VersionHash }),
                _plan.Replication(level, confirmation), _ledger, async (genome, sample, token) =>
                {
                    EvolutionFidelityResumeState? resume = null;
                    if (!confirmation && states.TryGetValue(candidate.Id, out var previous))
                    {
                        var value = previous[sample.Index];
                        if (value is not null && value.GenomeId == candidate.Id && value.EvaluatorVersionHash == _searchVersion &&
                            value.StateVersionHash == _stateVersion && value.ReplicateIndex == sample.Index && value.SourceLevel.ResourceLevel < level.ResourceLevel)
                            resume = value;
                    }
                    prior.Add(resume?.SourceSampleIdentity);
                    var context = new EvolutionFidelityEvaluationContext(level, sample, resume);
                    var outcome = await (confirmation ? _confirm : _search)(genome, context, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("No fidelity measurement receipt returned.");
                    byte[]? payload = confirmation ? null : outcome.CopyContinuationToken();
                    if (payload is not null)
                    {
                        offered++;
                        if (outcome.StateVersionHash == _stateVersion)
                            pending[sample.Index] = new(candidate.Id, _searchVersion, _stateVersion, level, sample, payload);
                    }
                    return outcome.Measurement;
                });
            var measurements = await runner.RunAsync(candidate, new EvolutionEvaluationContext(indices[candidate.Id], seed, (ulong)(rung + 1), 1),
                identity, purpose, cancellationToken).ConfigureAwait(false);
            int accepted = !confirmation && measurements.IsComplete ? pending.Count(value => value is not null) : 0;
            if (!confirmation)
            {
                if (measurements.IsComplete) states[candidate.Id] = pending;
                else states.Remove(candidate.Id);
            }
            var batch = new EvolutionFidelityBatch<TGenome>(candidate, level, purpose, measurements, prior, accepted, offered - accepted);
            batches.Add(batch); return batch;
        }

        for (int rung = 0; rung < _plan.Levels.Count; rung++)
        {
            var successful = new List<EvolutionFidelityBatch<TGenome>>();
            foreach (var candidate in current)
            {
                if (cancellationToken.IsCancellationRequested) return Report(EvolutionFidelityStopReason.Canceled);
                var batch = await Evaluate(candidate, rung, false).ConfigureAwait(false);
                var terminal = Terminal(batch.Measurements.StopReason);
                if (terminal.HasValue) return Report(terminal.Value);
                if (batch.Measurements.IsComplete) successful.Add(batch);
            }
            if (successful.Count == 0) return Report(EvolutionFidelityStopReason.NoEligibleCandidates);
            var ranked = Rank(successful);
            if (rung == _plan.Levels.Count - 1) { current = ranked.Select(batch => batch.Candidate).ToList(); break; }
            int keep = Math.Min(ranked.Count, Math.Max(_plan.MinimumSurvivors, (current.Count + _plan.ReductionFactor - 1) / _plan.ReductionFactor));
            int exploratory = _plan.ExplorationFraction > 0 && keep > 1
                ? Math.Min(Math.Min(keep - 1, ranked.Count - keep), Math.Max(1, (int)Math.Ceiling(keep * _plan.ExplorationFraction))) : 0;
            var selected = ranked.Take(keep - exploratory).Select(batch => (Batch: batch, Exploration: false)).ToList();
            var tail = ranked.Skip(keep).ToList();
            ulong stream = ulong.Parse(EvolutionHash.Combine(new[] { identity, "promotion", rung.ToString(CultureInfo.InvariantCulture) }).Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var random = StableRandom.CreateStream(seed, stream);
            for (int pick = 0; pick < exploratory; pick++)
            {
                int index = random.NextInt(tail.Count); selected.Add((tail[index], true)); tail.RemoveAt(index);
            }
            foreach (var selection in selected)
                promotions.Add(new(selection.Batch.Candidate.Id, selection.Batch.Measurements.BatchIdentity, _plan.Levels[rung].Id,
                    _plan.Levels[rung + 1].Id, ranked.IndexOf(selection.Batch) + 1, selection.Exploration));
            current = selected.Select(selection => selection.Batch.Candidate).ToList();
            foreach (string id in states.Keys.Where(id => !current.Any(candidate => candidate.Id == id)).ToArray()) states.Remove(id);
        }
        states.Clear(); // Search continuation state must never flow into independent final confirmation.
        bool confirmed = false;
        foreach (var candidate in current)
        {
            if (cancellationToken.IsCancellationRequested) return Report(EvolutionFidelityStopReason.Canceled);
            var batch = await Evaluate(candidate, _plan.Levels.Count - 1, true).ConfigureAwait(false);
            var terminal = Terminal(batch.Measurements.StopReason);
            if (terminal.HasValue) return Report(terminal.Value);
            confirmed |= batch.Measurements.IsComplete;
        }
        return Report(confirmed ? EvolutionFidelityStopReason.Completed : EvolutionFidelityStopReason.NoConfirmedCandidate);
    }

    private List<EvolutionFidelityBatch<TGenome>> Rank(IEnumerable<EvolutionFidelityBatch<TGenome>> candidates) =>
        (_plan.Direction == EvolutionOptimizationDirection.Maximize ? candidates.OrderByDescending(batch => batch.Measurements.MeanQuality)
            : candidates.OrderBy(batch => batch.Measurements.MeanQuality)).ThenBy(batch => batch.Candidate.Id, StringComparer.Ordinal).ToList();
    private static EvolutionFidelityStopReason? Terminal(EvolutionReplicationStopReason reason) => reason switch
    {
        EvolutionReplicationStopReason.BudgetExhausted => EvolutionFidelityStopReason.BudgetExhausted,
        EvolutionReplicationStopReason.Canceled => EvolutionFidelityStopReason.Canceled,
        EvolutionReplicationStopReason.MaximumCostExceeded => EvolutionFidelityStopReason.MaximumCostExceeded,
        _ => null
    };
}
