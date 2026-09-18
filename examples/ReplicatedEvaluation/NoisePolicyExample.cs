using System.Text.Json;
using AiDotNet.Evolution;

internal static class NoisePolicyExample
{
    internal static async Task<int> RunAsync()
    {
        var ledger = new EvolutionResourceLedger("noise-policy-example", EvolutionResources.Of("cost_units", 1000));
        int searchCalls = 0, confirmationCalls = 0, auditCalls = 0, timingCalls = 0;
        var identities = new HashSet<string>(StringComparer.Ordinal);
        double Noise(EvolutionReplicateContext context)
        {
            if (!identities.Add(context.SampleIdentity)) throw new InvalidOperationException("A sample was reused.");
            return context.EvaluationContext.CreateRandom().NextDouble() * .05;
        }
        EvolutionTaskResult Measured(double value) => EvolutionTaskResult.Completed(value, new Dictionary<string, double>(), costUnits: 1);
        var challenge = new EvolutionIncumbentChallenge<int>("bounded-search-v1", "bounded-confirmation-v1", ledger,
            4, 64, 2, 0, 1, .1, 1,
            (genome, context, _) => { searchCalls++; return new(Measured((genome == 0 ? .1 : .85) + Noise(context))); },
            (genome, context, _) => { confirmationCalls++; return new(Measured((genome == 1 ? .85 : .1) + Noise(context))); });
        var incumbent = new EvolutionCanonicalGenome<int>(0, "incumbent");
        var context = new EvolutionEvaluationContext(0, 42, 17, 1);
        var realImprovement = await challenge.RunAsync(0, new(1, "improved"), incumbent, context);
        var luckySearch = await challenge.RunAsync(1, new(2, "search-only-improvement"), incumbent, context);
        var audit = new EvolutionRejectionAudit<int>("full-fidelity-v1", ledger, 4, 64, 0, 1, .5, 1,
            (genome, sample, _) => { auditCalls++; return new(Measured((genome % 2 == 0 ? .1 : .85) + Noise(sample))); });
        var auditReport = await audit.RunAsync("frozen-screen-preset", Enumerable.Range(0, 8)
            .Select(index => new EvolutionCanonicalGenome<int>(index, "rejected-" + index)).ToArray(), 917);
        var timing = new EvolutionTimingProtocol(2, 1000);
        var timingRows = new List<EvolutionTaskResult>();
        var timingRunner = new EvolutionReplicateRunner<int>(timing.VersionHash,
            new EvolutionReplicationPlan(3, 3, 0, 1000, 3, direction: EvolutionOptimizationDirection.Minimize), ledger,
            async (_, _, token) =>
            {
                var row = await timing.MeasureAsync(_ =>
                {
                    timingCalls++;
                    int[] values = Enumerable.Range(0, 512).Select(value => 511 - value).ToArray();
                    Array.Sort(values);
                    if (!values.SequenceEqual(Enumerable.Range(0, 512))) throw new InvalidOperationException("Incorrect sort output.");
                    return default;
                }, token);
                timingRows.Add(row); return row;
            });
        var timingReport = await timingRunner.RunAsync(new(0, "trusted-sort"), context, "fixed-timing");
        decimal expected = searchCalls + confirmationCalls + auditCalls + timingCalls;
        bool valid = realImprovement.IsConfirmed && !luckySearch.IsConfirmed && luckySearch.CandidateConfirmation is not null &&
            searchCalls == 16 && confirmationCalls == 256 && auditCalls == 256 && timingCalls == 9 &&
            timingReport.IsComplete && ledger.Snapshot().Spent["cost_units"] == expected &&
            auditReport.Entries.All(row => row.FullEvaluation?.IsComplete == true);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Kind = "bounded-noise-policy-contract",
            Valid = valid,
            SearchCalls = searchCalls,
            ConfirmationCalls = confirmationCalls,
            AuditCalls = auditCalls,
            TimingCalls = timingCalls,
            Ledger = ledger.Snapshot(),
            RealImprovement = realImprovement,
            LuckySearch = luckySearch,
            RejectionAudit = auditReport,
            Timing = timingReport,
            TimingObservations = timingRows,
            Interpretation = "Predeclared bounded synthetic noise plus trusted local sorting timing. Not a representative model-training/runtime win, sandbox proof, or production cascade recommendation."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return valid ? 0 : 1;
    }
}
