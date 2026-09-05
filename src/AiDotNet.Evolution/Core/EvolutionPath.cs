namespace AiDotNet.Evolution;

/// <summary>Provides path joining whose later components never discard an earlier root.</summary>
internal static class EvolutionPath
{
    /// <summary>Joins two path components without treating the second component as a replacement root.</summary>
    internal static string Join(string first, string second)
    {
#if NET471
        if (string.IsNullOrEmpty(first)) return second;
        if (string.IsNullOrEmpty(second)) return first;

        bool firstEndsWithSeparator = IsDirectorySeparator(first[first.Length - 1]);
        bool secondStartsWithSeparator = IsDirectorySeparator(second[0]);
        if (firstEndsWithSeparator && secondStartsWithSeparator) return first + second.Substring(1);
        if (firstEndsWithSeparator || secondStartsWithSeparator) return first + second;
        return first + Path.DirectorySeparatorChar + second;
#else
        return Path.Join(first, second);
#endif
    }

#if NET471
    private static bool IsDirectorySeparator(char value) =>
        value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
#endif
}
