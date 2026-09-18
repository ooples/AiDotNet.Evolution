#if !NET471
using AiDotNet.Evolution;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The one thing the mutation operator must never do: propose its own parent.
/// </summary>
/// <remarks>
/// <para>
/// TOUCHING A VALUE IS NOT CHANGING IT. <c>ParameterSpace.Create</c> clamps to the bounds
/// and snaps to the step, so a Gaussian step smaller than half a step -- or one that
/// overshoots a bound the parent already sits on -- normalises straight back to the
/// parent's value. The operator's own "at least one was touched" guard cannot see this:
/// it knows a value was perturbed, not that the perturbation survived.
/// </para>
/// <para>
/// The engine folds the duplicate before any evaluator sees it, so this never surfaces as
/// a wrong answer or a wasted evaluation. It surfaces as a run that searched less than it
/// was asked to, which nothing counts.
/// </para>
/// </remarks>
public sealed class ParameterVariationTests
{
    private static ParameterSpace Space(double min, double max, double step) =>
        new(new List<ParameterDefinition> { new("x", min, max, step, false) });

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void AProposalIdenticalToItsParentIsMovedOff(double at)
    {
        // Every position, because the two bounds fail for a different reason from the
        // middle: at a bound half the Gaussian is clamped away, in the middle a small
        // step is snapped away.
        ParameterSpace space = Space(0, 1, 0.1);
        ParameterGenome parent = space.Create(new[] { at });
        ParameterGenome collapsed = space.Create(new[] { at });

        ParameterGenome moved = ParameterVariation.EnsureDifferent(
            collapsed, parent, new StableRandom(1234UL));

        Assert.NotEqual(parent.CanonicalId(), moved.CanonicalId());
        // ...and it moves by ONE step, not to somewhere arbitrary: a mutation that
        // teleports is not a neighbourhood search.
        Assert.Equal(0.1, Math.Abs(moved.Values[0] - parent.Values[0]), 9);
    }

    [Fact]
    public void AProposalThatAlreadyDiffersIsLeftAlone()
    {
        ParameterSpace space = Space(0, 1, 0.1);
        ParameterGenome parent = space.Create(new[] { 0.5 });
        ParameterGenome child = space.Create(new[] { 0.8 });

        ParameterGenome result = ParameterVariation.EnsureDifferent(
            child, parent, new StableRandom(1UL));

        Assert.Equal(child.CanonicalId(), result.CanonicalId());
    }

    [Fact]
    public void ADimensionWithOneRepresentablePointCannotMove()
    {
        // The honest limit: a step wider than the range leaves no neighbour to propose,
        // and inventing one would mean returning a value outside the declared bounds.
        ParameterSpace space = Space(0, 1, 4);
        ParameterGenome parent = space.Create(new[] { 0.0 });
        ParameterGenome collapsed = space.Create(new[] { 0.0 });

        ParameterGenome result = ParameterVariation.EnsureDifferent(
            collapsed, parent, new StableRandom(5UL));

        Assert.Equal(parent.CanonicalId(), result.CanonicalId());
    }

    [Fact]
    public void TheMovedDimensionVariesWithTheRandomStream()
    {
        // Always forcing the FIRST dimension would bias every collapsed proposal along
        // one axis, which over a run is a systematic distortion of the search.
        var parameters = new List<ParameterDefinition>();
        for (int i = 0; i < 8; i += 1) parameters.Add(new ParameterDefinition($"p{i}", 0, 1, 0.1, false));
        var space = new ParameterSpace(parameters);
        double[] values = Enumerable.Repeat(0.5, 8).ToArray();
        ParameterGenome parent = space.Create(values);

        var moved = new HashSet<int>();
        for (ulong seed = 1; seed <= 40; seed += 1)
        {
            ParameterGenome result = ParameterVariation.EnsureDifferent(
                space.Create(values), parent, new StableRandom(seed));
            for (int i = 0; i < 8; i += 1)
            {
                if (Math.Abs(result.Values[i] - parent.Values[i]) > 1e-9) moved.Add(i);
            }
        }

        Assert.True(moved.Count > 1, $"only dimension(s) {string.Join(",", moved)} ever moved");
    }

