using System.Diagnostics.CodeAnalysis;

namespace AiDotNet.Evolution.Programs;

internal static class ProgramGuard
{
    internal static void NotNull([NotNull] object? value)
    {
        ArgumentNullException.ThrowIfNull(value);
    }

    internal static void NotNullOrWhiteSpace([NotNull] string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A nonempty value is required.", nameof(value));
    }
}
