namespace AiDotNet.Evolution;

/// <summary>Time operations through a <see cref="TimeProvider"/>, on .NET 8+ and (via Microsoft.Bcl.TimeProvider) net471.</summary>
internal static class EvolutionClock
{
    /// <summary>A source cancelled after <paramref name="delay"/> as measured by <paramref name="time"/>.</summary>
    public static CancellationTokenSource CreateCancellationTokenSource(TimeProvider time, TimeSpan delay) =>
#if NET8_0_OR_GREATER
        new(delay, time);
#else
        time.CreateCancellationTokenSource(delay);
#endif

    /// <summary>Waits <paramref name="delay"/> as measured by <paramref name="time"/>.</summary>
    public static Task Delay(TimeProvider time, TimeSpan delay, CancellationToken cancellationToken) =>
#if NET8_0_OR_GREATER
        Task.Delay(delay, time, cancellationToken);
#else
        time.Delay(delay, cancellationToken);
#endif
}
