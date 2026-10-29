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

        // Enumerated, not copied by count: a collection may report a count it does not hold. Sized from the reported
        // count, so a truthful collection costs its one array rather than a list and a copy (E-1: a resume runs this for
        // every restored result).
        var result = new T[Math.Min(maximum, knownCount)];
        int count = 0;
        foreach (T value in values)
        {
            if (count == maximum) throw TooMany(maximum, parameterName);
            if (count == result.Length) Array.Resize(ref result, Math.Min(maximum, Math.Max(4, result.Length * 2)));
            result[count++] = value;
        }
        if (count != result.Length) Array.Resize(ref result, count);
        return result;
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
