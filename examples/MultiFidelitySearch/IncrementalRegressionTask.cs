using System.Text.Json;
using AiDotNet.Evolution;

namespace MultiFidelitySearch;

/// <summary>An authored, actually trained regression workload; not an AiDotNet AutoML or real-world dataset benchmark.</summary>
internal sealed class IncrementalRegressionTask
{
    internal const string StateVersion = "regression-gd-state-v1";
    internal const int Dimensions = 4;
    private const int TrainingRows = 128, ValidationRows = 64;
    private static readonly double[] Truth = { 0.2, -0.4, 0.6, 0.8 };
    private readonly bool _enableContinuation;
    internal IncrementalRegressionTask(bool enableContinuation = true) => _enableContinuation = enableContinuation;
    internal long Epochs { get; private set; }
    internal long TrainingRowVisits { get; private set; }
    internal long ValidationRowVisits { get; private set; }
    internal int Calls { get; private set; }
    internal int ResumedCalls { get; private set; }
    internal int ConfirmationCalls { get; private set; }
    internal List<object> Measurements { get; } = new();

    internal ValueTask<EvolutionFidelityEvaluationResult> Evaluate(EvolutionSearchGenome genome,
        EvolutionFidelityEvaluationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool confirmation = context.Replicate.Purpose == EvolutionReplicationPurpose.Confirmation;
        if (confirmation && (context.Resume is not null || context.Level.ResourceLevel != 64))
            throw new InvalidOperationException("Confirmation must independently retrain at full fidelity.");
        if (!_enableContinuation && context.Resume is not null) throw new InvalidOperationException("Restart-only evaluation cannot consume continuation state.");
        if (context.Level.ResourceLevel is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(context));
        // The data/initialization stream stays fixed across fidelities of a replicate. Search and confirmation
        // have different data AND initialization; all methods share the same predeclared seed pairing.
        ulong stream = (confirmation ? 100_000UL : 1_000UL) + (ulong)context.Replicate.Index * 10;
        var training = Data(StableRandom.CreateStream(context.Replicate.EvaluationContext.RootSeed, stream), TrainingRows);
        var validation = Data(StableRandom.CreateStream(context.Replicate.EvaluationContext.RootSeed, stream + 1), ValidationRows);
        string dataIdentity = EvolutionHash.Combine(new[] { "regression-data-v1", HashData(training), HashData(validation) });
        double[] weights; int completed = 0;
        if (context.Resume is { } resume)
        {
            weights = DecodeState(resume.CopyToken(), genome.Identity, dataIdentity, context.Replicate.Index,
                checked((int)resume.SourceLevel.ResourceLevel), out completed);
            if (completed >= context.Level.ResourceLevel) throw new InvalidOperationException("Continuation must advance the resource level.");
            ResumedCalls++;
        }
        else
        {
            var initial = StableRandom.CreateStream(context.Replicate.EvaluationContext.RootSeed, stream + 2);
            weights = Enumerable.Range(0, Dimensions + 1).Select(_ => (initial.NextDouble() - 0.5) * 0.1).ToArray();
        }
        int actualEpochs = checked((int)context.Level.ResourceLevel) - completed;
        Train(weights, training, genome.Number("learning-rate"), checked((int)genome.Number("warmup")),
            completed, checked((int)context.Level.ResourceLevel), cancellationToken);
        Epochs += actualEpochs; TrainingRowVisits += (long)actualEpochs * TrainingRows;
        double mse = MeanSquaredError(weights, validation);
        double quality = 1 / (1 + mse);
        Calls++; ValidationRowVisits += ValidationRows; if (confirmation) ConfirmationCalls++;
        // One unit per actual full-batch epoch, plus a fixed declared tariff covering data regeneration,
        // held-out scoring and token bookkeeping. These prices are work assumptions, not elapsed CPU/dollars.
        double cost = actualEpochs + 0.25;
        Measurements.Add(new
        {
            SampleIdentity = context.Replicate.SampleIdentity,
            GenomeId = genome.Identity,
            Purpose = context.Replicate.Purpose.ToString(),
            context.Replicate.Index,
            Level = context.Level.ResourceLevel,
            ResumedFrom = context.Resume?.SourceSampleIdentity,
            DataIdentity = dataIdentity,
            CompletedEpochsBefore = completed,
            ActualEpochs = actualEpochs,
            TrainingRowVisits = actualEpochs * TrainingRows,
            ValidationRowVisits = ValidationRows,
            MeanSquaredError = mse,
            Quality = quality,
            CostUnits = cost,
            WeightsHash = HashWeights(weights)
        });
        return new(new EvolutionFidelityEvaluationResult(EvolutionTaskResult.Completed(quality,
            new Dictionary<string, double> { ["mse"] = mse }, costUnits: cost), confirmation || !_enableContinuation ? null : EncodeState(
                genome.Identity, dataIdentity, context.Replicate.Index, checked((int)context.Level.ResourceLevel), weights),
            confirmation || !_enableContinuation ? null : StateVersion));
    }

