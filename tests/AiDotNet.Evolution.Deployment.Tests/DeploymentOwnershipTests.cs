extern alias AiDotNetConsumer;
using AiDotNet.Evolution;
using AiDotNet.Evolution.AutoML;
using AiDotNet.Evolution.Deployment;
using AiDotNet.Tensors.LinearAlgebra;
using Xunit;

namespace AiDotNet.Evolution.Deployment.Tests;

public sealed class DeploymentOwnershipTests
{
    [Fact]
    public void Deployment_and_evolution_search_are_owned_by_the_standalone_package()
    {
        var assembly = typeof(EvolutionDeploymentLifecycle).Assembly;
        Assert.Equal("AiDotNet.Evolution.Deployment", assembly.GetName().Name);
        Assert.Same(assembly, typeof(MapElitesAutoML<double, Matrix<double>, Vector<double>>).Assembly);
        Assert.Equal("AiDotNet.Evolution", typeof(EvolutionEngine<>).Assembly.GetName().Name);
        Assert.NotSame(assembly, typeof(AiDotNetConsumer::AiDotNet.Regression.MultipleRegression<double>).Assembly);
    }
}
