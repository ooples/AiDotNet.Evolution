namespace AiDotNet.Evolution;

/// <summary>An immutable admission request for one independently charged stage/attempt.</summary>
public sealed class EvolutionResourceRequest
{
    /// <summary>Creates a request. The ledger validates identities, declared resources and estimates before any batch admission.</summary>
    public EvolutionResourceRequest(string operationId, EvolutionResourceStage stage, EvolutionResources estimated,
        EvolutionResources maximum, int attempt = 1)
    {
        OperationId = operationId; Stage = stage; Estimated = estimated; Maximum = maximum; Attempt = attempt;
    }
    /// <summary>Gets the stable identity; ordinal identity order determines batch priority.</summary>
    public string OperationId { get; }
    /// <summary>Gets the work category.</summary>
    public EvolutionResourceStage Stage { get; }
    /// <summary>Gets the planning estimate.</summary>
    public EvolutionResources Estimated { get; }
    /// <summary>Gets the producer-enforced maximum reserved before dispatch.</summary>
    public EvolutionResources Maximum { get; }
    /// <summary>Gets the one-based attempt.</summary>
    public int Attempt { get; }
}

/// <summary>A batch decision; denied requests never acquire a reservation.</summary>
public sealed class EvolutionResourceAdmission
{
    internal EvolutionResourceAdmission(EvolutionResourceRequest request, EvolutionResourceReservation? reservation)
    { Request = request; Reservation = reservation; }
    /// <summary>Gets the admitted or denied request.</summary>
    public EvolutionResourceRequest Request { get; }
    /// <summary>Gets the reservation, or null for a pre-dispatch denial. Settle or dispose every admitted handle.</summary>
    public EvolutionResourceReservation? Reservation { get; }
}
