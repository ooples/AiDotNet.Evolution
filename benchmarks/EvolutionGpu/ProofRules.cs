internal static class ProofRules
{
    internal static bool ShouldPromote(IReadOnlyList<double> ratios) =>
        ratios.Count == 5 && ratios.All(ratio => double.IsFinite(ratio) && ratio > 1.05);

    internal static bool Correct(double actual, double expected) =>
        double.IsFinite(actual) && double.IsFinite(expected) &&
        Math.Abs(actual - expected) <= 1e-4 + 1e-3 * Math.Abs(expected);

    internal static void SelfTest()
    {
        if (!ShouldPromote(new[] { 1.1, 1.2, 1.3, 1.4, 1.5 }) ||
            ShouldPromote(new[] { 1.1, 1.2, 1.3, 1.4 }) ||
            ShouldPromote(new[] { 1.1, 1.2, 1.3, 1.4, 1.05 }) ||
            ShouldPromote(new[] { 1.1, 1.2, 1.3, 1.4, 0.9 }) ||
            ShouldPromote(new[] { 1.1, 1.2, 1.3, 1.4, double.PositiveInfinity }) ||
            ShouldPromote(new[] { 1.1, 1.2, 1.3, 1.4, double.NaN }) ||
            !Correct(0, 0) || Correct(1, 0) || Correct(double.NaN, 0) ||
            Correct(0, double.NaN) || Correct(double.PositiveInfinity, double.PositiveInfinity))
            throw new InvalidOperationException("GPU proof acceptance regression.");
        Console.WriteLine("PASS: 11 numerical/promotion acceptance cases; no CUDA device used.");
    }
}
