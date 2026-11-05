namespace AiDotNet.Evolution;

/// <summary>A task with archive descriptors measured against the population, which it may re-read as the run goes.</summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// <para>
/// Some descriptors are relative: OpenEvolve's <c>diversity</c> axis is a program's mean distance to a reference set
/// drawn from the population. Its reference set changes, but a descriptor measured against an old reference set is
/// filed in a cell that no longer means what the axis says. A task implementing this interface owns that reading. The
/// engine asks it for the current values of its relative axes whenever an evaluation is committed. After each
/// committed batch it calls <see cref="TryRebase"/>. When that returns <see langword="true"/>, the engine re-measures
/// every island archive, re-keys the cross-island elite index, and only then draws the next parent. So no archive ever
/// holds coordinates from two readings.
/// </para>
/// <para>
/// The reading is part of the run's state: <see cref="CaptureRebaseState"/> is written into every checkpoint and
/// <see cref="RestoreRebaseState"/> is applied before a resumed run proposes anything. A resumed run therefore files
/// candidates exactly as the uninterrupted run would have. <see cref="TryRebase"/> must be a deterministic function of
/// its arguments and the task's state, and it runs on the engine's sequential commit step, never concurrently.
/// </para>
/// <para><b>For Beginners:</b> Most archive axes measure a program on its own, such as its length. A "how different is
/// this from the others" axis can only be measured against some others, and as the search moves on, the right others
/// change. This interface lets a task change who it compares against, and tells the engine to re-file everything it
/// has kept against the new comparison so the archive stays consistent.</para>
/// </remarks>
public interface IRebasingEvolutionTask<TGenome> : IEvolutionTask<TGenome>
{
    /// <summary>Gets whether this task has relative descriptors at all; when <see langword="false"/> the engine skips
    /// every other member.</summary>
    bool RebasesDescriptors { get; }

    /// <summary>Measures the relative descriptors of one genome against the current reading.</summary>
    /// <param name="genome">The genome to measure; it must not be mutated.</param>
    /// <returns>Only the relative descriptors, by name. The engine overwrites those names in an evaluation's descriptors
    /// and keeps every other value.</returns>
    IReadOnlyDictionary<string, double> MeasureRebasable(TGenome genome);

    /// <summary>Decides, after a committed batch, whether to change the reading.</summary>
    /// <param name="completedEvaluations">Completed evaluations so far in the run, including resumed ones.</param>
    /// <param name="islands">Every island's current entries, indexed by island.</param>
    /// <returns><see langword="true"/> when the reading changed, so the engine must re-measure every archive.</returns>
    bool TryRebase(long completedEvaluations, IReadOnlyList<IReadOnlyList<EvolutionArchiveEntry<TGenome>>> islands);

    /// <summary>Captures the current reading for a checkpoint.</summary>
    string CaptureRebaseState();

    /// <summary>Restores a reading captured by <see cref="CaptureRebaseState"/>.</summary>
    /// <param name="state">The captured state.</param>
    /// <param name="completedEvaluations">
    /// The checkpoint's completed-evaluation count. A reading can never have been taken later than this, so a state
    /// that claims otherwise is malformed.
    /// </param>
    /// <exception cref="System.IO.InvalidDataException">
    /// The state is malformed, does not belong to this task, or records a reading later than
    /// <paramref name="completedEvaluations"/>.
    /// </exception>
    void RestoreRebaseState(string state, long completedEvaluations);
}
