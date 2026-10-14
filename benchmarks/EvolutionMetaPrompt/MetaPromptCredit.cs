using System.Collections.Concurrent;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.MetaPromptAblation;

/// <summary>
/// Drives <see cref="ProgramMetaPromptEvolver"/> from the engine's own event stream: credits each
/// instruction with the quality of the program it produced, and asks for a new one on the evolver's cadence.
/// </summary>
/// <remarks>
/// <para>
/// The evolver supplies instructions through <see cref="IProgramInstructionSource"/>, which the variation
/// operator already consumes, but nothing in the shipped engine tells it what the programs achieved - its own
/// documentation says "whoever owns the run tells the source what the programs achieved". This is that owner.
/// </para>
/// <para>
/// It is also the finding behind this benchmark: the CLI has no run-file field for meta-prompt evolution, so a
/// user cannot switch it on at all today. The mechanism is implemented and tested; this bridge and a run-file
/// section are what is missing.
/// </para>
/// <para>
/// Routed generations are <b>recorded</b> rather than inferred, because <c>Observe</c> refuses a generation it
/// never routed and the engine evaluates seeds that no proposal produced. Inferring "anything past generation
/// zero was routed" would be right today and wrong the moment a warm start or a cache hit changed which
/// generations reach the operator.
/// </para>
/// </remarks>
internal sealed class MetaPromptCredit : IProgramInstructionSource, IEvolutionObserver<ProgramGenome>
{
    private readonly ProgramMetaPromptEvolver _evolver;
    private readonly EvolutionResourceLedger? _ledger;
    private readonly ConcurrentDictionary<long, byte> _routed = new();

    internal MetaPromptCredit(ProgramMetaPromptEvolver evolver, EvolutionResourceLedger? ledger)
    {
        _evolver = evolver;
        _ledger = ledger;
    }

    /// <summary>Instructions the evolver accepted over the run.</summary>
    internal int Proposals { get; private set; }

    /// <summary>Outcomes credited back to an instruction, which is what makes the archive's scores mean anything.</summary>
    internal int Credited { get; private set; }

    /// <inheritdoc/>
    public string VersionHash => _evolver.VersionHash;

    /// <inheritdoc/>
    public string? Select(StableRandom random, long generation)
    {
        _routed[generation] = 0;
        return _evolver.Select(random, generation);
    }

    /// <inheritdoc/>
    public async ValueTask OnEventAsync(EvolutionEvent<ProgramGenome> evolutionEvent, CancellationToken cancellationToken = default)
    {
        if (evolutionEvent is null || evolutionEvent.Kind != EvolutionEventKind.Evaluated) return;
        if (evolutionEvent.Evaluation is not { } evaluation) return;

        long generation = evaluation.Lineage.Generation;
        if (_routed.TryRemove(generation, out _))
        {
            // A null quality is a failed evaluation, which is still information about the instruction that
            // produced it, so it is credited as the worst outcome rather than skipped: skipping would let an
            // instruction whose programs never parse keep a clean record.
            _evolver.Observe(generation, evaluation.Quality ?? 0.0);
            Credited++;
        }

        if (!_evolver.ShouldPropose(generation + 1)) return;
        string? proposed = await _evolver.ProposeAsync(generation + 1, _ledger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (proposed is not null) Proposals++;
    }
}
