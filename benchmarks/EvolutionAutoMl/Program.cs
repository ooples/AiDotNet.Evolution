// US-11 (#29): the engine tuning a real model's hyperparameters, against a grid at equal budget.
//
// The criterion asks for an AutoML example evaluated with independent data splits and a strong
// domain baseline. Both halves are literal here:
//
// - **Independent splits.** Train, validation and test are drawn from disjoint seeds. Selection -
//   the engine's and the grid's alike - sees train and validation only. The test split is sealed by
//   SHA-256 before either searcher starts and scored once each, at the end, after the seal is
//   re-verified. A run whose test rows no longer match its seal is refused rather than reported.
// - **A strong baseline at equal budget.** Exhaustive grid search over the same space, given the
//   same number of model fits. Grid search is the honest comparator for hyperparameter tuning: with
//   a budget this size it is not a straw man, and on these datasets it is hard to beat.
//
// No model calls and no LLM: the workload is a real `RidgeRegression` from the consumer package.
extern alias AiDotNetConsumer;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution;
using AiDotNet.Tensors.LinearAlgebra;
using AiDotNetConsumer::AiDotNet.Models.Options;
using AiDotNetConsumer::AiDotNet.Regression;

internal static class Program
{
    internal const string Protocol = "automl-tuning-v1";
    private static readonly ulong[] Seeds = { 7001, 7002, 7003, 7004, 7005, 7006, 7007, 7008 };
    private const int TrainRows = 120, ValidationRows = 60, TestRows = 400, Features = 4;
    private const int Budget = 36;                 // model fits per searcher per seed
    private const ulong TestSeedOffset = 7919;     // a prime, so no split shares a grid with another
    private const ulong ValidationSeedOffset = 104_729;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--output"))
            throw new ArgumentException("Usage: EvolutionAutoMl [--output new-rows.json]");
        using var stream = args.Length == 2 ? new FileStream(args[1], FileMode.CreateNew, FileAccess.Write) : null;

