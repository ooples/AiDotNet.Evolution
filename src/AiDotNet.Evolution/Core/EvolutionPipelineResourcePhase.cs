namespace AiDotNet.Evolution;

/// <summary>Pre-admits bounded resource work in logical order, before any phase callbacks can refund capacity.</summary>
internal sealed class EvolutionPipelineResourcePhase(EvolutionResourceLedger ledger) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private bool _closed;

    public bool Reserve(string operation, EvolutionResourceStage stage, EvolutionResources maximum, int attempt = 1)
    {
        lock (_gate)
        {
            if (_closed || _slots.Count >= 65536 || _slots.ContainsKey(operation))
                throw new InvalidOperationException("Pipeline resource phase is closed, oversized or repeats an operation.");
            var slot = new Slot(ledger.TryReserve(operation, stage, maximum, maximum, attempt), maximum);
            _slots.Add(operation, slot);
            return slot.Reservation is not null;
        }
    }

    public EvolutionResourceReservation? Take(string operation)
    {
        lock (_gate)
        {
            if (_closed || !_slots.TryGetValue(operation, out Slot? slot) || slot.Taken)
                throw new InvalidOperationException("Unplanned or repeated pipeline resource dispatch.");
            slot.Taken = true;
            return slot.Reservation;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Exception? failure = null;
            foreach (Slot slot in _slots.Values)
            {
                if (slot.Reservation is null) continue;
                try
                {
                    if (!slot.Taken)
                        slot.Reservation.Complete(new EvolutionResources(slot.Maximum.Amounts.ToDictionary(pair => pair.Key, _ => 0m, StringComparer.Ordinal)), EvolutionResourceOutcome.Rejected);
                    // A taken callback without a settled receipt keeps its maximum as unknown; never refund dispatched work.
                    slot.Reservation.Dispose();
                }
                catch (Exception exception) when (EvolutionExceptionPolicy.IsRecoverable(exception)) { failure ??= exception; }
            }
            if (failure is not null) throw new InvalidOperationException("Pipeline resource cleanup found conflicting external ledger state; remaining reservations were still reconciled.", failure);
        }
    }

    private sealed class Slot(EvolutionResourceReservation? reservation, EvolutionResources maximum)
    {
        public EvolutionResourceReservation? Reservation { get; } = reservation;
        public EvolutionResources Maximum { get; } = maximum;
        public bool Taken { get; set; }
    }
}
