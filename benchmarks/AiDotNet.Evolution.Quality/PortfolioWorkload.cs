using System.Diagnostics;
using System.Collections.Concurrent;

namespace AiDotNet.Evolution.Quality;

// Trusted, finite local workloads. No generated source is compiled or executed.
internal sealed class PortfolioWorkload
{
    internal static readonly string[] Names = ["x0", "x1", "x2", "x3"];
    private readonly string _family;
    private readonly bool _confirmation;
    private readonly double[] _instance;
    internal string Id { get; }
    internal sealed record Observation(string Genome, double Quality, double[] TimingsMilliseconds, long PrimitiveCases, double[] Descriptors);
    internal ConcurrentQueue<Observation> Observations { get; } = new();

    internal PortfolioWorkload(string family, string partition, ulong seed = 0)
    {
        if (family is not ("numeric" or "program" or "kernel") || partition is not ("development" or "confirmation"))
            throw new ArgumentException("Unknown workload.");
        _family = family; _confirmation = partition == "confirmation";
        var random = StableRandom.CreateStream(seed, 19817);
        _instance = Enumerable.Range(0, 4).Select(_ => 0.4 * random.NextDouble() - 0.2).ToArray();
        Id = family switch
        {
            "numeric" => _confirmation ? "portfolio-v2-shifted-coupled-absolute" : "portfolio-v2-shifted-rippled-quadratic",
            "program" => _confirmation ? "portfolio-v2-symbolic-shifted-absolute-cosine" : "portfolio-v2-symbolic-shifted-square-sine",
            _ => _confirmation ? "portfolio-v2-weighted-l1-matrix" : "portfolio-v2-weighted-matrix-product"
        };
    }

    internal EvolutionSearchGenome Normalize(EvolutionSearchSpace space, EvolutionSearchGenome genome)
    {
        // Quantized implementation parameters must share an identity; unused float bits cannot buy diversity.
        return space.CreateGenome(Names.Select((name, index) =>
        {
            double value = genome.Number(name);
            if (_family == "kernel") value = index == 3 ? (value < 0 ? -1 : 1) : (Tile(value) - 1) / 3.5 - 1;
            if (_family == "program" && index >= 2) value = Basis(value) / 1.5 - 1;
            return new KeyValuePair<string, EvolutionParameterValue>(name, EvolutionParameterValue.Numeric(value));
        }));
    }

    internal double Measure(EvolutionSearchGenome genome)
    {
        double[] x = Names.Select(genome.Number).ToArray();
        if (_family == "kernel") return Kernel(genome.Identity, x);
        double quality = _family switch
        {
            "numeric" => Numeric(x),
            _ => Program(x)
        };
        Observations.Enqueue(new(genome.Identity, quality, [], _family == "program" ? 2048 : 4, x.Take(2).ToArray()));
        return quality;
    }

    private double Numeric(double[] x)
    {
        double loss = _confirmation
            ? Enumerable.Range(0, 4).Sum(i => Math.Abs(x[i] + 0.4 * x[(i + 1) % 4] - _instance[i]))
            : Enumerable.Range(0, 4).Sum(i => Math.Pow(x[i] - _instance[i], 2) + 0.08 * (1 - Math.Cos(13 * (x[i] - _instance[i]))));
        return 1 / (1 + loss);
    }

    private double Program(double[] parameters)
    {
        // Two-node weighted expression grammar: coefficients [-2,2], basis {square, cos, abs, sin}.
        // Every evaluator actually interprets 2048 independent inputs; no numeric cost multiplier substitutes for work.
        double loss = 0;
        for (int i = 0; i < 2048; i++)
        {
            double x = -2 + 4 * (i + 0.5) / 2048;
            double target = _confirmation ? (0.65 + _instance[0]) * Math.Abs(x - _instance[1]) - 0.35 * Math.Cos(2.1 * x)
                : (0.35 + _instance[0]) * Math.Pow(x + _instance[1], 2) + 0.2 * Math.Sin(1.9 * x);
            double prediction = 2 * parameters[0] * EvaluateBasis(Basis(parameters[2]), x) +
                2 * parameters[1] * EvaluateBasis(Basis(parameters[3]), x);
            loss += Math.Pow(prediction - target, 2);
        }
        return 1 / (1 + loss / 2048);
    }

    private double Kernel(string identity, double[] parameters)
    {
        const int size = 24;
        var a = new double[size, size]; var b = new double[size, size];
        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++) { a[i, j] = (i + 2 * j) % 7; b[i, j] = (2 * i + j) % 5; }
        var expected = new double[size, size];
        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++)
                for (int k = 0; k < size; k++) expected[i, j] += Term(a, b, i, j, k);
        // Fixed one warmup plus three fresh measurements. Correctness is checked on every invocation.
        // Setup, warmup, and checks all count inside the same evaluator-call budget and elapsed run cost.
        var times = new List<double>();
        for (int repeat = 0; repeat < 4; repeat++)
        {
            var result = new double[size, size];
            var clock = Stopwatch.StartNew();
            int ti = Tile(parameters[0]), tj = Tile(parameters[1]), tk = Tile(parameters[2]);
            for (int ii = 0; ii < size; ii += ti)
                for (int jj = 0; jj < size; jj += tj)
                    for (int kk = 0; kk < size; kk += tk)
                        if (parameters[3] < 0)
                        {
                            for (int i = ii; i < Math.Min(size, ii + ti); i++)
                                for (int j = jj; j < Math.Min(size, jj + tj); j++)
                                    for (int k = kk; k < Math.Min(size, kk + tk); k++) result[i, j] += Term(a, b, i, j, k);
                        }
                        else
                        {
                            for (int i = ii; i < Math.Min(size, ii + ti); i++)
                                for (int k = kk; k < Math.Min(size, kk + tk); k++)
                                    for (int j = jj; j < Math.Min(size, jj + tj); j++) result[i, j] += Term(a, b, i, j, k);
                        }
            clock.Stop();
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    if (result[i, j] != expected[i, j]) throw new InvalidDataException("Kernel correctness failure.");
            double elapsed = clock.Elapsed.TotalMilliseconds;
            if (!double.IsFinite(elapsed) || elapsed <= 0) throw new InvalidDataException("Invalid kernel timing.");
            times.Add(elapsed);
        }
        double quality = 1 / (1 + times.Skip(1).OrderBy(value => value).ElementAt(1));
        Observations.Enqueue(new(identity, quality, times.ToArray(), 5L * size * size * size, parameters.Take(2).ToArray()));
        return quality;
    }
    private double Term(double[,] a, double[,] b, int i, int j, int k) => (1 + k % 5) *
        (_confirmation ? Math.Abs(a[i, k] - b[j, k]) : a[i, k] * b[k, j]);
    private static int Tile(double x) => Math.Clamp((int)Math.Round((x + 1) * 3.5) + 1, 1, 8);
    private static int Basis(double x) => Math.Clamp((int)Math.Round((x + 1) * 1.5), 0, 3);
    private static double EvaluateBasis(int basis, double x) => basis switch { 0 => x * x, 1 => Math.Cos(x), 2 => Math.Abs(x), _ => Math.Sin(x) };
}
