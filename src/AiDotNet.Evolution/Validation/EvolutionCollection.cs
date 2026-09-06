namespace AiDotNet.Evolution;

/// <summary>Bounded materialization shared by public collection boundaries.</summary>
internal static class EvolutionCollection
{
    internal static T[] ToBoundedArray<T>(IEnumerable<T> values, int maximum, string parameterName)
    {
        Guard.NotNull(values);
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        int knownCount = 4;
        if (values is ICollection<T> collection)
        {
            if (collection.Count < 0) throw NegativeCount(parameterName);
            if (collection.Count > maximum) throw TooMany(maximum, parameterName);
            knownCount = collection.Count;
        }
        else if (values is IReadOnlyCollection<T> readOnly)
        {
            if (readOnly.Count < 0) throw NegativeCount(parameterName);
            if (readOnly.Count > maximum) throw TooMany(maximum, parameterName);
            knownCount = readOnly.Count;
        }

        var result = new List<T>(Math.Min(maximum, knownCount));
        foreach (T value in values)
        {
            if (result.Count == maximum) throw TooMany(maximum, parameterName);
            result.Add(value);
        }
        return result.ToArray();
    }

    internal static T[] CopyBounded<T>(IReadOnlyList<T> values, int maximum, string parameterName)
    {
        Guard.NotNull(values);
        if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (values.Count < 0) throw NegativeCount(parameterName);
        if (values.Count > maximum) throw TooMany(maximum, parameterName);
        var result = new T[values.Count];
        for (int index = 0; index < result.Length; index++) result[index] = values[index];
        return result;
    }

    private static ArgumentException TooMany(int maximum, string parameterName) =>
        new($"A collection may contain at most {maximum} entries.", parameterName);

    private static ArgumentException NegativeCount(string parameterName) =>
        new("A collection reported a negative count.", parameterName);
}
