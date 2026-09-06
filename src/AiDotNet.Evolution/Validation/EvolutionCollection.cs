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
            knownCount = ValidateReportedCount(collection.Count, maximum, parameterName);
        }
        else if (values is IReadOnlyCollection<T> readOnly)
        {
            knownCount = ValidateReportedCount(readOnly.Count, maximum, parameterName);
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
        int count = ValidateReportedCount(values.Count, maximum, parameterName);
        var result = new T[count];
        for (int index = 0; index < result.Length; index++) result[index] = values[index];
        return result;
    }

    private static int ValidateReportedCount(int count, int maximum, string parameterName)
    {
        if ((uint)count <= (uint)maximum) return count;
        if (count > maximum) throw TooMany(maximum, parameterName);
        throw NegativeCount(parameterName);
    }

    private static ArgumentException TooMany(int maximum, string parameterName) =>
        new($"A collection may contain at most {maximum} entries.", parameterName);

    private static ArgumentException NegativeCount(string parameterName) =>
        new("A collection reported a negative count.", parameterName);
}
