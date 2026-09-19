namespace AiDotNet.Evolution;

/// <summary>Connects a live fingerprinted ask/tell session to durable external delivery without rebinding old results to a new engine.</summary>
/// <typeparam name="TGenome">The engine's immutable genome type.</typeparam>
/// <remarks>The caller owns both session and coordinator. After coordinator recovery, create a new bridge to the
/// same still-live session. A new session instance is refused even when run/compatibility/evaluation IDs match.
/// This preserves delivery recovery, not lost proposal/operator state or exact partial-batch search continuation.</remarks>
public sealed class EvolutionDurableSessionBridge<TGenome>
{
    private readonly object _sync = new();
    private readonly EvolutionSession<TGenome> _session;
    private readonly DurableEvolutionWorkCoordinator _coordinator;
    private readonly IEvolutionGenomeCodec<TGenome> _codec;
    private readonly Func<string, EvolutionTaskResult> _decodeResult;
    private readonly EvolutionWorkRequirements _requirements;
    private readonly EvolutionResources _estimated;
    private readonly EvolutionResources _maximum;

    /// <summary>Attaches an engine-owned codec and caller-versioned result decoder to a compatible delivery coordinator.</summary>
    /// <remarks>Decoder failures do not consume stored results or refund receipts. Acknowledgement is persisted after
    /// the fenced tell; replay after an acknowledgement failure cannot tell the same source attempt twice.</remarks>
    public EvolutionDurableSessionBridge(EvolutionSession<TGenome> session, DurableEvolutionWorkCoordinator coordinator,
        Func<string, EvolutionTaskResult> decodeResult, EvolutionWorkRequirements requirements,
        EvolutionResources estimated, EvolutionResources maximum)
    {
        Guard.NotNull(session); Guard.NotNull(coordinator); Guard.NotNull(decodeResult);
        Guard.NotNull(requirements); Guard.NotNull(estimated); Guard.NotNull(maximum);
        if (!session.RequiresWorkIdentity) throw new ArgumentException("Durable delivery requires a fingerprinted, fenced session.", nameof(session));
        if (session.RunId != coordinator.RunId || session.CompatibilityHash != coordinator.CompatibilityHash)
            throw new ArgumentException("Session run and compatibility must match the coordinator.", nameof(coordinator));
        _codec = session.ExternalWorkCodec ?? throw new ArgumentException("The engine must have an explicit genome codec.", nameof(session));
        _session = session; _coordinator = coordinator; _decodeResult = decodeResult;
        _requirements = requirements; _estimated = estimated; _maximum = maximum;
        coordinator.ValidateSessionCosts(estimated, maximum);
        coordinator.BindSession(session.InstanceId);
    }

    /// <summary>Persists an original ask item, including its full source ticket and exact random context.</summary>
    /// <returns>True when durably enqueued, including an identical retry; false for work no longer outstanding in this session.</returns>
    /// <remarks>Keep the ask item until this call succeeds. If publication fails, reopen the coordinator and retry with
    /// the same item/session; do not ask for or manufacture a replacement ticket. Codec and storage errors propagate.</remarks>
    public bool Enqueue(EvolutionAskItem<TGenome> work)
    {
        Guard.NotNull(work);
        lock (_sync)
        {
            if (work.WorkIdentity is null || !_session.IsOutstanding(work.WorkIdentity, work.Candidate, work.Context)) return false;
            var payload = new EvolutionDurableEvaluationPayload(_codec.Serialize(work.Candidate.CanonicalGenome.Genome),
                work.Candidate.CanonicalGenome.Id, work.Context);
            _coordinator.EnqueueSessionWork(work.WorkIdentity, payload.CanonicalGenomeId, payload.ToJson(), _requirements, _estimated, _maximum);
            return true;
        }
    }

    /// <summary>Delivers stored logical results using original source tickets, then persists each acknowledgement.</summary>
    /// <returns>The number of tells newly accepted during this call, not the total number historically accepted by the engine.</returns>
    public int DeliverAvailableResults()
    {
        lock (_sync)
        {
            int accepted = 0;
            foreach (EvolutionWorkIdentity source in _coordinator.GetSourceWork())
            {
                EvolutionCommittedWork? result = _coordinator.GetResult(source.EvaluationId, source.Attempt);
                if (result is null) continue;
                EvolutionTaskResult decoded = _decodeResult(result.Payload) ?? throw new InvalidDataException("The external result decoder returned null.");
                bool told = _session.TellAttempt(source, decoded);
                _coordinator.AcknowledgeSourceResult(source, told);
                if (told) accepted++;
            }
            return accepted;
        }
    }

    /// <summary>Requests cancellation of durable work whose original session attempt expired or stopped.</summary>
    /// <remarks>Call periodically and before dispatch; this cannot physically terminate a worker or refund its reservation.</remarks>
    public int ReconcileExpiredWork()
    {
        lock (_sync)
        {
            int canceled = 0;
            foreach (EvolutionWorkIdentity source in _coordinator.GetSourceWork())
                if (!_session.IsOutstanding(source) && _coordinator.Cancel(source.EvaluationId, source.Attempt)) canceled++;
            return canceled;
        }
    }
}
