using System.Reflection;

namespace AiDotNet.Evolution;

/// <summary>Validates the immutable ownership contract once per closed genome type.</summary>
internal static class EvolutionGenomeContract<TGenome>
{
    private static readonly bool IsImmutable = IsKnownImmutable(typeof(TGenome), new HashSet<Type>());

    /// <summary>Rejects a reference genome type that has not declared the immutable-genome contract.</summary>
    internal static void Require(string parameterName)
    {
        if (IsImmutable) return;
        throw new ArgumentException(
            $"Genome type '{typeof(TGenome).FullName}' contains reference state and must implement " +
            $"{nameof(IImmutableEvolutionGenome)} before instances can be retained by evolution.",
            parameterName);
    }

    private static bool IsKnownImmutable(Type type, HashSet<Type> activeTypes)
    {
        if (type == typeof(string) || typeof(IImmutableEvolutionGenome).IsAssignableFrom(type)) return true;
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
                if (!IsKnownImmutable(field.FieldType, activeTypes)) return false;
            }

            return true;
        }
        finally
        {
            activeTypes.Remove(type);
        }
    }
}
