namespace AiDotNet.Evolution;

/// <summary>Marks a reference-typed genome whose complete reachable state is immutable.</summary>
/// <remarks>
/// <para>
/// Evolution archives retain canonical genomes and safely share them with selection, variation, migration,
/// observation, and checkpoint readers. A reference type used as a genome must therefore implement this marker and
/// keep every value reachable through it immutable for the object's entire lifetime. The contract includes nested
/// arrays, collections, and objects: exposing a mutable collection through a read-only interface is not sufficient.
/// <see cref="string"/> and value types whose complete field graph is already value-based satisfy the engine
/// boundary without implementing this interface. A value type containing an arbitrary reference must implement the
/// marker because copying the value does not copy the referenced object.
/// </para>
/// <para>
/// This explicit type contract avoids defensive serialization or cloning on archive hot paths. Implement it only after
/// construction makes private snapshots of mutable inputs and the public surface cannot mutate those snapshots.
/// </para>
/// </remarks>
public interface IImmutableEvolutionGenome
{
}
