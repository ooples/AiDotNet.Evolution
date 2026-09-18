using System.Globalization;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Quality;

/// <summary>Independent seeded instances of fixed, family-disjoint benchmark definitions.</summary>
internal sealed class SuiteNumericTask
{
    private readonly double[] _coefficients;
    private readonly ulong _seed;
    internal SuiteNumericTask(string id, ulong instanceSeed)
    {
        (Family, Partition, WorkUnits) = id switch
        {
            "block-trap" => ("deceptive-binary-blocks", "development", 1),
            "rastrigin" => ("periodic-separable-landscapes", "development", 1),
            "knapsack" => ("capacity-selection", "development", 1),
            "stochastic-regression" => ("noisy-linear-prediction", "selection", 16),
            "diffusion-control" => ("time-stepped-diffusion", "selection", 256),
            "coupled-absolute" => ("nonsmooth-coupled-losses", "selection", 1),
            "spin-glass" => ("signed-interaction-graphs", "final", 1),
            "inventory-risk" => ("stochastic-inventory-control", "final", 128),
            "robust-design" => ("nonlinear-robust-constraints", "final", 16),
            _ => throw new ArgumentException("Unregistered suite objective.", nameof(id))
        };
        Id = id; _seed = instanceSeed;
        var random = new StableRandom(instanceSeed);
        _coefficients = Enumerable.Range(0, 128).Select(_ => random.NextDouble()).ToArray();
        VersionHash = EvolutionHash.Combine(new[] { "representative-numeric-v1", id, Family, Partition,
            instanceSeed.ToString(CultureInfo.InvariantCulture), WorkUnits.ToString(CultureInfo.InvariantCulture) });
    }
    internal string Id { get; }
    internal string Family { get; }
    internal string Partition { get; }
    internal int WorkUnits { get; }
    internal string VersionHash { get; }

    internal (double Loss, double Violation) Evaluate(IReadOnlyList<double> coordinates, int evaluationOrdinal)
    {
        if (coordinates.Count != 8 || coordinates.Any(value => !double.IsFinite(value) || value < -5 || value > 5) || evaluationOrdinal < 0)
            throw new ArgumentException("Expected eight bounded coordinates and a nonnegative evaluation ordinal.");
        double[] x = coordinates.Select(value => (value + 5) / 10).ToArray();
        var noise = StableRandom.CreateStream(_seed, (ulong)evaluationOrdinal + 1);
        double loss = 0, violation = 0;
        switch (Id)
        {
            case "block-trap":
                for (int block = 0; block < 2; block++)
                {
                    int ones = 0;
                    for (int j = 0; j < 4; j++) if ((x[block * 4 + j] >= 0.5) == (_coefficients[block * 4 + j] >= 0.5)) ones++;
                    loss += ones == 4 ? 0 : ones + 1;
                }
                break;
            case "rastrigin":
                for (int j = 0; j < 8; j++)
                {
                    double z = 10 * (x[j] - _coefficients[j]);
                    loss += z * z + 10 * (1 - Math.Cos(2 * Math.PI * z));
                }
                break;
            case "knapsack":
                double weight = 0;
                for (int j = 0; j < 8; j++)
                {
                    if (x[j] >= 0.5) { weight += 0.25 + _coefficients[j]; loss -= 0.25 + _coefficients[j + 8]; }
                }
                violation = Math.Max(0, weight - 3);
                break;
            case "stochastic-regression":
                for (int sample = 0; sample < 16; sample++)
                {
                    double residual = (noise.NextDouble() - 0.5) * 0.2;
                    for (int j = 0; j < 8; j++) residual += (x[j] - _coefficients[j]) * (2 * _coefficients[8 + sample * 7 % 112 + j] - 1);
                    loss += residual * residual / 16;
                }
                break;
            case "diffusion-control":
                var state = _coefficients.Take(8).ToArray();
                var next = new double[8];
                for (int step = 0; step < 256; step++)
                {
                    for (int j = 0; j < 8; j++) next[j] = state[j] + 0.1 * (state[(j + 7) % 8] - 2 * state[j] + state[(j + 1) % 8]) + 0.01 * (x[j] - state[j]);
                    (state, next) = (next, state);
                }
                for (int j = 0; j < 8; j++) loss += Math.Pow(state[j] - _coefficients[j + 8], 2);
                break;
            case "coupled-absolute":
                for (int j = 0; j < 8; j++) loss += Math.Abs(x[j] + x[(j + 1) % 8] - _coefficients[j] - _coefficients[j + 8]);
                break;
            case "spin-glass":
                for (int j = 0; j < 8; j++)
                    for (int k = j + 1; k < 8; k++) loss -= (2 * _coefficients[j * 8 + k] - 1) * (x[j] >= 0.5 ? 1 : -1) * (x[k] >= 0.5 ? 1 : -1);
                break;
            case "inventory-risk":
                for (int scenario = 0; scenario < 128; scenario++)
                {
                    double stock = 0;
                    for (int period = 0; period < 8; period++)
                    {
                        stock += x[period] - (_coefficients[period] + noise.NextDouble()) / 2;
                        loss += (Math.Max(0, stock) + 3 * Math.Max(0, -stock)) / 128;
                    }
                }
                break;
            case "robust-design":
                for (int scenario = 0; scenario < 16; scenario++)
                {
                    double stress = 0;
                    for (int j = 0; j < 8; j++) stress += _coefficients[scenario * 8 + j] / (0.25 + x[j]);
                    violation = Math.Max(violation, Math.Max(0, stress - 6.4));
                }
                loss = x.Sum(value => value * value);
                break;
        }
        return (loss, violation);
    }
}
