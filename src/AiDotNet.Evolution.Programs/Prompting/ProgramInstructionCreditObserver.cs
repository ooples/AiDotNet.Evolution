namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Credits an evolving instruction with the quality of the programs it produced, and asks for a new one on
/// the evolver's cadence (V1-53, #166).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ProgramMetaPromptEvolver"/> supplies instructions through
/// <see cref="IProgramInstructionSource"/>, which <see cref="LlmProgramVariationOperator"/> already consumes,
/// but the operator never sees outcomes - so something has to tell the evolver what the programs achieved.
/// Without it the archive's scores stay empty and selection has nothing to prefer, which makes the feature
/// inert rather than broken: instructions are proposed, used, and never judged.
/// </para>
/// <para>
/// Hand this to the engine as its observer <b>and</b> to the operator as its
/// <see cref="LlmProgramVariationOptions.InstructionSource"/>. It is both: the source half records which
/// generations were routed to which instruction, and the observer half credits them when their outcomes
/// arrive.
/// </para>
/// <para><b>For Beginners:</b> meta-prompt evolution tries different standing instructions for the model and
/// keeps the ones whose programs score well. This is the part that reports the scores back. If you wire the
/// evolver in without it, you will see instructions being proposed and no sign of any of them being
/// preferred.</para>
/// </remarks>
public sealed class ProgramInstructionCreditObserver : IProgramInstructionSource, IEvolutionObserver<ProgramGenome>
{
    private readonly ProgramMetaPromptEvolver _evolver;
    private readonly EvolutionResourceLedger? _ledger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _routed = new();

    /// <summary>Creates an observer that credits <paramref name="evolver"/> from the engine's outcomes.</summary>
    /// <param name="evolver">The evolver to route instructions from and credit outcomes to.</param>
    /// <param name="ledger">The run's ledger, charged for each instruction proposal; <c>null</c> to meter nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="evolver"/> is <c>null</c>.</exception>
    public ProgramInstructionCreditObserver(ProgramMetaPromptEvolver evolver, EvolutionResourceLedger? ledger = null)
    {
        ProgramGuard.NotNull(evolver);
        _evolver = evolver;
        _ledger = ledger;
    }

    /// <summary>Gets how many instructions the evolver accepted while this observer was attached.</summary>
    public int AcceptedProposals { get; private set; }

    /// <summary>Gets how many outcomes were credited back to an instruction.</summary>
    /// <remarks>
    /// Zero after a run that proposed instructions means the wiring is wrong rather than the feature
    /// ineffective, which is worth being able to tell apart.
    /// </remarks>
    public int CreditedOutcomes { get; private set; }

    /// <inheritdoc/>
    public string VersionHash => _evolver.VersionHash;

    /// <inheritdoc/>
    public string? Select(StableRandom random, long generation)
    {
        // Recorded rather than inferred: Observe refuses a generation it never routed, and the engine
        // evaluates seed candidates that no proposal produced. Deriving "anything past generation zero was
        // routed" is correct today and wrong as soon as a warm start, a cache hit or an island changes which
        // generations reach the operator.
        _routed[generation] = 0;
        return _evolver.Select(random, generation);
    }

    /// <inheritdoc/>
    public async ValueTask OnEventAsync(EvolutionEvent<ProgramGenome> evolutionEvent, CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(evolutionEvent);
        if (evolutionEvent.Kind != EvolutionEventKind.Evaluated || evolutionEvent.Evaluation is not { } evaluation) return;

        long generation = evaluation.Lineage.Generation;
        if (_routed.TryRemove(generation, out _))
        {
            // A null quality is a failed evaluation. Credited as the worst outcome rather than skipped: an
            // instruction whose programs never parse would otherwise keep a clean record and stay selectable.
            _evolver.Observe(generation, evaluation.Quality ?? 0.0);
            CreditedOutcomes++;
        }

        if (!_evolver.ShouldPropose(generation + 1)) return;
        string? proposed = await _evolver
            .ProposeAsync(generation + 1, _ledger, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (proposed is not null) AcceptedProposals++;
    }

    /// <summary>Releases the route for a generation whose outcome will never arrive.</summary>
    /// <param name="generation">The abandoned generation.</param>
    /// <returns><c>true</c> when a route was held and has now been released.</returns>
    /// <remarks>
    /// A cancelled run leaves routes outstanding, and the evolver will not evict an instruction that is still
    /// awaiting one. Call this when the engine abandons a proposal so a resumed run is not held up by a
    /// generation that cannot be credited.
    /// </remarks>
    public bool Abandon(long generation) => _routed.TryRemove(generation, out _) && _evolver.Abandon(generation);
}
