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
    public ProgramInstructionCreditObserver(
        ProgramMetaPromptEvolver evolver,
        EvolutionResourceLedger? ledger = null)
    {
        ProgramGuard.NotNull(evolver);
        _evolver = evolver;
        _ledger = ledger;
    }

    /// <summary>What a failed evaluation is credited as, so it never outranks a measured outcome.</summary>
    /// <remarks>
    /// Maximizing: <c>0.0</c>, at or below every real quality. Minimizing: the largest cost the run has
    /// actually observed, so a failure is never cheaper than a program that ran. A run whose first outcome is
    /// a failure has nothing observed yet, and <c>0.0</c> is then the only floor available.
    /// </remarks>
    /// <summary>Gets how many instructions the evolver accepted while this observer was attached.</summary>
    public int AcceptedProposals { get; private set; }

    /// <summary>Gets how many outcomes were credited back to an instruction.</summary>
    /// <remarks>
    /// Zero after a run that proposed instructions means the wiring is wrong rather than the feature
    /// ineffective, which is worth being able to tell apart.
    /// </remarks>
    public int CreditedOutcomes { get; private set; }

    /// <summary>Routed outcomes that reported no quality, so the instruction earned no score.</summary>
    /// <remarks>
    /// Reported because the archive cannot show it: an instruction is ordered by the mean of what it did
    /// score, so one that failed nine times out of ten is ranked on the one measurement it has. Only an
    /// instruction that never scores at all sorts last. Ranking by failure rate would mean the archive
    /// carrying failure counts through its checkpoint, which is a change to the evolver rather than to this
    /// observer.
    /// </remarks>
    public int FailedOutcomes { get; private set; }

    /// <inheritdoc/>
    public string VersionHash => _evolver.VersionHash;

    /// <inheritdoc/>
    public string? Select(StableRandom random, long generation)
    {
        // Recorded rather than inferred: Observe refuses a generation it never routed, and the engine
        // evaluates seed candidates that no proposal produced. Deriving "anything past generation zero was
        // routed" is correct today and wrong as soon as a warm start, a cache hit or an island changes which
        // generations reach the operator.
        // Recorded only AFTER the evolver accepts: Select throws when the generation is already routed or
        // the pending table is full, and a route recorded first would leave this observer holding one the
        // evolver does not have - so a later outcome would call Observe for an unrouted generation and throw
        // inside the engine's observer path.
        string? instruction = _evolver.Select(random, generation);
        _routed[generation] = 0;
        return instruction;
    }

    /// <inheritdoc/>
    public async ValueTask OnEventAsync(EvolutionEvent<ProgramGenome> evolutionEvent, CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(evolutionEvent);
        if (evolutionEvent.Kind != EvolutionEventKind.Evaluated || evolutionEvent.Evaluation is not { } evaluation) return;

        long generation = evaluation.Lineage.Generation;
        if (_routed.TryRemove(generation, out _))
        {
            // A null quality is a failed evaluation, credited rather than skipped: an instruction whose
            // programs never parse would otherwise keep a clean record and stay selectable. The value depends
            // on the direction - see FailureQuality - because the evolver negates quality when minimizing.
            if (evaluation.Quality is { } measured)
            {
                _evolver.Observe(generation, measured);
                CreditedOutcomes++;
            }
            else
            {
                // No quality was measured, so there is nothing to credit. Releasing the route is what the
                // evolver offers for exactly this case; any stand-in number is a score the run never
                // measured, and whichever number is chosen can beat a real outcome - 0.0 beats every
                // positive cost when minimizing, and beats every negative quality when maximizing.
                _evolver.Abandon(generation);
                FailedOutcomes++;
            }
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
