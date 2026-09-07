namespace AiDotNet.Evolution;

/// <summary>Creates an independently owned immutable snapshot of an evolution genome.</summary>
/// <typeparam name="TGenome">The concrete genome type returned by the snapshot operation.</typeparam>
/// <remarks>
/// <para>
/// Evolution archives retain canonical genomes and safely share them with selection, variation, migration,
/// observation, and checkpoint readers. A reference type used as a genome must therefore implement this contract.
/// <see cref="CreateOwnedSnapshot"/> must return a new instance whose complete reachable state is immutable for the
/// object's lifetime. Nested arrays, collections, and objects must be copied recursively; wrapping a caller-owned
/// collection in a read-only view is not sufficient. <see cref="string"/> and value types whose complete field graph
/// is already value-based satisfy the engine boundary without implementing this interface. A value type containing
/// a reference must implement the contract because copying the value alone does not copy the referenced object.
/// </para>
/// <para>
/// The engine calls this operation exactly once when a canonical genome crosses the retention boundary, avoiding
/// repeated cloning on archive, migration, and selection hot paths. Reference types must return a different instance;
/// returning <see langword="this"/> is rejected even when the object appears immutable.
/// </para>
/// </remarks>
public interface IImmutableEvolutionGenome<TGenome>
{
    /// <summary>Creates a new, independently owned immutable snapshot of this genome.</summary>
    /// <returns>A snapshot that shares no mutable reachable state with this instance or its construction inputs.</returns>
    TGenome CreateOwnedSnapshot();
}
