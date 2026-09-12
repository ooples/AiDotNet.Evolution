namespace AiDotNet.Evolution;

public sealed partial class DurableEvolutionWorkCoordinator
{
    /// <summary>Gets the original live engine-session identity, if this coordinator has been attached to one.</summary>
    public string? SourceSessionId { get { lock (_sync) { _journal.EnsureUsable(); return _state.SourceSessionId; } } }

    internal void ValidateSessionCosts(EvolutionResources estimated, EvolutionResources maximum) => ValidateCost(estimated, maximum);

    internal void BindSession(string instanceId)
    {
        _ = new EvolutionWorkIdentity(RunId, 0, 1, instanceId);
        lock (_sync)
        {
            _journal.EnsureUsable();
            if (_state.SourceSessionId == instanceId) return;
            if (_state.SourceSessionId is not null || _state.Work.Count != 0)
                throw new InvalidOperationException("Stored work cannot attach to a different engine session. Lost search state requires an explicit fork with retained campaign costs and provenance.");
            var next = Next(); next.State.SourceSessionId = instanceId; Publish(next.State, next.Ledger, Now());
        }
    }

    internal bool EnqueueSessionWork(EvolutionWorkIdentity source, string canonicalGenomeId, string payload,
        EvolutionWorkRequirements requirements, EvolutionResources estimated, EvolutionResources maximum) =>
        EnqueueCore(source.EvaluationId, source.Attempt, canonicalGenomeId, payload, requirements, estimated, maximum, source);

    internal IReadOnlyList<EvolutionWorkIdentity> GetSourceWork()
    {
        lock (_sync)
        {
            _journal.EnsureUsable();
            return _state.Work.Values.Where(job => job.SourceLeaseId is not null && !job.SourceTellAccepted.HasValue)
                .Select(job => new EvolutionWorkIdentity(RunId, job.EvaluationId, job.Attempt, job.SourceLeaseId!)).ToArray();
        }
    }

    internal void AcknowledgeSourceResult(EvolutionWorkIdentity source, bool tellAccepted)
    {
        lock (_sync)
        {
            var next = Next();
            if (source.RunId != RunId || !next.State.Work.TryGetValue(Key(source.EvaluationId, source.Attempt), out WorkItemState? job)
                || source.LeaseId != job.SourceLeaseId || job.Status != WorkItemStatus.Completed)
                throw new InvalidOperationException("No completed work matches this source-session ticket.");
            if (job.SourceTellAccepted.HasValue) return;
            // False can mean a stale attempt OR a successful tell whose acknowledgement was lost.
            // It is not a claim that the engine never received the result.
            job.SourceTellAccepted = tellAccepted; Publish(next.State, next.Ledger, Now());
        }
    }
}
