using AiDotNet.Evolution.Performance;
using Xunit;

namespace AiDotNet.Evolution.Performance.Tests;

public sealed class ProfileGroupTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("0", 0)]
    [InlineData("63", 63)]
    public void ExplicitGroupOverridesCurrentSchedulingGroup(string? requested, int expected) =>
        Assert.Equal((ushort)expected, ProfileCampaign.SelectProcessorGroup(requested, 1));

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("64")]
    [InlineData(" 0")]
    [InlineData("0 ")]
    public void InvalidGroupDoesNotSilentlyFallBack(string requested) =>
        Assert.Throws<ArgumentException>(() => ProfileCampaign.SelectProcessorGroup(requested, 0));
}
