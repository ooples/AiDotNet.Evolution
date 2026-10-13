using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Programs;

/// <summary>
/// Supplies the system instruction for one proposal, so a run's instructions can evolve instead of
/// being fixed when the operator is built (V1-53, #166).
/// </summary>
/// <remarks>
/// <para>
/// The source is handed the engine's seeded stream and the generation, so whatever it does must be
/// replayable from those two: a source holding a private random number generator would make the
/// operator's own replay guarantee false.
/// </para>
/// <para>
/// Crediting is not part of this contract. The operator supplies proposals and never sees outcomes,
/// so whoever owns the run tells the source what the programs achieved - for
/// <see cref="ProgramMetaPromptEvolver"/> that is its <c>Observe</c>.
/// </para>
/// </remarks>
public interface IProgramInstructionSource
{
    /// <summary>A stable identity for this source, folded into the operator's version hash.</summary>
    /// <remarks>
    /// An interface-typed option cannot be serialized into the operator's identity, so without this
    /// two operators differing only by their instruction source would claim the same version and a
    /// checkpoint from one would restore into the other.
    /// </remarks>
    string VersionHash { get; }

    /// <summary>
    /// Returns the instruction for <paramref name="generation"/>'s proposal, or
    /// <see langword="null"/> to leave the configured system message in place.
    /// </summary>
    /// <param name="random">The engine's seeded stream, so the choice replays.</param>
    /// <param name="generation">The generation being proposed for.</param>
    string? Select(StableRandom random, long generation);
}
