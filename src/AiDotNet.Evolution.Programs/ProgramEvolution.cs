namespace AiDotNet.Evolution.Programs;

/// <summary>Standalone program search entry point; no AiModelBuilder or model package is required.</summary>
public static class ProgramEvolution
{
    /// <summary>Creates a correctness-gated engine using one explicitly shared proposal/evaluation ledger.</summary>
    /// <remarks>Correctness must be trusted and independently versioned. Evaluators own process isolation.
    /// Hold final confirmation outside this search. The maximum includes correctness and fitness work.
    /// Automatic resume is refused; coordinated engine/ledger persistence remains an explicit lower-level workflow.</remarks>
    public static EvolutionEngine<ProgramGenome> CreateEngine(
        IProgramVariationOperator variation, IProgramFitnessEvaluator correctness, IProgramFitnessEvaluator fitness,
        ProgramEvolutionResourceOptions resources, EvolutionEngineOptions options,
        Func<int, IEvolutionArchive<ProgramGenome>> archiveFactory,
        IEvolutionObserver<ProgramGenome>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(variation);
        ArgumentNullException.ThrowIfNull(correctness);
        ArgumentNullException.ThrowIfNull(fitness);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(archiveFactory);
        if (options.Resume) throw new ArgumentException("Automatic resume cannot restore the shared resource ledger.", nameof(options));
        if (options.CheckpointInterval != 0)
            throw new ArgumentException("Automatic checkpointing cannot persist the shared resource ledger.", nameof(options));
        if (options.EnableEvaluationCache)
            throw new ArgumentException("Fresh correctness requires disabling the engine evaluation cache; use explicit fitness-only reuse instead.", nameof(options));
        if (fitness is IProgramResourceLedgerProvider fitnessResources && !ReferenceEquals(fitnessResources.Ledger, resources.Ledger))
            throw new ArgumentException("Fitness persistence and evaluation must share the same live ledger.", nameof(resources));
        if (correctness is IProgramResourceLedgerProvider correctnessResources && !ReferenceEquals(correctnessResources.Ledger, resources.Ledger))
            throw new ArgumentException("Correctness and evaluation must share the same live ledger.", nameof(resources));
        if (variation is not IProgramResourceLedgerProvider provider || variation is not IEvolutionProposalCostProvider)
            throw new ArgumentException("Supply a metered proposal operator with owned resource receipts.", nameof(variation));
        if (!ReferenceEquals(provider.Ledger, resources.Ledger))
            throw new ArgumentException("Proposal and evaluation must share the same live ledger.", nameof(resources));
        if (variation is IEvolutionProposalCostProvider cost && cost.CostUnitVersionHash != resources.CostUnitVersionHash)
            throw new ArgumentException("Proposal and evaluation must share cost-unit semantics.", nameof(resources));
        var gate = new CorrectnessGatedProgramFitnessEvaluator(
            new VersionPinnedProgramFitnessEvaluator(correctness), new VersionPinnedProgramFitnessEvaluator(fitness));
        var task = new ResourceMeteredEvolutionTask<ProgramGenome>(new TaskAdapter(gate, resources.CostUnitVersionHash),
            resources.Ledger, new[] { resources.MaximumEvaluationCostUnits });
        return new EvolutionEngine<ProgramGenome>(task, variation, archiveFactory, options, observer: observer);
    }

    private sealed class TaskAdapter(IProgramFitnessEvaluator evaluator, string costUnitVersion) : IEvolutionTask<ProgramGenome>
    {
        public string Id => "standalone-program";
        public string VersionHash => EvolutionHash.Combine(new[] { "standalone-program-exact-source-v1", costUnitVersion });
        public string EvaluatorVersionHash => evaluator.VersionHash;
        public ValueTask<EvolutionCanonicalGenome<ProgramGenome>> CanonicalizeAsync(ProgramGenome genome, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(genome);
            cancellationToken.ThrowIfCancellationRequested();
            return new(new EvolutionCanonicalGenome<ProgramGenome>(genome.CreateOwnedSnapshot(), genome.Id));
        }
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<ProgramGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default) =>
            evaluator.EvaluateAsync(candidate.CanonicalGenome.Genome, context, cancellationToken);
    }
}
