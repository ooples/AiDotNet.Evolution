namespace AiDotNet.Evolution;

internal static class EvolutionArchiveGeometry
{
    internal static long CellCount<TGenome>(IEvolutionArchiveView<TGenome> archive)
    {
        if (archive is IEvolutionArchiveCellCount geometry)
        {
            long count = geometry.TotalCells;
            if (count <= 0 || count < archive.Count)
                throw new InvalidOperationException("Archive geometry has an invalid cell count.");
            return count;
        }

        long cells = 1;
        foreach (EvolutionDescriptorDefinition descriptor in archive.Descriptors)
        {
            if (cells > long.MaxValue / descriptor.EffectiveBinCount) return long.MaxValue;
            cells *= descriptor.EffectiveBinCount;
        }
        return Math.Max(1, cells);
    }
}