        var rows = new List<Row>();
        foreach (ulong seed in Seeds) rows.Add(await MeasureAsync(seed).ConfigureAwait(false));
        foreach (Row row in rows)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "seed={0} engine test_mse={1:F6} grid test_mse={2:F6} ratio={3:F4} engine_alpha={4:G4} grid_alpha={5:G4} fits={6}/{7}",
                row.Seed, row.EngineTestMse, row.GridTestMse, row.TestMseRatio, row.EngineAlpha, row.GridAlpha,
                row.EngineFits, row.GridFits));

        var report = new Report(Protocol, Features, TrainRows, ValidationRows, TestRows, Budget, Seeds, rows.ToArray());
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (stream is not null)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
            Console.WriteLine("wrote " + args[1]);
        }
        return 0;
    }

    internal sealed record Row(ulong Seed, string TestSeal, double EngineTestMse, double GridTestMse,
        double TestMseRatio, double EngineValidationMse, double GridValidationMse, double EngineAlpha,
        double GridAlpha, bool EngineIntercept, bool GridIntercept, int EngineFits, int GridFits);

    internal sealed record Report(string Protocol, int Features, int TrainRows, int ValidationRows, int TestRows,
        int Budget, ulong[] Seeds, Row[] Rows);

    /// <summary>Deterministic rows: a sparse linear signal with a nonlinear term and bounded noise.</summary>
    /// <remarks>
    /// Ridge cannot fit the nonlinear term, so the best reachable error is bounded away from zero and
    /// the tuning problem has a genuine interior optimum rather than "use the smallest alpha".
    /// </remarks>
    private static (Matrix<double> X, Vector<double> Y) Draw(ulong seed, int rows)
    {
        var random = StableRandom.CreateStream(seed, 31UL);
        var x = new Matrix<double>(rows, Features);
        var y = new Vector<double>(rows);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < Features; column++) x[row, column] = random.NextDouble() * 2 - 1;
            y[row] = 1.5 * x[row, 0] - 0.75 * x[row, 1] + 0.5 * x[row, 2] * x[row, 2]
                     + (random.NextDouble() * 2 - 1) * 0.15;
        }
        return (x, y);
    }

    private static string Seal(Matrix<double> x, Vector<double> y)
    {
        var text = new StringBuilder();
        for (int row = 0; row < y.Length; row++)
        {
            for (int column = 0; column < Features; column++)
                text.Append(x[row, column].ToString("R", CultureInfo.InvariantCulture)).Append(',');
            text.Append(y[row].ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>Mean squared error of a freshly trained model, which is the only thing either searcher sees.</summary>
    private static double Fit(double alpha, bool intercept, Matrix<double> trainX, Vector<double> trainY,
                              Matrix<double> scoreX, Vector<double> scoreY)
    {
        var model = new RidgeRegression<double>(new RidgeRegressionOptions<double> { Alpha = alpha, UseIntercept = intercept });
        model.Train(trainX, trainY);
        Vector<double> predicted = model.Predict(scoreX);
        double total = 0;
        for (int row = 0; row < scoreY.Length; row++)
        {
            double error = predicted[row] - scoreY[row];
            total += error * error;
        }
        double mse = total / scoreY.Length;
        if (!double.IsFinite(mse)) throw new InvalidOperationException("The model produced a nonfinite error.");
        return mse;
    }

    private static async Task<Row> MeasureAsync(ulong seed)
    {
        (Matrix<double> trainX, Vector<double> trainY) = Draw(seed, TrainRows);
        (Matrix<double> validationX, Vector<double> validationY) = Draw(seed + ValidationSeedOffset, ValidationRows);
        (Matrix<double> testX, Vector<double> testY) = Draw(seed + TestSeedOffset, TestRows);
        // Sealed before either searcher runs, so neither can be blamed for the rows it is scored on.
        string seal = Seal(testX, testY);

        EvolutionSearchSpace space = new EvolutionSearchSpaceBuilder()
            .Add(EvolutionParameter.Logarithmic("alpha", 1e-4, 1e2))
            .Add(EvolutionParameter.Categorical("intercept", new[] { "yes", "no" }))
            .Build();

        int engineFits = 0;
        var task = new EvolutionSearchTask(space, "ridge-automl", Protocol, Protocol, (genome, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            engineFits++;
            // Validation only. The test rows are not in scope here, by construction rather than by rule.
            double mse = Fit(genome.Number("alpha"), genome.Category("intercept") == "yes",
                             trainX, trainY, validationX, validationY);
            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
                1 / (1 + mse), new Dictionary<string, double> { ["alpha"] = Math.Log10(genome.Number("alpha")) }, costUnits: 1));
        });
        var ledger = new EvolutionResourceLedger("automl-" + seed, EvolutionResources.Of("cost_units", Budget));
        var engine = new EvolutionEngine<EvolutionSearchGenome>(
            new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(task, ledger, new[] { 1m }),
            EvolutionSearchPresets.CreateAdaptiveMixed(space),
            _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("alpha", -4, 2, 6) }),
            new EvolutionEngineOptions
            {
                RunId = "automl-" + seed.ToString(CultureInfo.InvariantCulture),
                Seed = seed,
                MaxEvaluationAttempts = Budget,
                MaxProposals = Budget * 10,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                InspirationCount = 2,
                MigrationInterval = 0
            });
        var initial = new[] { space.Sample(StableRandom.CreateStream(seed, 1UL)), space.Sample(StableRandom.CreateStream(seed, 2UL)) };
        EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(initial).ConfigureAwait(false);
        EvolutionArchiveEntry<EvolutionSearchGenome>? best = result.Best
            ?? throw new InvalidOperationException("The engine returned no candidate, so there is nothing to compare.");
        EvolutionSearchGenome winner = best.Candidate.CanonicalGenome.Genome;
        double engineAlpha = winner.Number("alpha");
        bool engineIntercept = winner.Category("intercept") == "yes";

        // The baseline: an exhaustive grid over the same space, given the same number of fits. Half the
        // budget per intercept setting, log-spaced across the same alpha range the engine searched.
        int perSetting = Budget / 2;
        double bestGridMse = double.PositiveInfinity, gridAlpha = double.NaN;
        bool gridIntercept = false;
        int gridFits = 0;
        foreach (bool intercept in new[] { true, false })
            for (int step = 0; step < perSetting; step++)
            {
                double alpha = Math.Pow(10, -4 + 6.0 * step / (perSetting - 1));
                gridFits++;
                double mse = Fit(alpha, intercept, trainX, trainY, validationX, validationY);
                if (mse < bestGridMse) { bestGridMse = mse; gridAlpha = alpha; gridIntercept = intercept; }
            }

        // Scored once each, after re-verifying the seal: a tampered test split is a refusal, not a number.
        if (Seal(testX, testY) != seal) throw new InvalidOperationException("The test split no longer matches its seal.");
        double engineTest = Fit(engineAlpha, engineIntercept, trainX, trainY, testX, testY);
        double gridTest = Fit(gridAlpha, gridIntercept, trainX, trainY, testX, testY);
        double engineValidation = Fit(engineAlpha, engineIntercept, trainX, trainY, validationX, validationY);

        return new Row(seed, seal, engineTest, gridTest, engineTest / gridTest, engineValidation, bestGridMse,
            engineAlpha, gridAlpha, engineIntercept, gridIntercept, engineFits, gridFits);
    }
}
