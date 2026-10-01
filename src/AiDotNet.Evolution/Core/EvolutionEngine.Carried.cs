using static AiDotNet.Evolution.EvolutionEngineDocuments;

namespace AiDotNet.Evolution;

// Budget-truncated batches (#178 follow-up). The evaluation budget can stop a batch short: a run with three evaluations
// left plans three candidates where a run with more plans a full batch from the same archive snapshot. Committing the
// short batch as a checkpoint boundary made a run resumed with a larger budget plan its next candidates from a different
// archive than an uninterrupted run. A truncated batch therefore never becomes a boundary: the checkpoint stays at the
// boundary before it, and carries the evaluator calls the batch made. A resumed run replans that batch in full (planning
// is keyed by evaluation id, so it plans the same candidates) and takes each carried call's result instead of calling
// the evaluator again, so the outcome is exact and no evaluation is repeated.
public sealed partial class EvolutionEngine<TGenome>
{
    private readonly object _callGate = new();
    // Calls made during the current batch, recorded in case the batch turns out to be truncated.
    private readonly List<CarriedEvaluationDocument> _batchCalls = new();
    // Calls carried by the checkpoint a run resumed from, consumed as the replanned batch makes them.
    private readonly Dictionary<(long EvaluationId, int Stage, int Attempt), CarriedEvaluationDocument> _carriedCalls = new();
    // The calls of a truncated batch, to be saved with the boundary before it; null once a full batch commits.
    private List<CarriedEvaluationDocument>? _pendingCarried;

    private void BeginBatchCalls()
    {
        lock (_callGate) _batchCalls.Clear();
    }

    private void RecordCall(long evaluationId, int stage, int attempt, EvolutionTaskResult result, bool abandoned)
    {
        // A cancelled call says nothing about what the evaluator returns, so it is never replayed.
        if (result.Status == EvolutionEvaluationStatus.Canceled) return;
        lock (_callGate)
            _batchCalls.Add(new CarriedEvaluationDocument
            {
                EvaluationId = evaluationId,
                Stage = stage,
                Attempt = attempt,
                Abandoned = abandoned,
                Result = TaskResultDocument.From(result)
            });
    }

    private bool TryTakeCarried(long evaluationId, int stage, int attempt, out EvolutionTaskResult result)
    {
        CarriedEvaluationDocument? call;
        lock (_callGate)
        {
            if (!_carriedCalls.TryGetValue((evaluationId, stage, attempt), out call))
            {
                result = new EvolutionTaskResult(EvolutionEvaluationStatus.Canceled);
                return false;
            }
            _carriedCalls.Remove((evaluationId, stage, attempt));
        }

        // An abandoned call raised the abandonment counter when it ran; replaying it raises it again.
        if (call.Abandoned) Interlocked.Increment(ref _abandonedEvaluations);
        result = call.Result?.ToTaskResult()
            ?? throw new InvalidDataException("A carried evaluation in the checkpoint has no result.");
        // Recorded again, so a replanned batch that the budget cuts short a second time still carries it.
        RecordCall(evaluationId, stage, attempt, result, call.Abandoned);
        return true;
    }

    // After a batch commits: a full batch is a boundary and carries nothing; a truncated one keeps the earlier boundary
    // and carries its calls.
    private void SettleBatchCalls(bool truncated)
    {
        lock (_callGate)
        {
            _pendingCarried = truncated ? new List<CarriedEvaluationDocument>(_batchCalls) : null;
            _carriedCalls.Clear();
        }
    }

    // Puts the truncated batch's calls into the boundary that is about to be saved. The boundary is a new revision, so
    // its sequence moves on: a store must never see one sequence with two different payloads.
    private void AttachCarried()
    {
        List<CarriedEvaluationDocument>? carried;
        lock (_callGate)
        {
            carried = _pendingCarried;
            _pendingCarried = null;
        }
        if (carried is null || carried.Count == 0) return;
        if (_safeDocument is { } document)
        {
            document.CarriedEvaluations = carried;
        }
        else if (_safePayload is { } payload)
        {
            // Once serialized the boundary no longer tracks later changes, so the calls are added to its JSON directly.
            var node = System.Text.Json.Nodes.JsonNode.Parse(payload)?.AsObject()
                ?? throw new InvalidOperationException("The checkpoint boundary is not a JSON object.");
            node["CarriedEvaluations"] = System.Text.Json.JsonSerializer.SerializeToNode(carried,
                EvolutionStateJsonContext.Default.ListCarriedEvaluationDocument);
            _safePayload = node.ToJsonString();
        }
        else
        {
            return;
        }
        _safeSequence++;
    }

    private void LoadCarried(EngineStateDocument state)
    {
        lock (_callGate)
        {
            _carriedCalls.Clear();
            foreach (CarriedEvaluationDocument call in state.CarriedEvaluations ?? new List<CarriedEvaluationDocument>())
            {
                if (call is null || call.Result is null || call.EvaluationId < 0 || call.Attempt < 1 ||
                    call.EvaluationId >= state.NextEvaluationId + EvolutionCollectionLimits.MaximumResultEntries)
                    throw new InvalidDataException("A carried evaluation in the checkpoint is invalid.");
                var key = (call.EvaluationId, call.Stage, call.Attempt);
                if (_carriedCalls.ContainsKey(key))
                    throw new InvalidDataException("The checkpoint carries one evaluation call twice.");
                _carriedCalls[key] = call;
            }
        }
    }
}