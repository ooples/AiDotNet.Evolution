extern alias AiDotNetConsumer;
using AiDotNet.Evolution.AutoML;
using AiDotNet.Evolution.Deployment;
using AiDotNet.Evolution.Programs;
using AiDotNet.Tensors.LinearAlgebra;

string hash = new('a', 64);
var envelope = new EvolutionDeploymentEnvelope(hash, hash, hash, hash, hash, hash);
var artifact = EvolutionDeployableArtifact.FromProgram(new ProgramGenome("return 1;"), envelope);
if (artifact.ReadProgram().Source != "return 1;") throw new InvalidOperationException("Package program roundtrip failed.");
using var search = new MapElitesAutoML<double, Matrix<double>, Vector<double>>();
if (search.GetType().Assembly.GetName().Name != "AiDotNet.Evolution.Deployment")
    throw new InvalidOperationException("Wrong AutoML owner.");
if (typeof(AiDotNetConsumer::AiDotNet.Regression.MultipleRegression<double>).Assembly == search.GetType().Assembly)
    throw new InvalidOperationException("Consumer primitives must remain separate.");
Console.WriteLine("PASS: packaged deployment and MAP-Elites consumer, without project references.");