    private static double[][] Data(StableRandom random, int count)
    {
        var rows = new double[count][];
        for (int i = 0; i < count; i++)
        {
            var row = new double[Dimensions + 1]; double target = 0.1;
            for (int d = 0; d < Dimensions; d++) { row[d] = random.NextDouble() * 2 - 1; target += row[d] * Truth[d]; }
            row[Dimensions] = target + (random.NextDouble() - 0.5) * 0.1; rows[i] = row;
        }
        return rows;
    }

    private static void Train(double[] weights, double[][] rows, double rate, int warmup, int start, int end, CancellationToken token)
    {
        for (int epoch = start; epoch < end; epoch++)
        {
            token.ThrowIfCancellationRequested();
            var gradient = new double[Dimensions + 1];
            foreach (var row in rows)
            {
                double error = Predict(weights, row) - row[Dimensions];
                for (int d = 0; d < Dimensions; d++) gradient[d] += 2 * error * row[d] / rows.Length;
                gradient[Dimensions] += 2 * error / rows.Length;
            }
            double effectiveRate = epoch < warmup ? rate / 32 : rate;
            for (int d = 0; d < weights.Length; d++) weights[d] -= effectiveRate * gradient[d];
        }
    }
    private static double Predict(double[] weights, double[] row)
    {
        double value = weights[Dimensions];
        for (int d = 0; d < Dimensions; d++) value += weights[d] * row[d];
        return value;
    }
    private static double MeanSquaredError(double[] weights, double[][] rows) => rows.Average(row => Math.Pow(Predict(weights, row) - row[Dimensions], 2));
    private static string HashWeights(IEnumerable<double> weights) => EvolutionHash.Combine(weights.Select(EvolutionHash.EncodeDouble));
    private static string HashData(IEnumerable<double[]> rows) => EvolutionHash.Combine(rows.Select(HashWeights));
    private static byte[] EncodeState(string genome, string data, int replicate, int epochs, double[] weights) =>
        JsonSerializer.SerializeToUtf8Bytes(new TrainingState
        {
            Version = StateVersion,
            Genome = genome,
            Data = data,
            Replicate = replicate,
            Epochs = epochs,
            Weights = (double[])weights.Clone(),
            WeightsHash = HashWeights(weights)
        });

    private static double[] DecodeState(byte[] payload, string genome, string data, int replicate, int epochs, out int completed)
    {
        if (payload.Length > 4096) throw new InvalidOperationException("Training state exceeds its token bound.");
        var state = JsonSerializer.Deserialize<TrainingState>(payload) ?? throw new InvalidOperationException("Missing training state.");
        if (state.Version != StateVersion || state.Genome != genome || state.Data != data || state.Replicate != replicate || state.Epochs != epochs ||
            state.Epochs is < 1 or > 64 || state.Weights is null || state.Weights.Length != Dimensions + 1 ||
            state.Weights.Any(value => !double.IsFinite(value) || Math.Abs(value) > 10) || state.WeightsHash != HashWeights(state.Weights))
            throw new InvalidOperationException("Training state provenance or weights differ.");
        completed = state.Epochs; return (double[])state.Weights.Clone();
    }

    internal static void VerifyTraining()
    {
        var rows = Data(new StableRandom(42), TrainingRows);
        double[] initial = new double[Dimensions + 1], continuous = (double[])initial.Clone(), split = (double[])initial.Clone();
        Train(continuous, rows, 0.2, 8, 0, 64, default);
        Train(split, rows, 0.2, 8, 0, 16, default);
        byte[] payload = EncodeState("genome", "data", 1, 16, split);
        split = DecodeState(payload, "genome", "data", 1, 16, out int completed);
        Train(split, rows, 0.2, 8, completed, 64, default);
        if (HashWeights(split) != HashWeights(continuous) || MeanSquaredError(continuous, rows) >= MeanSquaredError(initial, rows))
            throw new InvalidOperationException("Real gradient descent did not learn or resume exactly.");
        foreach (int change in Enumerable.Range(0, 5))
        {
            bool rejected = false;
            try
            {
                DecodeState(payload, change == 0 ? "other" : "genome", change == 1 ? "other" : "data", change == 2 ? 2 : 1, change == 3 ? 15 : 16, out _);
                if (change == 4)
                {
                    var corrupted = JsonSerializer.Deserialize<TrainingState>(payload)!; corrupted.Weights![0] += 1;
                    DecodeState(JsonSerializer.SerializeToUtf8Bytes(corrupted), "genome", "data", 1, 16, out _);
                }
            }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Training state corruption was accepted.");
        }
        Console.WriteLine("Real regression training verified: gradient descent learns, split-token continuation is exact, five provenance/weight corruptions rejected.");
    }
    private sealed class TrainingState
    {
        public string? Version { get; set; }
        public string? Genome { get; set; }
        public string? Data { get; set; }
        public int Replicate { get; set; }
        public int Epochs { get; set; }
        public double[]? Weights { get; set; }
        public string? WeightsHash { get; set; }
    }
}
