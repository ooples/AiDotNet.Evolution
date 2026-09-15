using System.Diagnostics;
using System.Collections.Concurrent;

namespace AiDotNet.Evolution.Quality;

// Trusted, finite local workloads. No generated source is compiled or executed.
internal sealed class AblationWorkload
{
    internal static readonly string[] Names = ["x0", "x1", "x2", "x3"];
    private readonly string _family;
    private readonly bool _confirmation;
    private readonly bool _portfolio;
    internal string Id { get; }
    internal sealed record Observation(string Genome, double Quality, double[] TimingsMilliseconds, long PrimitiveCases);
    internal ConcurrentQueue<Observation> Observations { get; } = new();

    internal AblationWorkload(string family, string partition, bool portfolio = false)
    {
        if (family is not ("numeric" or "program" or "kernel") || partition is not ("development" or "confirmation"))
            throw new ArgumentException("Unknown workload.");
        _family = family; _confirmation = partition == "confirmation"; _portfolio = portfolio;
        Id = family switch
        {
            "numeric" => _confirmation ? "coupled-absolute" : "rippled-quadratic",
            "program" => _confirmation ? "symbolic-absolute-sine" : "symbolic-quadratic-cosine",
            _ => _confirmation ? "blocked-distance-matrix" : "blocked-matrix-product"
        };
        if (portfolio) Id = family switch
        {
            "numeric" => _confirmation ? "maximum-coupled-square" : "rosenbrock-chain",
            "program" => _confirmation ? "symbolic-frequency-absolute" : "symbolic-quartic-cosine",
            _ => _confirmation ? "blocked-l1-distance" : "blocked-gram-product"
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
        Observations.Enqueue(new(genome.Identity, quality, [], _family == "program" ? 2048 : 4));
        return quality;
    }

    private double Numeric(double[] x)
    {
        if (_portfolio)
        {
            double objective = _confirmation
                ? Enumerable.Range(0, 4).Max(i => Math.Pow(x[i] + 0.4 * x[(i + 1) % 4] - 0.3, 2))
                : Enumerable.Range(0, 3).Sum(i => 5 * Math.Pow(x[i + 1] - x[i] * x[i], 2) + Math.Pow(0.7 - x[i], 2));
            return 1 / (1 + objective);
        }
        double loss = _confirmation
            ? Enumerable.Range(0, 4).Sum(i => Math.Abs(x[i] + 0.35 * x[(i + 1) % 4] - 0.2))
            : x.Sum(v => v * v + 0.1 * (1 - Math.Cos(12 * v)));
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
            double target = _confirmation ? 0.7 * Math.Abs(x) - 0.4 * Math.Sin(x) : 0.6 * x * x + 0.3 * Math.Cos(x);
            if (_portfolio) target = _confirmation ? 0.5 * Math.Abs(x) + 0.4 * Math.Cos(2 * x) : 0.2 * Math.Pow(x, 4) + 0.4 * Math.Cos(x);
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
            times.Add(clock.Elapsed.TotalMilliseconds);
        }
        double quality = 1 / (1 + times.Skip(1).OrderBy(value => value).ElementAt(1));
        Observations.Enqueue(new(identity, quality, times.ToArray(), 5L * size * size * size));
        return quality;
    }
    private double Term(double[,] a, double[,] b, int i, int j, int k) => _portfolio
        ? (_confirmation ? Math.Abs(a[i, k] - b[j, k]) : a[i, k] * a[j, k])
        : (_confirmation ? Math.Pow(a[i, k] - b[j, k], 2) : a[i, k] * b[k, j]);
    private static int Tile(double x) => Math.Clamp((int)Math.Round((x + 1) * 3.5) + 1, 1, 8);
    private static int Basis(double x) => Math.Clamp((int)Math.Round((x + 1) * 1.5), 0, 3);
    private static double EvaluateBasis(int basis, double x) => basis switch { 0 => x * x, 1 => Math.Cos(x), 2 => Math.Abs(x), _ => Math.Sin(x) };
}
