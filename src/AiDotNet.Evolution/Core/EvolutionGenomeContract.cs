using System.Reflection;

namespace AiDotNet.Evolution;

/// <summary>Validates the immutable ownership contract once per closed genome type.</summary>
internal static class EvolutionGenomeContract<TGenome>
{
    private static readonly bool IsDeepValueImmutable = IsKnownImmutableValue(typeof(TGenome), new HashSet<Type>());

    /// <summary>Returns the value copy or a genome-provided independently owned snapshot.</summary>
    internal static TGenome CaptureOwned(TGenome genome, string parameterName)
    {
        if (IsDeepValueImmutable) return genome;
        if (genome is not IImmutableEvolutionGenome<TGenome> snapshotProvider)
        {
            throw new ArgumentException(
                $"Genome type '{typeof(TGenome).FullName}' contains reference state and must implement " +
                $"{typeof(IImmutableEvolutionGenome<TGenome>).Name} before instances can be retained by evolution.",
                parameterName);
        }

        TGenome snapshot = snapshotProvider.CreateOwnedSnapshot();
        if (snapshot is null)
            throw new ArgumentException("The genome ownership contract returned a null snapshot.", parameterName);
        if (!typeof(TGenome).IsValueType && ReferenceEquals(genome, snapshot))
            throw new ArgumentException(
                "A reference genome must return a new independently owned snapshot rather than itself.",
                parameterName);
        return snapshot;
    }

    private static bool IsKnownImmutableValue(Type type, HashSet<Type> activeTypes)
    {
        if (type == typeof(string)) return true;
        if (!type.IsValueType || type.IsPointer || type.IsByRef) return false;
        if (type.IsPrimitive || type.IsEnum) return true;

        // A value type is copied at the archive boundary, but references nested inside it are not. Walk its
        // declared storage once per closed genome type so an array/list hidden in a struct cannot bypass the
        // same explicit ownership contract required of a class.
        if (!activeTypes.Add(type)) return true;
        try
        {
            foreach (FieldInfo field in type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!IsKnownImmutableValue(field.FieldType, activeTypes)) return false;
            }

            return true;
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }
}
