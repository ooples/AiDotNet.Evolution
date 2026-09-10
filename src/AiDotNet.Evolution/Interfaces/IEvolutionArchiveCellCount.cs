namespace AiDotNet.Evolution;

/// <summary>Optional geometry contract for archives whose cell count is not a product of descriptor bins.</summary>
/// <remarks>The count describes the partition, not the number of occupied cells or an eviction budget.</remarks>
public interface IEvolutionArchiveCellCount
{
    /// <summary>Gets the positive number of cells in the current partition.</summary>
    long TotalCells { get; }
}
