using System.Diagnostics.CodeAnalysis;

namespace AiDotNet.Evolution.Programs.Experience;

/// <summary>Connects an <see cref="LlmProgramVariationOperator"/> to a <see cref="ProgramExperienceStore"/>.</summary>
/// <remarks>
/// With a binding, each proposal prompt ends with lessons retrieved for this task and version, within
/// <see cref="ContextBudgetCharacters"/>, and a <see cref="ProgramExperienceRecorder"/> attached to the engine adds a
/// lesson for every evaluated program. The store keeps its own guarantees: final-test evidence never enters it, and
/// retrieval never crosses task identity or version.
/// </remarks>
[Experimental("AIDEVO004")]
public sealed class ProgramExperienceBinding
{
    /// <summary>Creates a binding.</summary>
    public ProgramExperienceBinding(ProgramExperienceStore store, string runId, string taskIdentity, string taskVersion,
        int contextBudgetCharacters = 4_000, bool includeOtherRuns = false)
    {
        ProgramGuard.NotNull(store);
        ProgramGuard.NotNullOrWhiteSpace(runId);
        ProgramGuard.NotNullOrWhiteSpace(taskIdentity);
        ProgramGuard.NotNullOrWhiteSpace(taskVersion);
        if (contextBudgetCharacters is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(contextBudgetCharacters));
        Store = store; RunId = runId; TaskIdentity = taskIdentity; TaskVersion = taskVersion;
        ContextBudgetCharacters = contextBudgetCharacters; IncludeOtherRuns = includeOtherRuns;
    }

    /// <summary>Gets the store lessons are read from and written to.</summary>
    public ProgramExperienceStore Store { get; }
    /// <summary>Gets the current run.</summary>
    public string RunId { get; }
    /// <summary>Gets the task identity lessons are scoped to.</summary>
    public string TaskIdentity { get; }
    /// <summary>Gets the task/evaluator version lessons are scoped to.</summary>
    public string TaskVersion { get; }
    /// <summary>Gets the most characters of lessons added to one prompt.</summary>
    public int ContextBudgetCharacters { get; }
    /// <summary>Gets whether lessons from other runs of the same task and version are used.</summary>
    public bool IncludeOtherRuns { get; }

    internal ProgramExperienceQuery Query() => new(RunId, TaskIdentity, TaskVersion, ContextBudgetCharacters, IncludeOtherRuns);
}

/// <summary>An engine observer that turns every evaluated program into a lesson in a <see cref="ProgramExperienceStore"/>.</summary>
/// <remarks>
/// The lesson's hypothesis is the program's <see cref="ProgramGenome.Description"/> (what the proposal said it changed).
/// A completed evaluation that entered or improved the archive is <see cref="ProgramExperienceOutcome.Improved"/>, any
/// other completed one <see cref="ProgramExperienceOutcome.NotImproved"/>, one with constraint violations
/// <see cref="ProgramExperienceOutcome.Invalid"/>, and anything that did not complete
/// <see cref="ProgramExperienceOutcome.Failed"/>. Evidence is recorded as search-partition evidence.
/// </remarks>
[Experimental("AIDEVO004")]
public sealed class ProgramExperienceRecorder : IEvolutionObserver<ProgramGenome>
{
    private readonly ProgramExperienceBinding _binding;

    /// <summary>Creates a recorder for <paramref name="binding"/>.</summary>
    public ProgramExperienceRecorder(ProgramExperienceBinding binding)
    {
        ProgramGuard.NotNull(binding);
        _binding = binding;
    }

    /// <inheritdoc/>
    public ValueTask OnEventAsync(EvolutionEvent<ProgramGenome> evolutionEvent, CancellationToken cancellationToken = default)
    {
        ProgramGuard.NotNull(evolutionEvent);
        if (evolutionEvent.Kind != EvolutionEventKind.Evaluated || evolutionEvent.Candidate is not { } candidate ||
            evolutionEvent.Evaluation is not { } evaluation || evaluation.Status == EvolutionEvaluationStatus.Canceled)
            return default;
        ProgramGenome genome = candidate.CanonicalGenome.Genome;
        ProgramExperienceOutcome outcome = evaluation.Status != EvolutionEvaluationStatus.Completed ? ProgramExperienceOutcome.Failed
            : evaluation.ConstraintViolations.Any(value => value > 0) ? ProgramExperienceOutcome.Invalid
            : evolutionEvent.InsertionResult is EvolutionArchiveInsertionResult.Inserted or EvolutionArchiveInsertionResult.Replaced
                or EvolutionArchiveInsertionResult.InsertedWithEviction ? ProgramExperienceOutcome.Improved
            : ProgramExperienceOutcome.NotImproved;
        string hypothesis = genome.Description is { } description && description.Trim().Length > 0 ? description : "(no description given)";
        _binding.Store.Add(new ProgramExperienceRecord(_binding.RunId, _binding.TaskIdentity, _binding.TaskVersion, hypothesis,
            genome.Id, outcome, ProgramEvidencePartition.Search, evaluation.Quality,
            evaluation.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message), evolutionEvent.Sequence));
        return default;
    }
}
