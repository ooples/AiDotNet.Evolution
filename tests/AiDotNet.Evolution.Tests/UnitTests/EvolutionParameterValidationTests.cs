using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionParameterValidationTests
{
    // Every rejected domain used to raise one generic message naming `minimum`, even when `maximum` was the
    // bad argument and even when the real reason was the ordering or an infinite width.
    [Theory]
    [InlineData(1, double.NaN, "maximum", "The maximum must be a finite number.")]
    [InlineData(1, double.PositiveInfinity, "maximum", "The maximum must be a finite number.")]
    [InlineData(2, 1, "maximum", "The maximum must be greater than or equal to the minimum.")]
    [InlineData(0, 1, "minimum", "A logarithmic minimum must be greater than zero.")]
    [InlineData(-1, 1, "minimum", "A logarithmic minimum must be greater than zero.")]
    [InlineData(double.NaN, 1, "minimum", "The minimum must be a finite number.")]
    public void LogarithmicDomainErrorsNameTheOffendingArgumentAndItsReason(double minimum, double maximum, string parameterName, string reason)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionParameter.Logarithmic("x", minimum, maximum));
        Assert.Equal(parameterName, exception.ParamName);
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, double.NaN, "maximum", "The maximum must be a finite number.")]
    [InlineData(1, double.NegativeInfinity, "maximum", "The maximum must be a finite number.")]
    [InlineData(2, 1, "maximum", "The maximum must be greater than or equal to the minimum.")]
    [InlineData(double.NaN, 1, "minimum", "The minimum must be a finite number.")]
    [InlineData(double.NegativeInfinity, 1, "minimum", "The minimum must be a finite number.")]
    public void RealDomainErrorsNameTheOffendingArgumentAndItsReason(double minimum, double maximum, string parameterName, string reason)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionParameter.Real("x", minimum, maximum));
        Assert.Equal(parameterName, exception.ParamName);
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInfiniteIntervalWidthIsReportedAgainstTheMaximumAndValidDomainsStillBuild()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionParameter.Real("x", -double.MaxValue, double.MaxValue));
        Assert.Equal("maximum", exception.ParamName);
        Assert.Contains("The interval width maximum - minimum must be finite.", exception.Message, StringComparison.Ordinal);
        // A zero or negative minimum is legal for every non-logarithmic kind, so the logarithmic rule must not leak.
        Assert.Equal(0, EvolutionParameter.Real("x", 0, 1).Minimum);
        Assert.Equal(-1, EvolutionParameter.Integer("x", -1, 1).Minimum);
        Assert.Equal(7, EvolutionParameter.Real("x", 7, 7).Maximum);
    }
}