    [Theory]
    [InlineData(0.0, 0.01)]
    [InlineData(1.0, 0.01)]
    [InlineData(0.0, 0.1)]
    [InlineData(1.0, 0.1)]
    [InlineData(0.0, 0.5)]
    [InlineData(1.0, 0.5)]
    public void IntegralBinaryDomainEscapesSubunitSteps(double at, double step)
    {
        // .01 is also HostSession's default step for [0,1]. Integral rounding used to
        // collapse both local probes, even though the other binary value is admissible.
        var space = new ParameterSpace(new[] { new ParameterDefinition("x", 0, 1, step, true) });
        ParameterGenome parent = space.Create(new[] { at });

        ParameterGenome moved = ParameterVariation.EnsureDifferent(parent, parent, new StableRandom(1234UL));

        Assert.NotEqual(parent.CanonicalId(), moved.CanonicalId());
        Assert.Equal(1 - at, moved.Values[0]);
        Assert.Equal(moved.CanonicalId(), space.Create(moved.Values).CanonicalId());
    }

    [Fact]
    public void SingletonIntegralDomainCannotInventANeighbour()
    {
        var space = new ParameterSpace(new[] { new ParameterDefinition("x", 0.1, 1.9, 0.01, true) });
        ParameterGenome parent = space.Create(new[] { 1.0 });

        ParameterGenome moved = ParameterVariation.EnsureDifferent(parent, parent, new StableRandom(1UL));

        Assert.Equal(parent.CanonicalId(), moved.CanonicalId());
        Assert.Equal(1.0, moved.Values[0]);
    }

    [Fact]
    public void MovingOneDimensionDoesNotRenormalizeAnotherIntoADifferentValue()
    {
        var space = new ParameterSpace(new[]
        {
            new ParameterDefinition("integral", 0.5, 3.5, 1, true),
            new ParameterDefinition("continuous", 0, 1, 0.1, false),
        });
        ParameterGenome parent = space.Create(new[] { 1.0, 0.5 });

        ParameterGenome moved = ParameterVariation.EnsureDifferent(parent, parent, new StableRandom(1UL));

        int changed = Enumerable.Range(0, parent.Values.Count)
            .Count(index => parent.Values[index] != moved.Values[index]);
        Assert.Equal(1, changed);
        Assert.Equal(moved.CanonicalId(), space.Create(moved.Values).CanonicalId());
    }

    [Fact]
    public void AnOverflowingUpperNeighbourUsesTheNearbyLowerStepInsteadOfTheMinimum()
    {
        var parameter = new ParameterDefinition("x", 1e308, 1.7e308, 1e307, false);
        var space = new ParameterSpace(new[] { parameter });
        ParameterGenome parent = space.Create(new[] { parameter.Maximum });
        Assert.True(double.IsPositiveInfinity(parent.Values[0] + parameter.Step));

        ParameterGenome moved = ParameterVariation.EnsureDifferent(parent, parent, new StableRandom(1UL));

        Assert.Equal(parameter.Normalize(parent.Values[0] - parameter.Step), moved.Values[0]);
        Assert.NotEqual(parameter.Minimum, moved.Values[0]);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void GeneratedOverflowClampsToItsOwnBoundary(double generated)
    {
        var parameter = new ParameterDefinition("x", 1e308, 1.7e308, 1e305, false);

        double bounded = ParameterVariation.ClampGeneratedValue(parameter, generated);

        Assert.Equal(double.IsPositiveInfinity(generated) ? parameter.Maximum : parameter.Minimum, bounded);
        Assert.True(double.IsFinite(parameter.Normalize(bounded)));
        // External non-finite values retain the established fallback policy.
        Assert.Equal(parameter.Minimum, parameter.Normalize(generated));
    }
}
#endif
